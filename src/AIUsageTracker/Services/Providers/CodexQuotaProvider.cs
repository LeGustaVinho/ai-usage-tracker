using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIUsageTracker.Helpers;
using AIUsageTracker.Models;

namespace AIUsageTracker.Services.Providers;

public sealed class CodexQuotaProvider : IQuotaProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(7)
    };

    public QuotaProviderType ProviderType => QuotaProviderType.Codex;

    public async Task<IReadOnlyList<QuotaItem>> FetchQuotasAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.EnableCodex)
        {
            return Array.Empty<QuotaItem>();
        }

        // 1. Try live OAuth endpoint (wham/usage)
        var liveItems = await TryFetchFromLiveApiAsync(settings, cancellationToken);
        if (liveItems.Count > 0)
        {
            return liveItems;
        }

        // 2. Fallback to latest local Codex session JSONL log
        var localItems = TryFetchFromLocalSessions(settings);
        if (localItems.Count > 0)
        {
            return localItems;
        }

        return new[]
        {
            new QuotaItem
            {
                Id = "codex.primary",
                Provider = QuotaProviderType.Codex,
                ProviderBadge = "CODEX",
                BadgeColorHex = "#10A37F",
                Title = "ChatGPT / Codex",
                UsedPercent = 0,
                IsAvailable = false,
                Subtitle = "Credenciais ou sessão não encontradas (~/.codex)",
                SortOrder = 10
            }
        };
    }

    private static async Task<IReadOnlyList<QuotaItem>> TryFetchFromLiveApiAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            string authPath = settings.GetEffectiveCodexAuthPath();
            if (!File.Exists(authPath))
            {
                return Array.Empty<QuotaItem>();
            }

            string authJson = await File.ReadAllTextAsync(authPath, cancellationToken);
            using JsonDocument authDoc = JsonDocument.Parse(authJson);

            if (!authDoc.RootElement.TryGetProperty("tokens", out JsonElement tokensEl) ||
                !tokensEl.TryGetProperty("access_token", out JsonElement accessTokenEl))
            {
                return Array.Empty<QuotaItem>();
            }

            string? accessToken = accessTokenEl.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Array.Empty<QuotaItem>();
            }

            string? accountId = tokensEl.TryGetProperty("account_id", out JsonElement accEl)
                ? accEl.GetString()
                : null;

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.ParseAdd("codex-cli/1.0");
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            }

            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<QuotaItem>();
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            string planType = root.TryGetProperty("plan_type", out JsonElement planEl) && planEl.ValueKind == JsonValueKind.String
                ? planEl.GetString() ?? "chatgpt"
                : "chatgpt";

            int resetCredits = 0;
            if (root.TryGetProperty("rate_limit_reset_credits", out JsonElement rcEl) &&
                rcEl.ValueKind == JsonValueKind.Object &&
                rcEl.TryGetProperty("available_count", out JsonElement availCountEl) &&
                availCountEl.TryGetInt32(out int parsedCount))
            {
                resetCredits = parsedCount;
            }

            var results = new List<QuotaItem>();

            if (root.TryGetProperty("rate_limit", out JsonElement rlEl) && rlEl.ValueKind == JsonValueKind.Object)
            {
                if (settings.ShowCodexPrimary &&
                    rlEl.TryGetProperty("primary_window", out JsonElement pwEl) &&
                    pwEl.ValueKind == JsonValueKind.Object)
                {
                    results.Add(ParseWhamWindow(
                        pwEl,
                        id: "codex.primary",
                        defaultLabel: "ChatGPT / Codex",
                        planType: planType,
                        resetCredits: resetCredits,
                        sortOrder: 10));
                }

                if (settings.ShowCodexSecondary &&
                    rlEl.TryGetProperty("secondary_window", out JsonElement swEl) &&
                    swEl.ValueKind == JsonValueKind.Object)
                {
                    results.Add(ParseWhamWindow(
                        swEl,
                        id: "codex.secondary",
                        defaultLabel: "Codex (Janela 2)",
                        planType: planType,
                        resetCredits: 0,
                        sortOrder: 11));
                }
            }

            return results;
        }
        catch
        {
            return Array.Empty<QuotaItem>();
        }
    }

    private static QuotaItem ParseWhamWindow(
        JsonElement windowEl,
        string id,
        string defaultLabel,
        string planType,
        int resetCredits,
        int sortOrder)
    {
        double usedPercent = windowEl.TryGetProperty("used_percent", out JsonElement upEl) && upEl.TryGetDouble(out double up)
            ? up
            : 0.0;

        int? windowSeconds = windowEl.TryGetProperty("limit_window_seconds", out JsonElement lwEl) && lwEl.TryGetInt32(out int lw)
            ? lw
            : null;

        DateTimeOffset? resetTime = null;
        if (windowEl.TryGetProperty("reset_at", out JsonElement raEl) && raEl.TryGetInt64(out long unixSec) && unixSec > 0)
        {
            resetTime = DateTimeOffset.FromUnixTimeSeconds(unixSec);
        }

        string windowTag = TimeFormatHelper.FormatWindowDuration(windowSeconds);
        string title = string.IsNullOrEmpty(windowTag)
            ? defaultLabel
            : $"{defaultLabel} ({windowTag})";

        string creditsNote = resetCredits > 0 ? $" • {resetCredits} reset extra" : string.Empty;
        string subtitle = $"Plano: {planType}{creditsNote}";

        return new QuotaItem
        {
            Id = id,
            Provider = QuotaProviderType.Codex,
            ProviderBadge = "CODEX",
            BadgeColorHex = "#10A37F",
            Title = title,
            UsedPercent = Math.Clamp(usedPercent, 0.0, 100.0),
            ResetTime = resetTime,
            Subtitle = subtitle,
            SourceDetail = "ChatGPT OAuth API (wham/usage)",
            IsAvailable = true,
            SortOrder = sortOrder
        };
    }

    private static IReadOnlyList<QuotaItem> TryFetchFromLocalSessions(AppSettings settings)
    {
        try
        {
            string sessionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "sessions");

            if (!Directory.Exists(sessionsDir))
            {
                return Array.Empty<QuotaItem>();
            }

            var latestFiles = new DirectoryInfo(sessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5);

            foreach (FileInfo file in latestFiles)
            {
                var parsed = TryParseSessionFile(file.FullName, settings);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Ignore IO errors
        }

        return Array.Empty<QuotaItem>();
    }

    private static IReadOnlyList<QuotaItem> TryParseSessionFile(string filePath, AppSettings settings)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);

            string? lastRateLimitLine = null;
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                {
                    lastRateLimitLine = line;
                }
            }

            if (string.IsNullOrWhiteSpace(lastRateLimitLine))
            {
                return Array.Empty<QuotaItem>();
            }

            using JsonDocument doc = JsonDocument.Parse(lastRateLimitLine);
            if (!doc.RootElement.TryGetProperty("payload", out JsonElement payloadEl) ||
                !payloadEl.TryGetProperty("rate_limits", out JsonElement rlEl) ||
                rlEl.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<QuotaItem>();
            }

            string planType = rlEl.TryGetProperty("plan_type", out JsonElement ptEl) && ptEl.ValueKind == JsonValueKind.String
                ? ptEl.GetString() ?? "local"
                : "local";

            var results = new List<QuotaItem>();

            if (settings.ShowCodexPrimary &&
                rlEl.TryGetProperty("primary", out JsonElement primEl) &&
                primEl.ValueKind == JsonValueKind.Object)
            {
                double used = primEl.TryGetProperty("used_percent", out JsonElement upEl) && upEl.TryGetDouble(out double u)
                    ? u
                    : 0.0;

                int? windowMins = primEl.TryGetProperty("window_minutes", out JsonElement wmEl) && wmEl.TryGetInt32(out int wm)
                    ? wm
                    : null;

                DateTimeOffset? resetTime = primEl.TryGetProperty("resets_at", out JsonElement raEl) && raEl.TryGetInt64(out long unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : null;

                string winTag = windowMins.HasValue ? TimeFormatHelper.FormatWindowDuration(windowMins.Value * 60) : string.Empty;
                string title = string.IsNullOrEmpty(winTag) ? "ChatGPT / Codex" : $"ChatGPT / Codex ({winTag})";

                results.Add(new QuotaItem
                {
                    Id = "codex.primary",
                    Provider = QuotaProviderType.Codex,
                    ProviderBadge = "CODEX",
                    BadgeColorHex = "#10A37F",
                    Title = title,
                    UsedPercent = Math.Clamp(used, 0.0, 100.0),
                    ResetTime = resetTime,
                    Subtitle = $"Plano: {planType} (Cache de sessão local)",
                    SourceDetail = $"Arquivo local: {Path.GetFileName(filePath)}",
                    IsAvailable = true,
                    SortOrder = 10
                });
            }

            return results;
        }
        catch
        {
            return Array.Empty<QuotaItem>();
        }
    }
}
