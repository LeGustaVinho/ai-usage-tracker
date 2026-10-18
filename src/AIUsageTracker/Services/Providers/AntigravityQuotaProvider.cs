using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
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

        // 1. Try local language_server (when Antigravity IDE is open and running)
        try
        {
            QuotaResponses? responses = await QueryLanguageServerAsync(settings.ShowAntigravityGeminiWeekly, cancellationToken);
            if (responses != null)
            {
                var liveItems = ParseQuotaResponses(responses.UserStatusJson, responses.SummaryJson, settings);
                if (liveItems.Count > 0 && liveItems.Any(x => x.IsAvailable))
                {
                    SaveLastKnownQuotas(liveItems);
                    return liveItems;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Antigravity LanguageServer query failed: {ex.Message}");
        }

        // 2. If Antigravity IDE is closed, try to get quotas via CLI (agy /quota)
        if (settings.EnableAntigravityCliFallback)
        {
            try
            {
                var cliItems = await FetchQuotasFromCliAsync(settings, cancellationToken);
                if (cliItems != null && cliItems.Count > 0 && cliItems.Any(x => x.IsAvailable))
                {
                    SaveLastKnownQuotas(cliItems);
                    return cliItems;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Antigravity CLI query failed: {ex.Message}");
            }
        }

        // 3. Fallback: instead of showing offline, show the last known quota
        var lastKnown = GetLastKnownQuotas(settings);
        if (lastKnown != null && lastKnown.Count > 0)
        {
            return lastKnown;
        }

        // 4. Fallback if never connected before
        return CreateOfflineFallback(null, settings);
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
                Title = "Gemini 3.8 Flash (7d)",
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
                    Title = weekly ? "Gemini 3.8 Flash (7d)" : "Gemini 3.8 Flash (5h)",
                    UsedPercent = used,
                    ResetTime = resetTime,
                    Subtitle = string.IsNullOrWhiteSpace(description)
                        ? (weekly ? "Cota semanal Gemini 3.8 Flash" : "Cota de 5 horas Gemini 3.8 Flash")
                        : (weekly ? $"Cota semanal Gemini 3.8 Flash • {description}" : $"Cota Gemini 3.8 Flash • {description}"),
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
            "agy.gemini_weekly" => settings.ShowAntigravityGeminiWeekly,
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

    private static DateTimeOffset? _lastCliFetchTime;
    private static IReadOnlyList<QuotaItem>? _lastCliCachedItems;
    private static readonly SemaphoreSlim _cliLock = new(1, 1);

    internal static async Task<IReadOnlyList<QuotaItem>?> FetchQuotasFromCliAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        // Rate limit CLI executions: if executed less than 20 seconds ago, reuse the cached CLI items
        if (_lastCliFetchTime.HasValue &&
            DateTimeOffset.UtcNow - _lastCliFetchTime.Value < TimeSpan.FromSeconds(20) &&
            _lastCliCachedItems != null)
        {
            return FilterCliItemsBySettings(_lastCliCachedItems, settings);
        }

        if (!await _cliLock.WaitAsync(100, cancellationToken))
        {
            return _lastCliCachedItems != null ? FilterCliItemsBySettings(_lastCliCachedItems, settings) : null;
        }

        try
        {
            string agyPath = settings.GetEffectiveAgyCliPath();

            var psi = new ProcessStartInfo
            {
                FileName = agyPath,
                Arguments = "-p \"/quota\" --output-format json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return null;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(8));

            string output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var parsed = ParseCliQuotaResponse(output, settings);
                if (parsed.Count > 0)
                {
                    _lastCliFetchTime = DateTimeOffset.UtcNow;
                    _lastCliCachedItems = parsed;
                    return parsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Process timed out or cancelled
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Antigravity CLI fetch failed: {ex.Message}");
        }
        finally
        {
            _cliLock.Release();
        }

        return _lastCliCachedItems != null ? FilterCliItemsBySettings(_lastCliCachedItems, settings) : null;
    }

    private static IReadOnlyList<QuotaItem> FilterCliItemsBySettings(IReadOnlyList<QuotaItem> items, AppSettings settings)
    {
        return items.Where(x => IsFamilyEnabled(x.Id, settings)).OrderBy(x => x.SortOrder).ToList();
    }

    internal static IReadOnlyList<QuotaItem> ParseCliQuotaResponse(string json, AppSettings settings)
    {
        var items = new List<QuotaItem>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return items;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("command", out JsonElement cmd) ||
                !cmd.TryGetProperty("data", out JsonElement data) ||
                !data.TryGetProperty("groups", out JsonElement groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                return items;
            }

            foreach (JsonElement group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object ||
                    !group.TryGetProperty("name", out JsonElement nameEl))
                {
                    continue;
                }

                string groupName = nameEl.GetString() ?? string.Empty;
                if (!group.TryGetProperty("buckets", out JsonElement buckets) || buckets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement bucket in buckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string bucketId = bucket.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
                    string window = bucket.TryGetProperty("window", out JsonElement winEl) ? winEl.GetString() ?? "" : "";
                    string desc = bucket.TryGetProperty("description", out JsonElement descEl) ? descEl.GetString() ?? "" : "";

                    double remainingFraction = 0.0;
                    if (bucket.TryGetProperty("remaining_fraction", out JsonElement rfEl) && rfEl.TryGetDouble(out double rf))
                    {
                        remainingFraction = Math.Clamp(rf, 0.0, 1.0);
                    }
                    double usedPercent = Math.Clamp((1.0 - remainingFraction) * 100.0, 0.0, 100.0);

                    DateTimeOffset? resetTime = null;
                    if (bucket.TryGetProperty("reset_time", out JsonElement rtEl) && rtEl.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(rtEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedRt))
                    {
                        resetTime = parsedRt;
                    }

                    // 1. Argon
                    if (groupName.Contains("Argon", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("argon", StringComparison.OrdinalIgnoreCase))
                    {
                        if (settings.ShowAntigravityArgon)
                        {
                            items.Add(new QuotaItem
                            {
                                Id = "agy.gemini_argon",
                                Provider = QuotaProviderType.Antigravity,
                                ProviderBadge = "ARGON",
                                BadgeColorHex = "#3B82F6",
                                Title = "Gemini 4 Argon",
                                UsedPercent = usedPercent,
                                ResetTime = resetTime,
                                Subtitle = string.IsNullOrWhiteSpace(desc) ? "Cota compartilhada (CLI)" : $"Cota compartilhada • {desc}",
                                SourceDetail = "Antigravity CLI (agy /quota)",
                                IsAvailable = true,
                                SortOrder = 30
                            });
                        }
                    }
                    // 2. Gemini Models (Flash)
                    else if (groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isWeekly = bucketId.Equals("gemini-weekly", StringComparison.OrdinalIgnoreCase) || window.Equals("weekly", StringComparison.OrdinalIgnoreCase);
                        bool isSession = bucketId.Equals("gemini-5h", StringComparison.OrdinalIgnoreCase) || window.Equals("5h", StringComparison.OrdinalIgnoreCase);

                        if (isSession && settings.ShowAntigravityFlash)
                        {
                            items.Add(new QuotaItem
                            {
                                Id = "agy.gemini_flash",
                                Provider = QuotaProviderType.Antigravity,
                                ProviderBadge = "GEMINI",
                                BadgeColorHex = "#06B6D4",
                                Title = "Gemini 3.8 Flash (5h)",
                                UsedPercent = usedPercent,
                                ResetTime = resetTime,
                                Subtitle = string.IsNullOrWhiteSpace(desc) ? "Cota de 5 horas Gemini 3.8 Flash (CLI)" : $"Cota Gemini 3.8 Flash • {desc}",
                                SourceDetail = "Antigravity CLI (agy /quota)",
                                IsAvailable = true,
                                SortOrder = 31
                            });
                        }
                        else if (isWeekly && settings.ShowAntigravityGeminiWeekly)
                        {
                            items.Add(new QuotaItem
                            {
                                Id = "agy.gemini_weekly",
                                Provider = QuotaProviderType.Antigravity,
                                ProviderBadge = "GEMINI",
                                BadgeColorHex = "#06B6D4",
                                Title = "Gemini 3.8 Flash (7d)",
                                UsedPercent = usedPercent,
                                ResetTime = resetTime,
                                Subtitle = string.IsNullOrWhiteSpace(desc) ? "Cota semanal Gemini 3.8 Flash (CLI)" : $"Cota semanal Gemini 3.8 Flash • {desc}",
                                SourceDetail = "Antigravity CLI (agy /quota)",
                                IsAvailable = true,
                                SortOrder = 32
                            });
                        }
                    }
                    // 3. Claude and GPT models (3P)
                    else if (groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isWeekly = bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase) || window.Equals("weekly", StringComparison.OrdinalIgnoreCase);
                        if (isWeekly)
                        {
                            if (settings.ShowAntigravityClaudeOpus)
                            {
                                items.Add(new QuotaItem
                                {
                                    Id = "agy.claude_opus",
                                    Provider = QuotaProviderType.Antigravity,
                                    ProviderBadge = "AGY",
                                    BadgeColorHex = "#A855F7",
                                    Title = "Claude Opus 5.5 (AGY)",
                                    UsedPercent = usedPercent,
                                    ResetTime = resetTime,
                                    Subtitle = string.IsNullOrWhiteSpace(desc) ? "Cota compartilhada 3P (CLI)" : $"Cota 3P • {desc}",
                                    SourceDetail = "Antigravity CLI (agy /quota)",
                                    IsAvailable = true,
                                    SortOrder = 33
                                });
                            }
                            if (settings.ShowAntigravityClaudeSonnet)
                            {
                                items.Add(new QuotaItem
                                {
                                    Id = "agy.claude_sonnet",
                                    Provider = QuotaProviderType.Antigravity,
                                    ProviderBadge = "AGY",
                                    BadgeColorHex = "#8B5CF6",
                                    Title = "Claude Sonnet 5.5 (AGY)",
                                    UsedPercent = usedPercent,
                                    ResetTime = resetTime,
                                    Subtitle = string.IsNullOrWhiteSpace(desc) ? "Cota compartilhada 3P (CLI)" : $"Cota 3P • {desc}",
                                    SourceDetail = "Antigravity CLI (agy /quota)",
                                    IsAvailable = true,
                                    SortOrder = 34
                                });
                            }
                            if (settings.ShowAntigravityGptOss)
                            {
                                items.Add(new QuotaItem
                                {
                                    Id = "agy.gpt_oss",
                                    Provider = QuotaProviderType.Antigravity,
                                    ProviderBadge = "AGY",
                                    BadgeColorHex = "#64748B",
                                    Title = "GPT-OSS 120B (AGY)",
                                    UsedPercent = usedPercent,
                                    ResetTime = resetTime,
                                    Subtitle = string.IsNullOrWhiteSpace(desc) ? "Cota compartilhada 3P (CLI)" : $"Cota 3P • {desc}",
                                    SourceDetail = "Antigravity CLI (agy /quota)",
                                    IsAvailable = true,
                                    SortOrder = 35
                                });
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to parse CLI quota JSON: {ex.Message}");
        }

        return items.OrderBy(x => x.SortOrder).ToList();
    }

    public sealed record CachedQuotaRecord(
        DateTimeOffset CachedAt,
        List<QuotaItem> Items
    );

    private static CachedQuotaRecord? _inMemoryCache;

    public static string CustomCacheFilePath { get; set; } = string.Empty;

    internal static string GetCacheFilePath()
    {
        if (!string.IsNullOrWhiteSpace(CustomCacheFilePath))
        {
            return CustomCacheFilePath;
        }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "AIUsageTracker");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "antigravity_last_known_quota.json");
    }

    internal static void SaveLastKnownQuotas(IReadOnlyList<QuotaItem> items)
    {
        if (items == null || items.Count == 0) return;
        var validItems = items.Where(x => x.IsAvailable).ToList();
        if (validItems.Count == 0) return;

        var record = new CachedQuotaRecord(DateTimeOffset.Now, validItems);
        _inMemoryCache = record;

        try
        {
            string cachePath = GetCacheFilePath();
            string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(cachePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to persist Antigravity quota cache: {ex.Message}");
        }
    }

    internal static IReadOnlyList<QuotaItem>? GetLastKnownQuotas(AppSettings settings)
    {
        CachedQuotaRecord? record = _inMemoryCache;
        if (record == null)
        {
            try
            {
                string cachePath = GetCacheFilePath();
                if (File.Exists(cachePath))
                {
                    string json = File.ReadAllText(cachePath);
                    record = JsonSerializer.Deserialize<CachedQuotaRecord>(json);
                    _inMemoryCache = record;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load Antigravity quota cache: {ex.Message}");
            }
        }

        if (record == null || record.Items == null || record.Items.Count == 0)
        {
            return null;
        }

        string timeStr = record.CachedAt.ToString("HH:mm");
        var result = new List<QuotaItem>();

        foreach (QuotaItem item in record.Items)
        {
            if (!IsFamilyEnabled(item.Id, settings))
            {
                continue;
            }

            string cleanSubtitle = item.Subtitle ?? string.Empty;
            int separatorIdx = cleanSubtitle.IndexOf("•", StringComparison.Ordinal);
            string baseDesc = separatorIdx >= 0 ? cleanSubtitle.Substring(0, separatorIdx).Trim() : cleanSubtitle;
            if (string.IsNullOrWhiteSpace(baseDesc))
            {
                baseDesc = item.Title;
            }

            result.Add(new QuotaItem
            {
                Id = item.Id,
                Provider = item.Provider,
                ProviderBadge = item.ProviderBadge,
                BadgeColorHex = item.BadgeColorHex,
                Title = item.Title,
                UsedPercent = item.UsedPercent,
                ResetTime = item.ResetTime,
                IsAvailable = true,
                Subtitle = $"{baseDesc} • Última cota conhecida ({timeStr})",
                SourceDetail = $"Última cota conhecida salva às {timeStr} • Antigravity fechado",
                SortOrder = item.SortOrder
            });
        }

        return result.Count > 0 ? result.OrderBy(x => x.SortOrder).ToList() : null;
    }

    internal static void ClearCache()
    {
        _inMemoryCache = null;
        _lastCliCachedItems = null;
        _lastCliFetchTime = null;
    }

    private static IReadOnlyList<QuotaItem> CreateOfflineFallback(string? detail = null, AppSettings? settings = null)
    {
        var items = new List<QuotaItem>();
        if (settings == null || settings.ShowAntigravityArgon)
        {
            items.Add(new QuotaItem
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
            });
        }
        if (settings?.ShowAntigravityFlash == true)
        {
            items.Add(new QuotaItem
            {
                Id = "agy.gemini_flash",
                Provider = QuotaProviderType.Antigravity,
                ProviderBadge = "GEMINI",
                BadgeColorHex = "#06B6D4",
                Title = "Gemini 3.8 Flash (5h)",
                UsedPercent = 0,
                IsAvailable = false,
                Subtitle = "Antigravity fechado (language_server offline)",
                SourceDetail = detail ?? "Inicie o Antigravity para ler as cotas locais",
                SortOrder = 31
            });
        }
        if (settings?.ShowAntigravityGeminiWeekly == true)
        {
            items.Add(new QuotaItem
            {
                Id = "agy.gemini_weekly",
                Provider = QuotaProviderType.Antigravity,
                ProviderBadge = "GEMINI",
                BadgeColorHex = "#06B6D4",
                Title = "Gemini 3.8 Flash (7d)",
                UsedPercent = 0,
                IsAvailable = false,
                Subtitle = "Antigravity fechado (language_server offline)",
                SourceDetail = detail ?? "Inicie o Antigravity para ler as cotas locais",
                SortOrder = 32
            });
        }
        return items.Count > 0 ? items : new[]
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
