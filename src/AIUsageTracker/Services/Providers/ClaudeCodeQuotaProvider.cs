using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ClaudeAuthService? _authService;
    private long _nextAllowedApiCallTick;
    private string? _lastValidApiJson;
    private DateTimeOffset _lastValidApiTimeUtc;
    private string? _cachedCredentialSignature;

    public QuotaProviderType ProviderType => QuotaProviderType.ClaudeCode;

    public ClaudeCodeQuotaProvider(ClaudeAuthService? authService = null)
    {
        _authService = authService;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "AIUsageTracker");
        Directory.CreateDirectory(dir);
        _cacheFilePath = Path.Combine(dir, "claude-oauth-cache.json");

        if (File.Exists(_cacheFilePath))
        {
            try
            {
                TimeSpan cacheAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_cacheFilePath);
                if (cacheAge >= TimeSpan.Zero && cacheAge <= MaxCachedApiAge)
                {
                    using JsonDocument cachedDocument = JsonDocument.Parse(File.ReadAllText(_cacheFilePath));
                    JsonElement root = cachedDocument.RootElement;
                    if (root.TryGetProperty("credentialSignature", out JsonElement signatureElement) &&
                        signatureElement.ValueKind == JsonValueKind.String &&
                        root.TryGetProperty("usage", out JsonElement usageElement))
                    {
                        string candidate = usageElement.GetRawText();
                        if (IsValidUsageApiResponse(candidate))
                        {
                            _cachedCredentialSignature = signatureElement.GetString();
                            _lastValidApiJson = candidate;
                            _lastValidApiTimeUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
                        }
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

        InvalidateCacheIfCredentialsChanged(settings);

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
                    InvalidateCacheIfCredentialsChanged(settings);
                }

                string? requestSignature = GetCredentialSignature(settings);
                var apiResult = await TryCallOAuthApiAsync(settings, cancellationToken);
                if (apiResult.Success && requestSignature != null &&
                    requestSignature == GetCredentialSignature(settings) && IsValidUsageApiResponse(apiResult.Body))
                {
                    apiBody = apiResult.Body;
                    _lastValidApiJson = apiBody;
                    _lastValidApiTimeUtc = DateTimeOffset.UtcNow;
                    SetNextApiCall(MinApiPollInterval);
                    SaveCacheToDisk(apiBody!);
                }
                else if (apiResult.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Token expired: attempt silent refresh in background and retry!
                    bool refreshed = await ClaudeAuthService.TrySilentTokenRefreshAsync(settings, cancellationToken);
                    if (refreshed)
                    {
                        InvalidateCacheIfCredentialsChanged(settings);
                        requestSignature = GetCredentialSignature(settings);
                        var retryResult = await TryCallOAuthApiAsync(settings, cancellationToken);
                        if (retryResult.Success && requestSignature != null &&
                            requestSignature == GetCredentialSignature(settings) && IsValidUsageApiResponse(retryResult.Body))
                        {
                            apiBody = retryResult.Body;
                            _lastValidApiJson = apiBody;
                            _lastValidApiTimeUtc = DateTimeOffset.UtcNow;
                            SetNextApiCall(MinApiPollInterval);
                            SaveCacheToDisk(apiBody!);
                        }
                        else if (retryResult.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            _authService?.MarkTokenRejected();
                        }
                    }
                    else
                    {
                        _authService?.MarkTokenRejected();
                    }
                }
                else if (apiResult.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    SetNextApiCall(apiResult.RetryAfter ?? TimeSpan.FromMinutes(2));
                }
            }
            catch
            {
                // Network error, proceed to synthesize with local data
            }
        }

        bool isTokenExpired = _authService?.CheckTokenExpired() ?? ClaudeAuthService.IsTokenExpiredStatic(settings);

        // 4. Synthesize the most accurate, real-time quota items (using apiBody or cached API)
        TimeSpan age = DateTimeOffset.UtcNow - _lastValidApiTimeUtc;
        string? recentCachedApi = age >= TimeSpan.Zero && age <= MaxCachedApiAge
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
        if (Environment.TickCount64 < _nextAllowedApiCallTick)
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

    private void InvalidateCacheIfCredentialsChanged(AppSettings settings)
    {
        string? signature = GetCredentialSignature(settings);
        if (signature == _cachedCredentialSignature) return;

        _cachedCredentialSignature = signature;
        _lastValidApiJson = null;
        _lastValidApiTimeUtc = DateTimeOffset.MinValue;
        _nextAllowedApiCallTick = 0;
    }

    private void SetNextApiCall(TimeSpan wait)
    {
        // Monotonic elapsed time avoids a Windows clock adjustment freezing polling.
        long delayMs = (long)Math.Clamp(wait.TotalMilliseconds, 0, TimeSpan.FromMinutes(30).TotalMilliseconds);
        _nextAllowedApiCallTick = Environment.TickCount64 + delayMs;
    }

    private static string? GetCredentialSignature(AppSettings settings)
    {
        try
        {
            string path = Path.GetFullPath(settings.GetEffectiveClaudeCredentialsPath());
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement oauth = doc.RootElement.GetProperty("claudeAiOauth");
            string? accessToken = oauth.GetProperty("accessToken").GetString();
            if (string.IsNullOrWhiteSpace(accessToken)) return null;

            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n" + accessToken));
            return Convert.ToHexString(bytes);
        }
        catch { return null; }
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
                if (TryReadUsageWindow(doc.RootElement, key, out _, out _)) return true;
            }
        }
        catch (JsonException) { }

        return false;
    }

    private static bool TryReadUsageWindow(JsonElement root, string key, out double usedPercent, out DateTimeOffset? reset)
    {
        usedPercent = 0;
        reset = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(key, out JsonElement window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out JsonElement utilization) || utilization.ValueKind != JsonValueKind.Number ||
            !utilization.TryGetDouble(out double value) || !double.IsFinite(value) || value < 0 || value > 100)
            return false;

        usedPercent = value;
        if (window.TryGetProperty("resets_at", out JsonElement resetElement) &&
            resetElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetElement.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            reset = parsed;

        return true;
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
            if (retry == null && response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
                retry = retryDate - DateTimeOffset.UtcNow;
            return (false, HttpStatusCode.TooManyRequests, retry, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, response.StatusCode, null, null);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (true, HttpStatusCode.OK, null, body);
    }

    internal static List<QuotaItem> SynthesizeQuotas(
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
            string subtitle = settings.ShowClaudeSessionCostSubtitle
                ? sessionDetails.Subtitle ?? "Janela de sessão de 5 horas"
                : "Janela de sessão de 5 horas";

            if (isTokenExpired)
            {
                subtitle = string.IsNullOrEmpty(subtitle) || subtitle == "Janela de sessão de 5 horas"
                    ? "Token expirado • Clique em Reconectar"
                    : $"{subtitle} • Token expirado";
            }

            // Priority 1: Recent Anthropic OAuth API
            double parsedFiveHour = 0;
            DateTimeOffset? parsedFiveHourReset = null;
            bool hasFiveHour = hasApiRoot && TryReadUsageWindow(apiRoot, "five_hour", out parsedFiveHour, out parsedFiveHourReset);
            if (hasFiveHour)
            {
                fiveHourUsed = parsedFiveHour;
                if (parsedFiveHourReset > DateTimeOffset.UtcNow) fiveHourReset = parsedFiveHourReset;
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
                IsAvailable = hasFiveHour ||
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
            double parsedSevenDay = 0;
            DateTimeOffset? parsedSevenDayReset = null;
            bool hasSevenDay = hasApiRoot && TryReadUsageWindow(apiRoot, "seven_day", out parsedSevenDay, out parsedSevenDayReset);
            if (hasSevenDay)
            {
                sevenDayUsed = parsedSevenDay;
                if (parsedSevenDayReset > DateTimeOffset.UtcNow) sevenDayReset = parsedSevenDayReset;
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
                IsAvailable = hasSevenDay ||
                    (planHistory?.SevenDayUsedPercent.HasValue ?? false),
                SortOrder = 21
            });
        }

        // --- 3. Dedicated Opus / Sonnet (if available from API or settings) ---
        if (hasApiRoot)
        {
            if (settings.ShowClaudeOpus && TryReadUsageWindow(apiRoot, "seven_day_opus", out double opusUsed, out DateTimeOffset? opusReset))
            {
                items.Add(new QuotaItem
                {
                    Id = "claude.opus",
                    Provider = QuotaProviderType.ClaudeCode,
                    ProviderBadge = "CLAUDE",
                    BadgeColorHex = "#D97757",
                    Title = "Claude Opus (7d)",
                    UsedPercent = opusUsed,
                    ResetTime = opusReset,
                    Subtitle = "Cota semanal dedicada Opus",
                    SourceDetail = "Anthropic OAuth API",
                    IsAvailable = true,
                    SortOrder = 22
                });
            }

            if (settings.ShowClaudeSonnet && TryReadUsageWindow(apiRoot, "seven_day_sonnet", out double sonnetUsed, out DateTimeOffset? sonnetReset))
            {
                items.Add(new QuotaItem
                {
                    Id = "claude.sonnet",
                    Provider = QuotaProviderType.ClaudeCode,
                    ProviderBadge = "CLAUDE",
                    BadgeColorHex = "#D97757",
                    Title = "Claude Sonnet (7d)",
                    UsedPercent = sonnetUsed,
                    ResetTime = sonnetReset,
                    Subtitle = "Cota semanal dedicada Sonnet",
                    SourceDetail = "Anthropic OAuth API",
                    IsAvailable = true,
                    SortOrder = 23
                });
            }
        }

        if (settings.ShowClaudeDedicatedEvenIfEmpty)
        {
            if (settings.ShowClaudeOpus && !items.Any(item => item.Id == "claude.opus"))
                items.Add(CreateUnavailableDedicatedQuota("claude.opus", "Claude Opus (7d)", 22));
            if (settings.ShowClaudeSonnet && !items.Any(item => item.Id == "claude.sonnet"))
                items.Add(CreateUnavailableDedicatedQuota("claude.sonnet", "Claude Sonnet (7d)", 23));
        }

        return items;
    }

    private static QuotaItem CreateUnavailableDedicatedQuota(string id, string title, int order) => new()
    {
        Id = id,
        Provider = QuotaProviderType.ClaudeCode,
        ProviderBadge = "CLAUDE",
        BadgeColorHex = "#D97757",
        Title = title,
        IsAvailable = false,
        UsedPercent = 0,
        Subtitle = "Sem dados recentes desta cota",
        SourceDetail = "Anthropic OAuth API (indisponível)",
        SortOrder = order
    };

    private void SaveCacheToDisk(string json)
    {
        try
        {
            if (_cachedCredentialSignature == null) return;
            using JsonDocument doc = JsonDocument.Parse(json);
            string cache = JsonSerializer.Serialize(new
            {
                credentialSignature = _cachedCredentialSignature,
                usage = doc.RootElement
            });
            File.WriteAllText(_cacheFilePath, cache);
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
                            if (uEl.TryGetProperty("fh", out JsonElement fhEl) && fhEl.ValueKind == JsonValueKind.Number &&
                                fhEl.TryGetDouble(out double parsedFh) && double.IsFinite(parsedFh) && parsedFh >= 0 && parsedFh <= 100)
                                fh = parsedFh;
                            if (uEl.TryGetProperty("sd", out JsonElement sdEl) && sdEl.ValueKind == JsonValueKind.Number &&
                                sdEl.TryGetDouble(out double parsedSd) && double.IsFinite(parsedSd) && parsedSd >= 0 && parsedSd <= 100)
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

    internal sealed record PlanHistorySample(DateTimeOffset SampleTimeUtc, double? FiveHourUsedPercent, double? SevenDayUsedPercent);
    internal sealed record SessionDetails(string? Subtitle, DateTimeOffset? ActiveRateLimitReset, string? ModelName);
}
