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

    private CachedEndpoint? _cachedEndpoint;

    public QuotaProviderType ProviderType => QuotaProviderType.Antigravity;

    private sealed record CachedEndpoint(int ProcessId, string BaseUrl, string CsrfToken);

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
            string? responseJson = await QueryLanguageServerAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return CreateOfflineFallback();
            }

            return ParseUserStatusResponse(responseJson, settings);
        }
        catch (Exception ex)
        {
            return CreateOfflineFallback(ex.Message);
        }
    }

    private async Task<string?> QueryLanguageServerAsync(CancellationToken cancellationToken)
    {
        // 1. Try cached endpoint first if process is still alive
        if (_cachedEndpoint != null)
        {
            string? cachedResult = await TryCallGetUserStatusAsync(_cachedEndpoint.BaseUrl, _cachedEndpoint.CsrfToken, cancellationToken);
            if (!string.IsNullOrWhiteSpace(cachedResult))
            {
                return cachedResult;
            }
            _cachedEndpoint = null;
        }

        // 2. Discover running language_server processes
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
                        string? json = await TryCallGetUserStatusAsync(baseUrl, csrfToken, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            _cachedEndpoint = new CachedEndpoint(proc.Id, baseUrl, csrfToken);
                            return json;
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

        return null;
    }

    private static async Task<string?> TryCallGetUserStatusAsync(string baseUrl, string csrfToken, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"{baseUrl}/exa.language_server_pb.LanguageServerService/GetUserStatus";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", csrfToken);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Content = new StringContent(RequestBodyJson, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (content.Contains("clientModelConfigs", StringComparison.Ordinal))
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
                    Title = meta.DisplayName,
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
                    Title = raw.Label,
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
            return new FamilyMetadata("agy.claude_opus", baseName, "AGY", "#A855F7", 32);
        }

        if (label.Contains("Claude", StringComparison.OrdinalIgnoreCase) &&
            label.Contains("Sonnet", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = EffortSuffixRegex.Replace(label, "").Trim() + " (AGY)";
            return new FamilyMetadata("agy.claude_sonnet", baseName, "AGY", "#8B5CF6", 33);
        }

        if (label.Contains("GPT", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = EffortSuffixRegex.Replace(label, "").Trim() + " (AGY)";
            return new FamilyMetadata("agy.gpt_oss", baseName, "AGY", "#64748B", 34);
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
