using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIUsageTracker.Models;

namespace AIUsageTracker.Services.Providers;

public sealed class ClaudeCodeQuotaProvider : IQuotaProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(7)
    };

    private static readonly TimeSpan MinApiPollInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaxCachedApiAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxPlanHistoryAge = TimeSpan.FromMinutes(10);

    private readonly string _cacheFilePath;
    private DateTimeOffset _nextAllowedApiCallUtc = DateTimeOffset.MinValue;
    private string? _lastValidApiJson;
    private DateTimeOffset _lastValidApiTimeUtc;

    public QuotaProviderType ProviderType => QuotaProviderType.ClaudeCode;

    public ClaudeCodeQuotaProvider()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "AIUsageTracker");
        Directory.CreateDirectory(dir);
        _cacheFilePath = Path.Combine(dir, "claude-oauth-cache.json");

        if (File.Exists(_cacheFilePath))
        {
            try
            {
                if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_cacheFilePath) <= MaxCachedApiAge)
                {
                    string candidate = File.ReadAllText(_cacheFilePath);
                    if (IsValidUsageApiResponse(candidate))
                    {
                        _lastValidApiJson = candidate;
                        _lastValidApiTimeUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
                    }
                }
            }
            catch
            {
                // Ignore corrupted cache
            }
        }
    }

    public async Task<IReadOnlyList<QuotaItem>> FetchQuotasAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.EnableClaudeCode)
        {
            return Array.Empty<QuotaItem>();
        }

        // 1. Inspect recent local session logs for session cost, active model, and rate limit events (resetsAt)
        var sessionDetails = TryGetLatestSessionDetails();

        // 2. Inspect plan-usage-history.json written by Claude Desktop / Claude Code
        var planHistory = TryGetLatestPlanUsageHistory();

        // 3. Try live Anthropic OAuth API if credentials are valid and not expired
        string? apiBody = null;

        if (ShouldAttemptApiCall(settings))
        {
            try
            {
                // Proactively refresh token if expired or expiring within 5 minutes
                if (ClaudeAuthService.IsTokenExpiringSoonStatic(settings, TimeSpan.FromMinutes(5)))
                {
                    await ClaudeAuthService.TrySilentTokenRefreshAsync(settings, cancellationToken);
                }

                var apiResult = await TryCallOAuthApiAsync(settings, cancellationToken);
                if (apiResult.Success && IsValidUsageApiResponse(apiResult.Body))
                {
                    apiBody = apiResult.Body;
                    _lastValidApiJson = apiBody;
                    _lastValidApiTimeUtc = DateTimeOffset.UtcNow;
                    _nextAllowedApiCallUtc = DateTimeOffset.UtcNow.Add(MinApiPollInterval);
                    SaveCacheToDisk(apiBody!);
                }
                else if (apiResult.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Token expired: attempt silent refresh in background and retry!
                    bool refreshed = await ClaudeAuthService.TrySilentTokenRefreshAsync(settings, cancellationToken);
                    if (refreshed)
                    {
                        var retryResult = await TryCallOAuthApiAsync(settings, cancellationToken);
                        if (retryResult.Success && IsValidUsageApiResponse(retryResult.Body))
                        {
                            apiBody = retryResult.Body;
                            _lastValidApiJson = apiBody;
                            _lastValidApiTimeUtc = DateTimeOffset.UtcNow;
                            _nextAllowedApiCallUtc = DateTimeOffset.UtcNow.Add(MinApiPollInterval);
                            SaveCacheToDisk(apiBody!);
                        }
                    }
                }
                else if (apiResult.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _nextAllowedApiCallUtc = DateTimeOffset.UtcNow.Add(apiResult.RetryAfter ?? TimeSpan.FromMinutes(2));
                }
            }
            catch
            {
                // Network error, proceed to synthesize with local data
            }
        }

        bool isTokenExpired = ClaudeAuthService.IsTokenExpiredStatic(settings);

        // 4. Synthesize the most accurate, real-time quota items (using apiBody or cached API)
        string? recentCachedApi = DateTimeOffset.UtcNow - _lastValidApiTimeUtc <= MaxCachedApiAge
            ? _lastValidApiJson : null;
        var items = SynthesizeQuotas(settings, planHistory, sessionDetails, apiBody ?? recentCachedApi, isTokenExpired);
        if (items.Count > 0)
        {
            return items;
        }

        // 5. Fallback if no sources found
        string fallbackSubtitle = isTokenExpired
            ? "Token expirado • Clique em Reconectar"
            : (sessionDetails.Subtitle ?? "Nenhum dado recente encontrado");

        return new[]
        {
            new QuotaItem
            {
                Id = "claude.session",
                Provider = QuotaProviderType.ClaudeCode,
                ProviderBadge = "CLAUDE",
                BadgeColorHex = "#D97757",
                Title = "Claude Code (5h)",
                UsedPercent = 0,
                IsAvailable = false,
                Subtitle = fallbackSubtitle,
                SourceDetail = isTokenExpired ? "Claude Code (Token expirado)" : "Claude Desktop / Code",
                SortOrder = 20
            }
        };
    }

    private bool ShouldAttemptApiCall(AppSettings settings)
    {
        if (DateTimeOffset.UtcNow < _nextAllowedApiCallUtc)
        {
            return false;
        }

        string credPath = settings.GetEffectiveClaudeCredentialsPath();
        if (!File.Exists(credPath))
        {
            return false;
        }

        try
        {
            string credJson = File.ReadAllText(credPath);
            using JsonDocument doc = JsonDocument.Parse(credJson);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauthEl))
            {
                bool hasAccessToken = oauthEl.TryGetProperty("accessToken", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString());
                bool hasRefreshToken = oauthEl.TryGetProperty("refreshToken", out JsonElement rEl) && !string.IsNullOrWhiteSpace(rEl.GetString());
                return hasAccessToken || hasRefreshToken;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsValidUsageApiResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (string key in new[] { "five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet" })
            {
                if (doc.RootElement.TryGetProperty(key, out JsonElement window) &&
                    window.ValueKind == JsonValueKind.Object &&
                    window.TryGetProperty("utilization", out JsonElement utilization) &&
                    utilization.ValueKind == JsonValueKind.Number &&
                    utilization.TryGetDouble(out double value) && double.IsFinite(value) &&
                    value >= 0 && value <= 100)
                {
                    return true;
                }
            }
        }
        catch (JsonException) { }

        return false;
    }

    private static async Task<(bool Success, HttpStatusCode? StatusCode, TimeSpan? RetryAfter, string? Body)> TryCallOAuthApiAsync(
        AppSettings settings, CancellationToken cancellationToken)
    {
        string credPath = settings.GetEffectiveClaudeCredentialsPath();
        string credJson = await File.ReadAllTextAsync(credPath, cancellationToken);
        using JsonDocument credDoc = JsonDocument.Parse(credJson);

        string? accessToken = credDoc.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (false, null, null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("claude-code/2.0.0");

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retry = response.Headers.RetryAfter?.Delta;
            return (false, HttpStatusCode.TooManyRequests, retry, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, response.StatusCode, null, null);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (true, HttpStatusCode.OK, null, body);
    }

    private static List<QuotaItem> SynthesizeQuotas(
        AppSettings settings,
        PlanHistorySample? planHistory,
        SessionDetails sessionDetails,
        string? apiBody,
        bool isTokenExpired)
    {
        var items = new List<QuotaItem>();

        // Parse API body if available
        JsonElement apiRoot = default;
        bool hasApiRoot = false;
        if (!string.IsNullOrWhiteSpace(apiBody))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(apiBody);
                apiRoot = doc.RootElement.Clone();
                hasApiRoot = true;
            }
            catch { }
        }

        // --- 1. Five-Hour Session Quota ---
        if (settings.ShowClaudeSession5h)
        {
            double fiveHourUsed = 0.0;
            DateTimeOffset? fiveHourReset = null;
            string source = "Claude Desktop / Code Local State";
            string subtitle = sessionDetails.Subtitle ?? "Janela de sessão de 5 horas";

            if (isTokenExpired)
            {
                subtitle = string.IsNullOrEmpty(subtitle) || subtitle == "Janela de sessão de 5 horas"
                    ? "Token expirado • Clique em Reconectar"
                    : $"{subtitle} • Token expirado";
            }

            // Priority 1: Recent Anthropic OAuth API
            if (hasApiRoot && apiRoot.TryGetProperty("five_hour", out JsonElement fhEl) && fhEl.ValueKind == JsonValueKind.Object)
            {
                fiveHourUsed = fhEl.TryGetProperty("utilization", out JsonElement uEl) && uEl.TryGetDouble(out double u) ? u : 0.0;
                if (fhEl.TryGetProperty("resets_at", out JsonElement rEl) && DateTimeOffset.TryParse(rEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset r))
                {
                    if (r > DateTimeOffset.UtcNow)
                    {
                        fiveHourReset = r;
                    }
                }
                source = "Anthropic OAuth API";

                if (fiveHourUsed >= 100.0 || (sessionDetails.ActiveRateLimitReset.HasValue && sessionDetails.ActiveRateLimitReset.Value > DateTimeOffset.UtcNow))
                {
                    subtitle = $"Limite de sessão atingido • {sessionDetails.ModelName ?? "Claude"}";
                    if (!fiveHourReset.HasValue && sessionDetails.ActiveRateLimitReset.HasValue)
                    {
                        fiveHourReset = sessionDetails.ActiveRateLimitReset.Value;
                    }
                }
            }
            // Priority 2: Session log rate limit event
            else if (sessionDetails.ActiveRateLimitReset.HasValue && sessionDetails.ActiveRateLimitReset.Value > DateTimeOffset.UtcNow)
            {
                fiveHourUsed = 100.0;
                fiveHourReset = sessionDetails.ActiveRateLimitReset.Value;
                source = "Claude Session Log (Limite Atingido)";
                subtitle = $"Limite de sessão atingido • {sessionDetails.ModelName ?? "Claude"}";
            }
            // Priority 3: Claude Desktop plan history
            else if (planHistory != null && planHistory.FiveHourUsedPercent.HasValue)
            {
                fiveHourUsed = planHistory.FiveHourUsedPercent.Value;
                source = "Claude Desktop (plan-usage-history)";

                if (sessionDetails.ActiveRateLimitReset.HasValue && sessionDetails.ActiveRateLimitReset.Value > DateTimeOffset.UtcNow)
                {
                    fiveHourReset = sessionDetails.ActiveRateLimitReset.Value;
                }
                else if (fiveHourUsed >= 99.0)
                {
                    // If 100% and no exact resetsAt found, estimate from sample timestamp + 5 hours
                    fiveHourReset = planHistory.SampleTimeUtc.AddHours(5);
                }
            }

            items.Add(new QuotaItem
            {
                Id = "claude.session",
                Provider = QuotaProviderType.ClaudeCode,
                ProviderBadge = "CLAUDE",
                BadgeColorHex = "#D97757",
                Title = "Claude Code (5h)",
                UsedPercent = Math.Clamp(fiveHourUsed, 0.0, 100.0),
                ResetTime = fiveHourReset,
                Subtitle = subtitle,
                SourceDetail = source,
                IsAvailable = (hasApiRoot && apiRoot.TryGetProperty("five_hour", out JsonElement fiveHourWindow) &&
                    fiveHourWindow.ValueKind == JsonValueKind.Object && fiveHourWindow.TryGetProperty("utilization", out _)) ||
                    (planHistory?.FiveHourUsedPercent.HasValue ?? false) ||
                    (sessionDetails.ActiveRateLimitReset > DateTimeOffset.UtcNow),
                SortOrder = 20
            });
        }

        // --- 2. Seven-Day Consolidated Quota ---
        if (settings.ShowClaudeWeekly7d)
        {
            double sevenDayUsed = 0.0;
            DateTimeOffset? sevenDayReset = null;
            string source = "Claude Desktop / Code Local State";

            // Priority 1: Live / cached Anthropic OAuth API
            if (hasApiRoot && apiRoot.TryGetProperty("seven_day", out JsonElement sdEl) && sdEl.ValueKind == JsonValueKind.Object)
            {
                sevenDayUsed = sdEl.TryGetProperty("utilization", out JsonElement uEl) && uEl.TryGetDouble(out double u) ? u : 0.0;
                if (sdEl.TryGetProperty("resets_at", out JsonElement rEl) && DateTimeOffset.TryParse(rEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset r))
                {
                    if (r > DateTimeOffset.UtcNow)
                    {
                        sevenDayReset = r;
                    }
                }
                source = "Anthropic OAuth API";
            }
            // Priority 2: Claude Desktop plan history
            else if (planHistory != null && planHistory.SevenDayUsedPercent.HasValue)
            {
                sevenDayUsed = planHistory.SevenDayUsedPercent.Value;
                source = "Claude Desktop (plan-usage-history)";
            }

            string weeklySubtitle = isTokenExpired
                ? "Limite semanal consolidado • Token expirado"
                : "Limite semanal consolidado (Opus/Sonnet)";

            items.Add(new QuotaItem
            {
                Id = "claude.weekly",
                Provider = QuotaProviderType.ClaudeCode,
                ProviderBadge = "CLAUDE",
                BadgeColorHex = "#D97757",
                Title = "Claude Code (7d)",
                UsedPercent = Math.Clamp(sevenDayUsed, 0.0, 100.0),
                ResetTime = sevenDayReset,
                Subtitle = weeklySubtitle,
                SourceDetail = source,
                IsAvailable = (hasApiRoot && apiRoot.TryGetProperty("seven_day", out JsonElement sevenDayWindow) &&
                    sevenDayWindow.ValueKind == JsonValueKind.Object && sevenDayWindow.TryGetProperty("utilization", out _)) ||
                    (planHistory?.SevenDayUsedPercent.HasValue ?? false),
                SortOrder = 21
            });
        }

        // --- 3. Dedicated Opus / Sonnet (if available from API or settings) ---
        if (hasApiRoot)
        {
            if (settings.ShowClaudeOpus && apiRoot.TryGetProperty("seven_day_opus", out JsonElement opusEl) && opusEl.ValueKind == JsonValueKind.Object)
            {
                double u = opusEl.TryGetProperty("utilization", out JsonElement uEl) && uEl.TryGetDouble(out double v) ? v : 0.0;
                DateTimeOffset? r = null;
                if (opusEl.TryGetProperty("resets_at", out JsonElement rEl) && DateTimeOffset.TryParse(rEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                    r = parsed;

                items.Add(new QuotaItem
                {
                    Id = "claude.opus",
                    Provider = QuotaProviderType.ClaudeCode,
                    ProviderBadge = "CLAUDE",
                    BadgeColorHex = "#D97757",
                    Title = "Claude Opus (7d)",
                    UsedPercent = Math.Clamp(u, 0.0, 100.0),
                    ResetTime = r,
                    Subtitle = "Cota semanal dedicada Opus",
                    SourceDetail = "Anthropic OAuth API",
                    IsAvailable = true,
                    SortOrder = 22
                });
            }

            if (settings.ShowClaudeSonnet && apiRoot.TryGetProperty("seven_day_sonnet", out JsonElement sonnetEl) && sonnetEl.ValueKind == JsonValueKind.Object)
            {
                double u = sonnetEl.TryGetProperty("utilization", out JsonElement uEl) && uEl.TryGetDouble(out double v) ? v : 0.0;
                DateTimeOffset? r = null;
                if (sonnetEl.TryGetProperty("resets_at", out JsonElement rEl) && DateTimeOffset.TryParse(rEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                    r = parsed;

                items.Add(new QuotaItem
                {
                    Id = "claude.sonnet",
                    Provider = QuotaProviderType.ClaudeCode,
                    ProviderBadge = "CLAUDE",
                    BadgeColorHex = "#D97757",
                    Title = "Claude Sonnet (7d)",
                    UsedPercent = Math.Clamp(u, 0.0, 100.0),
                    ResetTime = r,
                    Subtitle = "Cota semanal dedicada Sonnet",
                    SourceDetail = "Anthropic OAuth API",
                    IsAvailable = true,
                    SortOrder = 23
                });
            }
        }

        return items;
    }

    private void SaveCacheToDisk(string json)
    {
        try
        {
            File.WriteAllText(_cacheFilePath, json);
        }
        catch
        {
            // Ignore IO errors
        }
    }

    private static PlanHistorySample? TryGetLatestPlanUsageHistory()
    {
        var candidatePaths = new List<string>();

        // 1. Windows UWP package path (Centennial Claude App)
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string packagesDir = Path.Combine(localApp, "Packages");
        if (Directory.Exists(packagesDir))
        {
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(packagesDir, "Claude_*"))
                {
                    candidatePaths.Add(Path.Combine(dir, "LocalCache", "Roaming", "Claude", "plan-usage-history.json"));
                }
            }
            catch { }
        }

        // 2. Standard Roaming path
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidatePaths.Add(Path.Combine(roaming, "Claude", "plan-usage-history.json"));

        PlanHistorySample? latest = null;
        foreach (string path in candidatePaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                string json = File.ReadAllText(path);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("samples", out JsonElement samplesEl) && samplesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement s in samplesEl.EnumerateArray())
                    {
                        if (s.ValueKind != JsonValueKind.Object) continue;
                        long t = s.TryGetProperty("t", out JsonElement tEl) && tEl.TryGetInt64(out long parsedT) ? parsedT : 0;
                        if (t <= 0) continue;
                        DateTimeOffset sampleTime;
                        try { sampleTime = DateTimeOffset.FromUnixTimeMilliseconds(t); }
                        catch (ArgumentOutOfRangeException) { continue; }

                        double? fh = null;
                        double? sd = null;

                        if (s.TryGetProperty("u", out JsonElement uEl) && uEl.ValueKind == JsonValueKind.Object)
                        {
                            if (uEl.TryGetProperty("fh", out JsonElement fhEl) && fhEl.TryGetDouble(out double parsedFh))
                                fh = parsedFh;
                            if (uEl.TryGetProperty("sd", out JsonElement sdEl) && sdEl.TryGetDouble(out double parsedSd))
                                sd = parsedSd;
                        }

                        if ((fh.HasValue || sd.HasValue) &&
                            sampleTime <= DateTimeOffset.UtcNow.AddMinutes(1) &&
                            DateTimeOffset.UtcNow - sampleTime <= MaxPlanHistoryAge &&
                            (latest is null || sampleTime > latest.SampleTimeUtc))
                        {
                            latest = new PlanHistorySample(sampleTime, fh, sd);
                        }
                    }
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

        return latest;
    }

    private static SessionDetails TryGetLatestSessionDetails()
    {
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string projectsDir = Path.Combine(userProfile, ".claude", "projects");

            if (!Directory.Exists(projectsDir))
            {
                return new SessionDetails(null, null, null);
            }

            var recentFiles = new DirectoryInfo(projectsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-2))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(12)
                .ToList();

            if (recentFiles.Count == 0)
            {
                return new SessionDetails(null, null, null);
            }

            string? lastCostLine = null;
            string? lastRateLimitLine = null;
            string? tokensLeftText = null;

            foreach (var file in recentFiles)
            {
                try
                {
                    using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    // Session logs can grow to hundreds of MB. Only recent events are relevant.
                    const int tailBytes = 1024 * 1024;
                    if (fs.Length > tailBytes)
                    {
                        fs.Seek(-tailBytes, SeekOrigin.End);
                    }
                    using var sr = new StreamReader(fs);
                    if (fs.Position > 0) sr.ReadLine(); // Discard a partial first record.
                    string? line;
                    string? fileCostLine = null;
                    string? fileRateLimitLine = null;
                    string? fileTokensText = null;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (lastCostLine == null && line.Contains("\"cost-state\"", StringComparison.Ordinal))
                        {
                            fileCostLine = line;
                        }
                        if (lastRateLimitLine == null && (line.Contains("\"resetsAt\"", StringComparison.Ordinal) ||
                            line.Contains("\"usage_limit_reached\"", StringComparison.Ordinal)))
                        {
                            fileRateLimitLine = line;
                        }
                        if (tokensLeftText == null && line.Contains("total_tokens", StringComparison.Ordinal))
                        {
                            int idx = line.IndexOf("<total_tokens>", StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                int endIdx = line.IndexOf("</total_tokens>", idx, StringComparison.Ordinal);
                                if (endIdx > idx)
                                {
                                    string raw = line.Substring(idx + 14, endIdx - (idx + 14)).Trim();
                                    string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length > 0 && long.TryParse(parts[0], out long tokCount))
                                    {
                                        fileTokensText = tokCount >= 1_000_000
                                            ? string.Create(CultureInfo.InvariantCulture, $"{(tokCount / 1_000_000.0):0.1}M tok")
                                            : string.Create(CultureInfo.InvariantCulture, $"{tokCount / 1_000}k tok");
                                    }
                                }
                            }
                        }
                    }
                    lastCostLine ??= fileCostLine;
                    lastRateLimitLine ??= fileRateLimitLine;
                    tokensLeftText ??= fileTokensText;
                }
                catch { }

                if (lastCostLine != null && lastRateLimitLine != null)
                {
                    break;
                }
            }

            DateTimeOffset? activeReset = null;
            if (!string.IsNullOrWhiteSpace(lastRateLimitLine))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(lastRateLimitLine);
                    JsonElement root = doc.RootElement;

                    long resetsAtUnix = 0;
                    if (root.TryGetProperty("apiErrorParams", out JsonElement aepEl) &&
                        aepEl.TryGetProperty("rate_limit_info", out JsonElement rliEl) &&
                        rliEl.TryGetProperty("resetsAt", out JsonElement raEl) &&
                        raEl.TryGetInt64(out long ra1))
                    {
                        resetsAtUnix = ra1;
                    }
                    else if (root.TryGetProperty("quotaLimits", out JsonElement qlEl) &&
                             qlEl.TryGetProperty("resetsAt", out JsonElement raEl2) &&
                             raEl2.TryGetInt64(out long ra2))
                    {
                        resetsAtUnix = ra2;
                    }

                    if (resetsAtUnix > 0)
                    {
                        var parsed = DateTimeOffset.FromUnixTimeSeconds(resetsAtUnix);
                        if (parsed > DateTimeOffset.UtcNow)
                        {
                            activeReset = parsed;
                        }
                    }
                }
                catch { }
            }

            string? subtitle = null;
            string? mainModelName = null;

            if (!string.IsNullOrWhiteSpace(lastCostLine))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(lastCostLine);
                    JsonElement root = doc.RootElement;

                    double costUsd = root.TryGetProperty("totalCostUSD", out JsonElement costEl) && costEl.TryGetDouble(out double c) ? c : 0.0;
                    var modelNames = new List<string>();
                    if (root.TryGetProperty("modelUsage", out JsonElement muEl) && muEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty prop in muEl.EnumerateObject())
                        {
                            modelNames.Add(FormatShortModelName(prop.Name));
                        }
                    }

                    mainModelName = modelNames.FirstOrDefault();
                    string modelsText = modelNames.Count > 0 ? string.Join(", ", modelNames.Distinct()) : "Claude";

                    if (!string.IsNullOrEmpty(tokensLeftText))
                    {
                        subtitle = string.Create(CultureInfo.InvariantCulture, $"{tokensLeftText} • Sessão: ${costUsd:F2} • {modelsText}");
                    }
                    else
                    {
                        subtitle = string.Create(CultureInfo.InvariantCulture, $"Última sessão: ${costUsd:F2} • {modelsText}");
                    }
                }
                catch { }
            }

            return new SessionDetails(subtitle, activeReset, mainModelName);
        }
        catch
        {
            return new SessionDetails(null, null, null);
        }
    }

    private static string FormatShortModelName(string rawModel)
    {
        if (rawModel.Contains("sonnet-5-5", StringComparison.OrdinalIgnoreCase)) return "Sonnet 5.5";
        if (rawModel.Contains("opus-5-5", StringComparison.OrdinalIgnoreCase)) return "Opus 5.5";
        if (rawModel.Contains("sonnet-4-5", StringComparison.OrdinalIgnoreCase)) return "Sonnet 4.5";
        if (rawModel.Contains("opus-4", StringComparison.OrdinalIgnoreCase)) return "Opus 4";
        if (rawModel.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return "Sonnet";
        if (rawModel.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (rawModel.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return "Haiku";
        return rawModel;
    }

    private sealed record PlanHistorySample(DateTimeOffset SampleTimeUtc, double? FiveHourUsedPercent, double? SevenDayUsedPercent);
    private sealed record SessionDetails(string? Subtitle, DateTimeOffset? ActiveRateLimitReset, string? ModelName);
}
