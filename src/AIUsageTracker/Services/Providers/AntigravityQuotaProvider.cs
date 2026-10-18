using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIUsageTracker.Helpers;
using AIUsageTracker.Models;

namespace AIUsageTracker.Services.Providers;

public sealed class AntigravityQuotaProvider : IQuotaProvider
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex CsrfRegex = new(@"--csrf_token(?:=|\s+)([^\s""]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EffortSuffixRegex = new(@"\s*\((Low|Medium|High)\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string RequestBodyJson =
        """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}""";
    private const string SummaryRequestBodyJson =
        """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en","ideVersion":"unknown"}}""";

    private CachedEndpoint? _cachedEndpoint;

    public QuotaProviderType ProviderType => QuotaProviderType.Antigravity;

    private sealed record CachedEndpoint(int ProcessId, string BaseUrl, string CsrfToken);
    private sealed record QuotaResponses(string? UserStatusJson, string? SummaryJson);

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public async Task<IReadOnlyList<QuotaItem>> FetchQuotasAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.EnableAntigravity)
        {
            return Array.Empty<QuotaItem>();
        }

        try
        {
            QuotaResponses? responses = await QueryLanguageServerAsync(settings.ShowAntigravityGeminiWeekly, cancellationToken);
            if (responses == null)
            {
                return CreateOfflineFallback();
            }

            return ParseQuotaResponses(responses.UserStatusJson, responses.SummaryJson, settings);
        }
        catch (Exception ex)
        {
            return CreateOfflineFallback(ex.Message);
        }
    }

    private async Task<QuotaResponses?> QueryLanguageServerAsync(bool needsWeeklySummary, CancellationToken cancellationToken)
    {
        QuotaResponses? legacyFallback = null;

        // Prefer servers that expose the weekly summary. The IDE-only language_server
        // usually supports GetUserStatus but not RetrieveUserQuotaSummary.
        if (_cachedEndpoint != null)
        {
            QuotaResponses? cachedResult = await TryFetchEndpointAsync(_cachedEndpoint.BaseUrl, _cachedEndpoint.CsrfToken, needsWeeklySummary, cancellationToken);
            if (cachedResult != null)
            {
                if (!needsWeeklySummary || HasGeminiWeeklyQuota(cachedResult.SummaryJson))
                {
                    return cachedResult;
                }
                legacyFallback = cachedResult;
            }
            else
            {
                _cachedEndpoint = null;
            }
        }

        // Discover all running language servers so an IDE endpoint cannot hide
        // a richer Antigravity app endpoint on the same machine.
        Process[] processes = Process.GetProcessesByName("language_server");
        try
        {
            foreach (Process proc in processes)
            {
                string? cmdLine = Win32Interop.GetProcessCommandLine(proc.Id);
                if (string.IsNullOrWhiteSpace(cmdLine))
                {
                    continue;
                }

                // Scheduled-task children can outlive the IDE and expose stale ports.
                if (cmdLine.Contains("multicall schedule", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = CsrfRegex.Match(cmdLine);
                if (!match.Success)
                {
                    continue;
                }

                string csrfToken = match.Groups[1].Value;
                List<int> ports = Win32Interop.GetListeningPortsForPid(proc.Id);

                foreach (int port in ports)
                {
                    foreach (string scheme in new[] { "http", "https" })
                    {
                        string baseUrl = $"{scheme}://127.0.0.1:{port}";
                        if (_cachedEndpoint?.ProcessId == proc.Id && _cachedEndpoint.BaseUrl == baseUrl)
                        {
                            continue;
                        }

                        QuotaResponses? result = await TryFetchEndpointAsync(baseUrl, csrfToken, needsWeeklySummary, cancellationToken);
                        if (result != null)
                        {
                            if (!needsWeeklySummary || HasGeminiWeeklyQuota(result.SummaryJson))
                            {
                                _cachedEndpoint = new CachedEndpoint(proc.Id, baseUrl, csrfToken);
                                return result;
                            }

                            if (legacyFallback == null)
                            {
                                legacyFallback = result;
                                _cachedEndpoint = new CachedEndpoint(proc.Id, baseUrl, csrfToken);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (Process proc in processes)
            {
                proc.Dispose();
            }
        }

        return legacyFallback;
    }

    private static async Task<QuotaResponses?> TryFetchEndpointAsync(string baseUrl, string csrfToken, bool needsWeeklySummary, CancellationToken cancellationToken)
    {
        string? status = await TryCallRpcAsync(baseUrl, csrfToken, "GetUserStatus", "clientModelConfigs", cancellationToken);
        string? summary = needsWeeklySummary
            ? await TryCallRpcAsync(baseUrl, csrfToken, "RetrieveUserQuotaSummary", "\"groups\"", cancellationToken)
            : null;
        return status != null || summary != null ? new QuotaResponses(status, summary) : null;
    }

    private static async Task<string?> TryCallRpcAsync(string baseUrl, string csrfToken, string method, string expectedField, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"{baseUrl}/exa.language_server_pb.LanguageServerService/{method}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", csrfToken);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            string body = method == "RetrieveUserQuotaSummary" ? SummaryRequestBodyJson : RequestBodyJson;
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (content.Contains(expectedField, StringComparison.Ordinal))
            {
                return content;
            }
        }
        catch
        {
            // Ignore port probe error
        }

        return null;
    }

    internal static IReadOnlyList<QuotaItem> ParseQuotaResponses(string? statusJson, string? summaryJson, AppSettings settings)
    {
        var items = !string.IsNullOrWhiteSpace(statusJson)
            ? ParseUserStatusResponse(statusJson, settings).ToList()
            : new List<QuotaItem>();

        if (settings.ShowAntigravityFlash &&
            !items.Any(x => x.IsAvailable && x.Id.StartsWith("agy.gemini_", StringComparison.Ordinal)))
        {
            QuotaItem? session = ParseGeminiSummaryQuota(summaryJson, weekly: false);
            if (session != null)
            {
                items.Add(session);
            }
        }

        QuotaItem? weekly = settings.ShowAntigravityGeminiWeekly
            ? ParseGeminiSummaryQuota(summaryJson, weekly: true) : null;
        if (weekly != null)
        {
            items.Add(weekly);
        }
        else if (settings.ShowAntigravityGeminiWeekly &&
                 items.Any(x => x.IsAvailable && (x.Id.StartsWith("agy.gemini_flash", StringComparison.Ordinal) ||
                                                  x.Id.StartsWith("agy.gemini_argon", StringComparison.Ordinal))))
        {
            items.Add(new QuotaItem
            {
                Id = "agy.gemini_weekly",
                Provider = QuotaProviderType.Antigravity,
                ProviderBadge = "GEMINI",
                BadgeColorHex = "#06B6D4",
                Title = "Gemini Models (7d)",
                IsAvailable = false,
                Subtitle = "Servidor local não fornece cota semanal; abra o Antigravity 2.x",
                SourceDetail = "Requer RetrieveUserQuotaSummary",
                SortOrder = 32
            });
        }

        return items.Count > 0 ? items : CreateOfflineFallback();
    }

    private static bool HasGeminiWeeklyQuota(string? summaryJson) =>
        ParseGeminiSummaryQuota(summaryJson, weekly: true) != null;

    private static QuotaItem? ParseGeminiSummaryQuota(string? summaryJson, bool weekly)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(summaryJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using JsonDocument ownedDocument = document;
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("response", out JsonElement response))
        {
            root = response;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        QuotaItem? mostUsed = null;
        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object ||
                !group.TryGetProperty("displayName", out JsonElement name) ||
                name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string groupName = name.GetString()!;
            // Must belong to Gemini Models (Flash / Pro). Exclude Argon which is tracked separately!
            if (!groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ||
                groupName.Contains("Argon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!group.TryGetProperty("buckets", out JsonElement buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object || !IsMatchingBucket(bucket, weekly))
                {
                    continue;
                }

                JsonElement remaining = bucket.TryGetProperty("remaining", out JsonElement rem) && rem.ValueKind == JsonValueKind.Object
                    ? rem : bucket;
                if ((!remaining.TryGetProperty("remainingFraction", out JsonElement fraction) &&
                     !bucket.TryGetProperty("remainingFraction", out fraction)) ||
                    fraction.ValueKind != JsonValueKind.Number ||
                    !fraction.TryGetDouble(out double value) || !double.IsFinite(value))
                {
                    continue;
                }

                double used = (1.0 - Math.Clamp(value, 0.0, 1.0)) * 100.0;
                if (mostUsed != null && mostUsed.UsedPercent >= used)
                {
                    continue;
                }

                DateTimeOffset? resetTime = ReadResetTime(bucket) ?? ReadResetTime(remaining);
                string? description = bucket.TryGetProperty("description", out JsonElement desc) && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString() : null;

                mostUsed = new QuotaItem
                {
                    Id = weekly ? "agy.gemini_weekly" : "agy.gemini_session",
                    Provider = QuotaProviderType.Antigravity,
                    ProviderBadge = "GEMINI",
                    BadgeColorHex = "#06B6D4",
                    Title = weekly ? "Gemini Models (7d)" : "Gemini Models (5h)",
                    UsedPercent = used,
                    ResetTime = resetTime,
                    Subtitle = string.IsNullOrWhiteSpace(description)
                        ? (weekly ? "Cota semanal compartilhada (inclui Gemini 3.8 Flash)" : "Cota de 5 horas compartilhada")
                        : (weekly ? $"Cota semanal compartilhada (Flash / Pro) • {description}" : $"Cota compartilhada • {description}"),
                    SourceDetail = "Antigravity RetrieveUserQuotaSummary (local)",
                    IsAvailable = true,
                    SortOrder = weekly ? 32 : 31
                };
            }
        }

        return mostUsed;
    }

    private static bool IsMatchingBucket(JsonElement bucket, bool weekly)
    {
        // 1. Direct match on bucketId (e.g. "gemini-weekly", "gemini-5h")
        if (bucket.TryGetProperty("bucketId", out JsonElement bId) && bId.ValueKind == JsonValueKind.String)
        {
            string id = bId.GetString() ?? string.Empty;
            if (weekly && (id.Equals("gemini-weekly", StringComparison.OrdinalIgnoreCase) || id.Contains("weekly", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (!weekly && (id.Equals("gemini-5h", StringComparison.OrdinalIgnoreCase) || id.Contains("5h", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // 2. Match on window or displayName
        string cadence = bucket.TryGetProperty("window", out JsonElement window) && window.ValueKind == JsonValueKind.String
            ? window.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(cadence))
        {
            foreach (string field in new[] { "bucketId", "displayName" })
            {
                if (bucket.TryGetProperty(field, out JsonElement text) && text.ValueKind == JsonValueKind.String)
                {
                    cadence += " " + text.GetString();
                }
            }
        }

        bool isWeekly = cadence.Contains("week", StringComparison.OrdinalIgnoreCase) ||
                        cadence.Contains("7d", StringComparison.OrdinalIgnoreCase) ||
                        cadence.Contains("7-day", StringComparison.OrdinalIgnoreCase) ||
                        cadence.Contains("7 day", StringComparison.OrdinalIgnoreCase);
        bool isSession = cadence.Contains("5h", StringComparison.OrdinalIgnoreCase) ||
                         cadence.Contains("five hour", StringComparison.OrdinalIgnoreCase) ||
                         cadence.Contains("five-hour", StringComparison.OrdinalIgnoreCase);
        return weekly ? isWeekly : isSession;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement element)
    {
        if (!element.TryGetProperty("resetTime", out JsonElement reset))
        {
            return null;
        }

        if (reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        if (reset.ValueKind == JsonValueKind.Number &&
            reset.TryGetInt64(out long unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static IReadOnlyList<QuotaItem> ParseUserStatusResponse(string json, AppSettings settings)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("userStatus", out JsonElement usEl) ||
            !usEl.TryGetProperty("cascadeModelConfigData", out JsonElement cmcdEl) ||
            !cmcdEl.TryGetProperty("clientModelConfigs", out JsonElement configsEl) ||
            configsEl.ValueKind != JsonValueKind.Array)
        {
            return CreateOfflineFallback("Resposta sem clientModelConfigs");
        }

        var rawModels = new List<RawAntigravityModel>();

        foreach (JsonElement itemEl in configsEl.EnumerateArray())
        {
            string? label = itemEl.TryGetProperty("label", out JsonElement labelEl) ? labelEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            if (!itemEl.TryGetProperty("quotaInfo", out JsonElement quotaEl) || quotaEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // In Protobuf 3 JSON, remainingFraction is omitted when equal to 0.0 (100% used).
            double remainingFraction = 0.0;
            if (quotaEl.TryGetProperty("remainingFraction", out JsonElement rfEl) && rfEl.TryGetDouble(out double parsedRf))
            {
                remainingFraction = Math.Clamp(parsedRf, 0.0, 1.0);
            }

            DateTimeOffset? resetTime = null;
            if (quotaEl.TryGetProperty("resetTime", out JsonElement rtEl) &&
                rtEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(rtEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedReset))
            {
                resetTime = parsedReset;
            }

            double usedPercent = Math.Clamp((1.0 - remainingFraction) * 100.0, 0.0, 100.0);
            rawModels.Add(new RawAntigravityModel(label, usedPercent, resetTime));
        }

        var items = new List<QuotaItem>();

        if (settings.GroupAntigravityEffortLevels)
        {
            var grouped = rawModels
                .GroupBy(m => ClassifyFamily(m.Label))
                .OrderBy(g => g.Key.SortOrder);

            foreach (var group in grouped)
            {
                FamilyMetadata meta = group.Key;
                if (!IsFamilyEnabled(meta.Id, settings))
                {
                    continue;
                }

                // Representative item (max used percent in the shared bucket)
                RawAntigravityModel rep = group.OrderByDescending(x => x.UsedPercent).First();
                string variants = string.Join(" / ", group
                    .Select(x => ExtractEffortLevel(x.Label))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(EffortRank));

                string subtitle = !string.IsNullOrEmpty(variants)
                    ? $"Cota compartilhada ({variants})"
                    : "Antigravity IDE Local Server";

                items.Add(new QuotaItem
                {
                    Id = meta.Id,
                    Provider = QuotaProviderType.Antigravity,
                    ProviderBadge = meta.Badge,
                    BadgeColorHex = meta.ColorHex,
                    Title = meta.Id == "agy.gemini_flash" ? $"{meta.DisplayName} (5h)" : meta.DisplayName,
                    UsedPercent = rep.UsedPercent,
                    ResetTime = rep.ResetTime,
                    Subtitle = subtitle,
                    SourceDetail = "Antigravity language_server Connect-RPC",
                    IsAvailable = true,
                    SortOrder = meta.SortOrder
                });
            }
        }
        else
        {
            int idx = 0;
            foreach (RawAntigravityModel raw in rawModels.OrderBy(m => ClassifyFamily(m.Label).SortOrder).ThenBy(m => m.Label))
            {
                FamilyMetadata meta = ClassifyFamily(raw.Label);
                if (!IsFamilyEnabled(meta.Id, settings))
                {
                    continue;
                }

                items.Add(new QuotaItem
                {
                    Id = $"{meta.Id}.{idx++}",
                    Provider = QuotaProviderType.Antigravity,
                    ProviderBadge = meta.Badge,
                    BadgeColorHex = meta.ColorHex,
                    Title = meta.Id == "agy.gemini_flash" ? $"{raw.Label} (5h)" : raw.Label,
                    UsedPercent = raw.UsedPercent,
                    ResetTime = raw.ResetTime,
                    Subtitle = "Antigravity IDE Local Server",
                    SourceDetail = "Antigravity language_server Connect-RPC",
                    IsAvailable = true,
                    SortOrder = meta.SortOrder + idx
                });
            }
        }

        return items;
    }

    private static bool IsFamilyEnabled(string familyId, AppSettings settings)
    {
        return familyId switch
        {
            "agy.gemini_argon" => settings.ShowAntigravityArgon,
            "agy.gemini_flash" => settings.ShowAntigravityFlash,
            "agy.claude_opus" => settings.ShowAntigravityClaudeOpus,
            "agy.claude_sonnet" => settings.ShowAntigravityClaudeSonnet,
            "agy.gpt_oss" => settings.ShowAntigravityGptOss,
            _ => true
        };
    }

    private static FamilyMetadata ClassifyFamily(string label)
    {
        if (label.Contains("Argon", StringComparison.OrdinalIgnoreCase))
        {
            return new FamilyMetadata("agy.gemini_argon", "Gemini 4 Argon", "ARGON", "#3B82F6", 30);
        }

        if (label.Contains("Flash", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = EffortSuffixRegex.Replace(label, "").Trim();
            return new FamilyMetadata("agy.gemini_flash", baseName, "GEMINI", "#06B6D4", 31);
        }

        if (label.Contains("Claude", StringComparison.OrdinalIgnoreCase) &&
            label.Contains("Opus", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = EffortSuffixRegex.Replace(label, "").Trim() + " (AGY)";
            return new FamilyMetadata("agy.claude_opus", baseName, "AGY", "#A855F7", 33);
        }

        if (label.Contains("Claude", StringComparison.OrdinalIgnoreCase) &&
            label.Contains("Sonnet", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = EffortSuffixRegex.Replace(label, "").Trim() + " (AGY)";
            return new FamilyMetadata("agy.claude_sonnet", baseName, "AGY", "#8B5CF6", 34);
        }

        if (label.Contains("GPT", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = EffortSuffixRegex.Replace(label, "").Trim() + " (AGY)";
            return new FamilyMetadata("agy.gpt_oss", baseName, "AGY", "#64748B", 35);
        }

        string clean = EffortSuffixRegex.Replace(label, "").Trim();
        string slug = clean.ToLowerInvariant().Replace(' ', '_');
        return new FamilyMetadata($"agy.{slug}", clean, "AGY", "#3B82F6", 39);
    }

    private static string ExtractEffortLevel(string label)
    {
        Match m = EffortSuffixRegex.Match(label);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static int EffortRank(string effort)
    {
        if (effort.Equals("Low", StringComparison.OrdinalIgnoreCase)) return 1;
        if (effort.Equals("Medium", StringComparison.OrdinalIgnoreCase)) return 2;
        if (effort.Equals("High", StringComparison.OrdinalIgnoreCase)) return 3;
        return 9;
    }

    private static IReadOnlyList<QuotaItem> CreateOfflineFallback(string? detail = null)
    {
        return new[]
        {
            new QuotaItem
            {
                Id = "agy.gemini_argon",
                Provider = QuotaProviderType.Antigravity,
                ProviderBadge = "ARGON",
                BadgeColorHex = "#3B82F6",
                Title = "Gemini 4 Argon",
                UsedPercent = 0,
                IsAvailable = false,
                Subtitle = "Antigravity fechado (language_server offline)",
                SourceDetail = detail ?? "Inicie o Antigravity para ler as cotas locais",
                SortOrder = 30
            }
        };
    }

    private sealed record RawAntigravityModel(string Label, double UsedPercent, DateTimeOffset? ResetTime);
    private sealed record FamilyMetadata(string Id, string DisplayName, string Badge, string ColorHex, int SortOrder);
}
