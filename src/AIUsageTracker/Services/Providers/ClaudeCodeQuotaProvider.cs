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

    // Minimum interval between live HTTP calls to Anthropic's oauth/usage endpoint to avoid HTTP 429
    private static readonly TimeSpan MinApiPollInterval = TimeSpan.FromMinutes(3);

    private readonly string _cacheFilePath;
    private DateTimeOffset _nextAllowedApiCallUtc = DateTimeOffset.MinValue;
    private string? _lastValidApiJson;

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
                _lastValidApiJson = File.ReadAllText(_cacheFilePath);
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

        string? sessionCostSubtitle = settings.ShowClaudeSessionCostSubtitle
            ? TryGetLatestLocalSessionSummary()
            : null;

        try
        {
            string credPath = settings.GetEffectiveClaudeCredentialsPath();
            if (!File.Exists(credPath))
            {
                return TryBuildFromCacheOrFallback(settings, sessionCostSubtitle, "Credenciais ~/.claude/.credentials.json não encontradas");
            }

            // If we recently polled or are in a 429 cooldown window and have cached JSON, use cache without hitting 429
            if (DateTimeOffset.UtcNow < _nextAllowedApiCallUtc && !string.IsNullOrWhiteSpace(_lastValidApiJson))
            {
                var cachedItems = ParseUsageJson(_lastValidApiJson, settings, sessionCostSubtitle, isCached: true);
                if (cachedItems.Count > 0)
                {
                    return cachedItems;
                }
            }

            string credJson = await File.ReadAllTextAsync(credPath, cancellationToken);
            using JsonDocument credDoc = JsonDocument.Parse(credJson);

            if (!credDoc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauthEl) ||
                !oauthEl.TryGetProperty("accessToken", out JsonElement tokenEl))
            {
                return TryBuildFromCacheOrFallback(settings, sessionCostSubtitle, "Token claudeAiOauth ausente");
            }

            string? accessToken = tokenEl.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return TryBuildFromCacheOrFallback(settings, sessionCostSubtitle, "Token OAuth vazio");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("claude-code/2.0.0");

            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Respect Retry-After header if provided, otherwise back off for 4 minutes
                TimeSpan backoff = TimeSpan.FromMinutes(4);
                if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
                {
                    backoff = delta;
                }
                _nextAllowedApiCallUtc = DateTimeOffset.UtcNow.Add(backoff);

                return TryBuildFromCacheOrFallback(settings, sessionCostSubtitle, "API em cooldown (HTTP 429 Rate Limit)");
            }

            if (!response.IsSuccessStatusCode)
            {
                return TryBuildFromCacheOrFallback(settings, sessionCostSubtitle, $"API retornou HTTP {(int)response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            var liveItems = ParseUsageJson(body, settings, sessionCostSubtitle, isCached: false);

            if (liveItems.Count > 0)
            {
                _lastValidApiJson = body;
                _nextAllowedApiCallUtc = DateTimeOffset.UtcNow.Add(MinApiPollInterval);
                SaveCacheToDisk(body);
                return liveItems;
            }

            return TryBuildFromCacheOrFallback(settings, sessionCostSubtitle, "Resposta da API sem janelas de cota");
        }
        catch (Exception ex)
        {
            return TryBuildFromCacheOrFallback(settings, sessionCostSubtitle, ex.Message);
        }
    }

    private IReadOnlyList<QuotaItem> TryBuildFromCacheOrFallback(
        AppSettings settings,
        string? sessionCostSubtitle,
        string fallbackReason)
    {
        if (!string.IsNullOrWhiteSpace(_lastValidApiJson))
        {
            try
            {
                var cached = ParseUsageJson(_lastValidApiJson, settings, sessionCostSubtitle, isCached: true);
                if (cached.Count > 0)
                {
                    return cached;
                }
            }
            catch
            {
                // Ignore cache parse failure
            }
        }

        return CreateFallbackList(sessionCostSubtitle, fallbackReason);
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

    private static List<QuotaItem> ParseUsageJson(
        string body,
        AppSettings settings,
        string? sessionCostSubtitle,
        bool isCached)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        string sourceTag = isCached
            ? "Anthropic OAuth Usage API (Cache Local — Proteção 429)"
            : "Anthropic OAuth Usage API";

        var items = new List<QuotaItem>();

        // 1. 5-Hour Session Quota
        if (settings.ShowClaudeSession5h &&
            root.TryGetProperty("five_hour", out JsonElement fiveHourEl) &&
            fiveHourEl.ValueKind == JsonValueKind.Object)
        {
            items.Add(ParseClaudeBucket(
                fiveHourEl,
                id: "claude.session",
                title: "Claude Code (5h)",
                subtitle: sessionCostSubtitle ?? "Janela de sessão de 5 horas",
                sourceDetail: sourceTag,
                sortOrder: 20));
        }

        // 2. 7-Day Weekly All-Models Quota
        if (settings.ShowClaudeWeekly7d &&
            root.TryGetProperty("seven_day", out JsonElement sevenDayEl) &&
            sevenDayEl.ValueKind == JsonValueKind.Object)
        {
            items.Add(ParseClaudeBucket(
                sevenDayEl,
                id: "claude.weekly",
                title: "Claude Code (7d)",
                subtitle: "Limite semanal consolidado (Opus/Sonnet)",
                sourceDetail: sourceTag,
                sortOrder: 21));
        }

        // 3. Dedicated Opus 7-Day Quota
        if (settings.ShowClaudeOpus)
        {
            if (root.TryGetProperty("seven_day_opus", out JsonElement opusEl) && opusEl.ValueKind == JsonValueKind.Object)
            {
                items.Add(ParseClaudeBucket(
                    opusEl,
                    id: "claude.opus",
                    title: "Claude Opus (7d)",
                    subtitle: "Cota semanal dedicada Opus",
                    sourceDetail: sourceTag,
                    sortOrder: 22));
            }
            else if (settings.ShowClaudeDedicatedEvenIfEmpty)
            {
                items.Add(new QuotaItem
                {
                    Id = "claude.opus",
                    Provider = QuotaProviderType.ClaudeCode,
                    ProviderBadge = "CLAUDE",
                    BadgeColorHex = "#D97757",
                    Title = "Claude Opus (7d)",
                    UsedPercent = 0,
                    Subtitle = "Compartilhado com cota semanal geral",
                    SourceDetail = sourceTag,
                    IsAvailable = true,
                    SortOrder = 22
                });
            }
        }

        // 4. Dedicated Sonnet 7-Day Quota
        if (settings.ShowClaudeSonnet)
        {
            if (root.TryGetProperty("seven_day_sonnet", out JsonElement sonnetEl) && sonnetEl.ValueKind == JsonValueKind.Object)
            {
                items.Add(ParseClaudeBucket(
                    sonnetEl,
                    id: "claude.sonnet",
                    title: "Claude Sonnet (7d)",
                    subtitle: "Cota semanal dedicada Sonnet",
                    sourceDetail: sourceTag,
                    sortOrder: 23));
            }
            else if (settings.ShowClaudeDedicatedEvenIfEmpty)
            {
                items.Add(new QuotaItem
                {
                    Id = "claude.sonnet",
                    Provider = QuotaProviderType.ClaudeCode,
                    ProviderBadge = "CLAUDE",
                    BadgeColorHex = "#D97757",
                    Title = "Claude Sonnet (7d)",
                    UsedPercent = 0,
                    Subtitle = "Compartilhado com cota semanal geral",
                    SourceDetail = sourceTag,
                    IsAvailable = true,
                    SortOrder = 23
                });
            }
        }

        return items;
    }

    private static QuotaItem ParseClaudeBucket(
        JsonElement bucketEl,
        string id,
        string title,
        string? subtitle,
        string sourceDetail,
        int sortOrder)
    {
        double utilization = bucketEl.TryGetProperty("utilization", out JsonElement utilEl) && utilEl.TryGetDouble(out double u)
            ? u
            : 0.0;

        DateTimeOffset? resetTime = null;
        if (bucketEl.TryGetProperty("resets_at", out JsonElement raEl) &&
            raEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(raEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedReset))
        {
            resetTime = parsedReset;
        }

        return new QuotaItem
        {
            Id = id,
            Provider = QuotaProviderType.ClaudeCode,
            ProviderBadge = "CLAUDE",
            BadgeColorHex = "#D97757",
            Title = title,
            UsedPercent = Math.Clamp(utilization, 0.0, 100.0),
            ResetTime = resetTime,
            Subtitle = subtitle,
            SourceDetail = sourceDetail,
            IsAvailable = true,
            SortOrder = sortOrder
        };
    }

    private static IReadOnlyList<QuotaItem> CreateFallbackList(string? sessionCostSubtitle, string reason)
    {
        return new[]
        {
            new QuotaItem
            {
                Id = "claude.session",
                Provider = QuotaProviderType.ClaudeCode,
                ProviderBadge = "CLAUDE",
                BadgeColorHex = "#D97757",
                Title = "Claude Code",
                UsedPercent = 0,
                IsAvailable = false,
                Subtitle = sessionCostSubtitle != null ? $"{sessionCostSubtitle} • ({reason})" : reason,
                SourceDetail = reason,
                SortOrder = 20
            }
        };
    }

    private static string? TryGetLatestLocalSessionSummary()
    {
        try
        {
            string projectsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "projects");

            if (!Directory.Exists(projectsDir))
            {
                return null;
            }

            FileInfo? latestFile = new DirectoryInfo(projectsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latestFile == null)
            {
                return null;
            }

            using var fs = new FileStream(latestFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);

            string? lastCostLine = null;
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Contains("\"cost-state\"", StringComparison.Ordinal))
                {
                    lastCostLine = line;
                }
            }

            if (string.IsNullOrWhiteSpace(lastCostLine))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(lastCostLine);
            JsonElement root = doc.RootElement;

            double costUsd = root.TryGetProperty("totalCostUSD", out JsonElement costEl) && costEl.TryGetDouble(out double c)
                ? c
                : 0.0;

            var modelNames = new List<string>();
            if (root.TryGetProperty("modelUsage", out JsonElement muEl) && muEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in muEl.EnumerateObject())
                {
                    modelNames.Add(FormatShortModelName(prop.Name));
                }
            }

            string modelsText = modelNames.Count > 0 ? string.Join(", ", modelNames.Distinct()) : "Claude";
            return string.Create(CultureInfo.InvariantCulture, $"Última sessão: ${costUsd:F2} • {modelsText}");
        }
        catch
        {
            return null;
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
}
