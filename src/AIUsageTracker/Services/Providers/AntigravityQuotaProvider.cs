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
    private const string CarbonQuotaId = "agy.gemini_carbon";
    private const string LegacyCarbonQuotaId = "agy.gemini_4_carbon";
    private const string CarbonTitle = "Gemini 4 Carbon";
    private const string CliSourceDetail = "Antigravity CLI (agy /quota)";

    private readonly Func<IEnumerable<LanguageServerEndpoint>> _discoverEndpoints;
    private readonly Func<LanguageServerEndpoint, bool, CancellationToken, Task<QuotaResponses?>> _fetchEndpointAsync;
    private LanguageServerEndpoint? _cachedEndpoint;

    public QuotaProviderType ProviderType => QuotaProviderType.Antigravity;

    internal sealed record LanguageServerEndpoint(int ProcessId, string BaseUrl, string CsrfToken);
    internal sealed record QuotaResponses(string? UserStatusJson, string? SummaryJson);

    public AntigravityQuotaProvider() : this(DiscoverLanguageServerEndpoints, TryFetchEndpointAsync)
    {
    }

    internal AntigravityQuotaProvider(
        Func<IEnumerable<LanguageServerEndpoint>> discoverEndpoints,
        Func<LanguageServerEndpoint, bool, CancellationToken, Task<QuotaResponses?>> fetchEndpointAsync)
    {
        _discoverEndpoints = discoverEndpoints;
        _fetchEndpointAsync = fetchEndpointAsync;
    }

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
            var liveItems = await QueryLanguageServerAsync(settings, cancellationToken);
            if (liveItems.Count > 0)
            {
                SaveLastKnownQuotas(liveItems, preserveHiddenCarbon: !settings.ShowAntigravityCarbon);
                return WithCarbonPlaceholder(liveItems, settings);
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
                if (cliItems is { Count: > 0 })
                {
                    var visibleItems = FilterCliItemsBySettings(cliItems, settings);
                    if (visibleItems.Count > 0)
                    {
                        SaveLastKnownQuotas(cliItems, preserveHiddenCarbon: !settings.ShowAntigravityCarbon);
                        return WithCarbonPlaceholder(visibleItems, settings);
                    }
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
            return WithCarbonPlaceholder(lastKnown, settings);
        }

        // 4. Fallback if never connected before
        return WithCarbonPlaceholder(CreateOfflineFallback(settings), settings);
    }

    private async Task<IReadOnlyList<QuotaItem>> QueryLanguageServerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        // Prefer an endpoint that provides both quotas. Carbon breaks a tie between partial responses.
        const int flashWeeklyScore = 1;
        const int carbonScore = 2;
        int requestedScore = (settings.ShowAntigravityGeminiWeekly ? flashWeeklyScore : 0) +
                             (settings.ShowAntigravityCarbon ? carbonScore : 0);
        LanguageServerEndpoint? cachedEndpoint = _cachedEndpoint;
        LanguageServerEndpoint? bestEndpoint = null;
        IReadOnlyList<QuotaItem> bestItems = Array.Empty<QuotaItem>();
        int bestScore = -1;

        if (cachedEndpoint != null && await TryEndpointAsync(cachedEndpoint))
        {
            return bestItems;
        }

        foreach (LanguageServerEndpoint endpoint in _discoverEndpoints())
        {
            if (endpoint != cachedEndpoint && await TryEndpointAsync(endpoint))
            {
                break;
            }
        }

        _cachedEndpoint = bestEndpoint;
        return bestItems;

        async Task<bool> TryEndpointAsync(LanguageServerEndpoint endpoint)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuotaResponses? response = await _fetchEndpointAsync(endpoint, requestedScore != 0, cancellationToken);
            if (response == null)
            {
                return false;
            }

            var items = ParseQuotaResponses(response.UserStatusJson, response.SummaryJson, settings);
            if (!items.Any(item => item.IsAvailable))
            {
                return false;
            }

            int score = 0;
            if (settings.ShowAntigravityGeminiWeekly && items.Any(item => item.IsAvailable && item.Id == "agy.gemini_weekly"))
            {
                score += flashWeeklyScore;
            }
            if (settings.ShowAntigravityCarbon && items.Any(item => item.IsAvailable && item.Id == CarbonQuotaId))
            {
                score += carbonScore;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestEndpoint = endpoint;
                bestItems = items;
            }
            return score == requestedScore;
        }
    }

    private static IEnumerable<LanguageServerEndpoint> DiscoverLanguageServerEndpoints()
    {
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
                    yield return new LanguageServerEndpoint(proc.Id, $"http://127.0.0.1:{port}", csrfToken);
                    yield return new LanguageServerEndpoint(proc.Id, $"https://127.0.0.1:{port}", csrfToken);
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
    }

    private static async Task<QuotaResponses?> TryFetchEndpointAsync(LanguageServerEndpoint endpoint, bool requestSummary, CancellationToken cancellationToken)
    {
        string? status = await TryCallRpcAsync(endpoint.BaseUrl, endpoint.CsrfToken, "GetUserStatus", "clientModelConfigs", cancellationToken);
        string? summary = requestSummary
            ? await TryCallRpcAsync(endpoint.BaseUrl, endpoint.CsrfToken, "RetrieveUserQuotaSummary", "\"groups\"", cancellationToken)
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
        using JsonDocument? summary = TryParseDocument(summaryJson);
        JsonElement groups = ReadSummaryGroups(summary?.RootElement ?? default);
        var items = ParseUserStatusResponse(statusJson, settings, out QuotaItem? carbonFromStatus);

        if (settings.ShowAntigravityFlash &&
            !items.Any(x => x.IsAvailable && x.Id.StartsWith("agy.gemini_", StringComparison.Ordinal)))
        {
            QuotaItem? session = ParseGeminiSummaryQuota(groups, weekly: false);
            if (session != null)
            {
                items.Add(session);
            }
        }

        QuotaItem? weekly = settings.ShowAntigravityGeminiWeekly
            ? ParseGeminiSummaryQuota(groups, weekly: true) : null;
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

        if (settings.ShowAntigravityCarbon &&
            (ParseCarbonBuckets(groups, "Antigravity RetrieveUserQuotaSummary (local)") ?? carbonFromStatus) is { } carbon)
        {
            items.Add(carbon);
        }

        return items;
    }

    private static JsonDocument? TryParseDocument(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ReadSummaryGroups(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out JsonElement response))
        {
            root = response;
        }

        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("groups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array
            ? groups : default;
    }

    private static QuotaItem? ParseGeminiSummaryQuota(JsonElement groups, bool weekly)
    {
        if (groups.ValueKind != JsonValueKind.Array)
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
            // Carbon and Argon have their own indicators.
            if (!groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ||
                groupName.Contains("Argon", StringComparison.OrdinalIgnoreCase) || IsCarbonGroup(group))
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
                if (bucket.ValueKind != JsonValueKind.Object || IsCarbonBucket(bucket) || !IsMatchingBucket(bucket, weekly))
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
                    WindowDuration = weekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5),
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
        string? id = ReadBucketId(bucket);
        if (id != null)
        {
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
        string? cadence = ReadStringProperty(bucket, "window");
        if (string.IsNullOrWhiteSpace(cadence))
        {
            cadence = $"{id} {ReadBucketName(bucket)}";
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
        if (!element.TryGetProperty("resetTime", out JsonElement reset) &&
            !element.TryGetProperty("reset_time", out reset))
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

    private static string? ReadStringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static string? ReadBucketId(JsonElement bucket) =>
        ReadStringProperty(bucket, "bucketId") ?? ReadStringProperty(bucket, "id");

    private static string? ReadBucketName(JsonElement bucket) =>
        ReadStringProperty(bucket, "displayName") ?? ReadStringProperty(bucket, "name");

    private static bool IsCarbonLabel(string? label) =>
        label != null && EffortSuffixRegex.Replace(label.Trim(), "").Equals(CarbonTitle, StringComparison.OrdinalIgnoreCase);

    private static bool IsCarbonId(string? id) =>
        IsIdOrVariant(id, "gemini-4-carbon") || IsIdOrVariant(id, "gemini-carbon");

    private static bool IsIdOrVariant(string? id, string baseId) =>
        string.Equals(id, baseId, StringComparison.OrdinalIgnoreCase) ||
        id?.StartsWith(baseId + "-", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCarbonGroup(JsonElement group) =>
        IsCarbonLabel(ReadBucketName(group)) ||
        IsCarbonId(ReadStringProperty(group, "id"));

    private static bool IsCarbonBucket(JsonElement bucket)
    {
        string? name = ReadBucketName(bucket);
        return IsCarbonId(ReadBucketId(bucket)) || IsCarbonLabel(name) ||
            name?.StartsWith(CarbonTitle + " Weekly Limit", StringComparison.OrdinalIgnoreCase) == true ||
            name?.StartsWith(CarbonTitle + " Five Hour Limit", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryGetRemainingFraction(JsonElement element, out JsonElement fraction) =>
        element.TryGetProperty("remainingFraction", out fraction) || element.TryGetProperty("remaining_fraction", out fraction);

    private static bool TryReadRemainingFraction(JsonElement fraction, out double value)
    {
        value = 0;
        return fraction.ValueKind == JsonValueKind.Number && fraction.TryGetDouble(out value) &&
            double.IsFinite(value) && value >= 0 && value <= 1;
    }

    private static TimeSpan? ReadBucketDuration(JsonElement bucket)
    {
        if (IsMatchingBucket(bucket, weekly: true))
        {
            return TimeSpan.FromDays(7);
        }

        return IsMatchingBucket(bucket, weekly: false) ? TimeSpan.FromHours(5) : null;
    }

    private static QuotaItem? ParseCarbonBuckets(JsonElement groups, string sourceDetail)
    {
        if (groups.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var carbonQuotas = new List<QuotaItem>();
        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object ||
                !group.TryGetProperty("buckets", out JsonElement buckets) || buckets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            bool isCarbonGroup = IsCarbonGroup(group);
            foreach (JsonElement bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object || !(isCarbonGroup || IsCarbonBucket(bucket)))
                {
                    continue;
                }

                JsonElement remaining = bucket.TryGetProperty("remaining", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested : bucket;
                if ((!TryGetRemainingFraction(remaining, out JsonElement fraction) && !TryGetRemainingFraction(bucket, out fraction)) ||
                    !TryReadRemainingFraction(fraction, out double remainingFraction))
                {
                    continue;
                }

                carbonQuotas.Add(CreateCarbonQuota(remainingFraction, ReadResetTime(bucket) ?? ReadResetTime(remaining),
                    ReadBucketDuration(bucket), sourceDetail));
            }
        }

        return carbonQuotas.OrderByDescending(item => item.WindowDuration)
            .ThenByDescending(item => item.UsedPercent).FirstOrDefault();
    }

    private static QuotaItem? ParseCarbonStatusQuota(JsonElement config, string? modelId)
    {
        if ((config.TryGetProperty("disabled", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True) ||
            modelId?.EndsWith("-disabled", StringComparison.OrdinalIgnoreCase) == true ||
            !config.TryGetProperty("quotaInfo", out JsonElement quota) || quota.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        DateTimeOffset? resetTime = ReadResetTime(quota);
        double remainingFraction = 0;
        if (TryGetRemainingFraction(quota, out JsonElement fraction))
        {
            if (!TryReadRemainingFraction(fraction, out remainingFraction))
            {
                return null;
            }
        }
        else if (resetTime == null)
        {
            // An omitted protobuf fraction represents zero only with evidence of a quota window.
            return null;
        }

        return CreateCarbonQuota(remainingFraction, resetTime, ReadBucketDuration(quota),
            "Antigravity language_server Connect-RPC");
    }

    private static QuotaItem CreateCarbonQuota(double? remainingFraction, DateTimeOffset? resetTime = null,
        TimeSpan? duration = null, string? sourceDetail = null) => new()
    {
        Id = CarbonQuotaId,
        Provider = QuotaProviderType.Antigravity,
        ProviderBadge = "CARBON",
        BadgeColorHex = "#3B82F6",
        Title = CarbonTitle,
        UsedPercent = (1 - (remainingFraction ?? 1)) * 100,
        ResetTime = resetTime,
        WindowDuration = duration,
        AllowWindowDurationInference = false,
        SourceDetail = sourceDetail,
        IsAvailable = remainingFraction.HasValue,
        UnavailableMessage = remainingFraction.HasValue ? null : "Sem cota disponível",
        SortOrder = 36
    };

    internal static IReadOnlyList<QuotaItem> WithCarbonPlaceholder(IReadOnlyList<QuotaItem> items, AppSettings settings)
    {
        return settings.ShowAntigravityCarbon && !items.Any(item => item.Id == CarbonQuotaId)
            ? items.Append(CreateCarbonQuota(null)).ToList() : items;
    }

    private static List<QuotaItem> ParseUserStatusResponse(string? json, AppSettings settings, out QuotaItem? carbonQuota)
    {
        carbonQuota = null;
        var items = new List<QuotaItem>();
        using JsonDocument? doc = TryParseDocument(json);
        JsonElement root = doc?.RootElement ?? default;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("userStatus", out JsonElement usEl) || usEl.ValueKind != JsonValueKind.Object ||
            !usEl.TryGetProperty("cascadeModelConfigData", out JsonElement cmcdEl) || cmcdEl.ValueKind != JsonValueKind.Object ||
            !cmcdEl.TryGetProperty("clientModelConfigs", out JsonElement configsEl) ||
            configsEl.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        var rawModels = new List<RawAntigravityModel>();

        foreach (JsonElement itemEl in configsEl.EnumerateArray())
        {
            string? label = ReadStringProperty(itemEl, "label");
            string? modelId = ReadStringProperty(itemEl, "modelId");
            if (IsCarbonLabel(label) || IsCarbonId(modelId))
            {
                if (settings.ShowAntigravityCarbon && ParseCarbonStatusQuota(itemEl, modelId) is { } candidate &&
                    (carbonQuota == null || candidate.UsedPercent > carbonQuota.UsedPercent))
                {
                    carbonQuota = candidate;
                }
                continue;
            }

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
            if (quotaEl.TryGetProperty("remainingFraction", out JsonElement rfEl) && rfEl.ValueKind == JsonValueKind.Number &&
                rfEl.TryGetDouble(out double parsedRf) && double.IsFinite(parsedRf))
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
                    WindowDuration = meta.Id == "agy.gemini_flash" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7),
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
                    WindowDuration = meta.Id == "agy.gemini_flash" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7),
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
        if (familyId.StartsWith(LegacyCarbonQuotaId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return familyId switch
        {
            "agy.gemini_argon" => settings.ShowAntigravityArgon,
            CarbonQuotaId => settings.ShowAntigravityCarbon,
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

    private static CachedQuotaRecord? _lastCliCache;
    private static readonly SemaphoreSlim _cliLock = new(1, 1);

    internal static async Task<IReadOnlyList<QuotaItem>?> FetchQuotasFromCliAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        // Rate limit CLI executions: if executed less than 20 seconds ago, reuse the cached CLI items
        CachedQuotaRecord? cached = _lastCliCache;
        if (cached != null && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(20))
        {
            return cached.Items;
        }

        if (!await _cliLock.WaitAsync(100, cancellationToken))
        {
            return null;
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
                var parsed = ReadCliQuotaItems(output);
                if (parsed.Count > 0)
                {
                    _lastCliCache = new CachedQuotaRecord(DateTimeOffset.UtcNow, parsed);
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

        return null;
    }

    private static IReadOnlyList<QuotaItem> FilterCliItemsBySettings(IReadOnlyList<QuotaItem> items, AppSettings settings)
    {
        return items.Where(x => IsFamilyEnabled(x.Id, settings)).OrderBy(x => x.SortOrder).ToList();
    }

    internal static IReadOnlyList<QuotaItem> ParseCliQuotaResponse(string json, AppSettings settings) =>
        FilterCliItemsBySettings(ReadCliQuotaItems(json), settings);

    private static List<QuotaItem> ReadCliQuotaItems(string json)
    {
        var items = new List<QuotaItem>();
        using JsonDocument? doc = TryParseDocument(json);
        JsonElement root = doc?.RootElement ?? default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("command", out JsonElement command) || command.ValueKind != JsonValueKind.Object ||
            !command.TryGetProperty("data", out JsonElement data))
        {
            return items;
        }

        JsonElement groups = ReadSummaryGroups(data);
        if (groups.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        if (ParseCarbonBuckets(groups, CliSourceDetail) is { } carbon)
        {
            items.Add(carbon);
        }

        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || IsCarbonGroup(group) ||
                !group.TryGetProperty("buckets", out JsonElement buckets) || buckets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            string groupName = ReadBucketName(group) ?? string.Empty;
            foreach (JsonElement bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object || IsCarbonBucket(bucket))
                {
                    continue;
                }

                string bucketId = ReadBucketId(bucket) ?? string.Empty;
                string window = ReadStringProperty(bucket, "window") ?? string.Empty;
                string? desc = ReadStringProperty(bucket, "description");
                double remainingFraction = 0;
                if (TryGetRemainingFraction(bucket, out JsonElement fraction) && fraction.ValueKind == JsonValueKind.Number &&
                    fraction.TryGetDouble(out double parsed) && double.IsFinite(parsed))
                {
                    remainingFraction = Math.Clamp(parsed, 0, 1);
                }

                double usedPercent = (1 - remainingFraction) * 100;
                DateTimeOffset? resetTime = ReadResetTime(bucket);
                void AddQuota(FamilyMetadata model, TimeSpan duration, string subtitle)
                {
                    items.Add(new QuotaItem
                    {
                        Id = model.Id,
                        Provider = QuotaProviderType.Antigravity,
                        ProviderBadge = model.Badge,
                        BadgeColorHex = model.ColorHex,
                        Title = model.DisplayName,
                        UsedPercent = usedPercent,
                        ResetTime = resetTime,
                        WindowDuration = duration,
                        Subtitle = subtitle,
                        SourceDetail = CliSourceDetail,
                        SortOrder = model.SortOrder
                    });
                }

                if (groupName.Contains("Argon", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("argon", StringComparison.OrdinalIgnoreCase))
                {
                    AddQuota(new FamilyMetadata("agy.gemini_argon", "Gemini 4 Argon", "ARGON", "#3B82F6", 30),
                        TimeSpan.FromDays(7), string.IsNullOrWhiteSpace(desc) ? "Cota compartilhada (CLI)" : $"Cota compartilhada • {desc}");
                }
                else if (groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    bool isSession = bucketId.Equals("gemini-5h", StringComparison.OrdinalIgnoreCase) || window.Equals("5h", StringComparison.OrdinalIgnoreCase);
                    bool isWeekly = bucketId.Equals("gemini-weekly", StringComparison.OrdinalIgnoreCase) || window.Equals("weekly", StringComparison.OrdinalIgnoreCase);
                    if (isSession)
                    {
                        AddQuota(new FamilyMetadata("agy.gemini_flash", "Gemini 3.8 Flash (5h)", "GEMINI", "#06B6D4", 31),
                            TimeSpan.FromHours(5), string.IsNullOrWhiteSpace(desc) ? "Cota de 5 horas Gemini 3.8 Flash (CLI)" : $"Cota Gemini 3.8 Flash • {desc}");
                    }
                    else if (isWeekly)
                    {
                        AddQuota(new FamilyMetadata("agy.gemini_weekly", "Gemini 3.8 Flash (7d)", "GEMINI", "#06B6D4", 32),
                            TimeSpan.FromDays(7), string.IsNullOrWhiteSpace(desc) ? "Cota semanal Gemini 3.8 Flash (CLI)" : $"Cota semanal Gemini 3.8 Flash • {desc}");
                    }
                }
                else if (groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) &&
                    (bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase) || window.Equals("weekly", StringComparison.OrdinalIgnoreCase)))
                {
                    string subtitle = string.IsNullOrWhiteSpace(desc) ? "Cota compartilhada 3P (CLI)" : $"Cota 3P • {desc}";
                    TimeSpan weeklyDuration = TimeSpan.FromDays(7);
                    AddQuota(new FamilyMetadata("agy.claude_opus", "Claude Opus 5.5 (AGY)", "AGY", "#A855F7", 33), weeklyDuration, subtitle);
                    AddQuota(new FamilyMetadata("agy.claude_sonnet", "Claude Sonnet 5.5 (AGY)", "AGY", "#8B5CF6", 34), weeklyDuration, subtitle);
                    AddQuota(new FamilyMetadata("agy.gpt_oss", "GPT-OSS 120B (AGY)", "AGY", "#64748B", 35), weeklyDuration, subtitle);
                }
            }
        }

        return items;
    }

    public sealed record CachedQuotaRecord(
        DateTimeOffset CachedAt,
        List<QuotaItem> Items
    )
    {
        public DateTimeOffset? CarbonCachedAt { get; init; }
    }

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

    internal static void SaveLastKnownQuotas(IReadOnlyList<QuotaItem> items, bool preserveHiddenCarbon = false)
    {
        if (items == null || items.Count == 0) return;
        var validItems = items.Where(x => x.IsAvailable).ToList();
        if (validItems.Count == 0) return;

        DateTimeOffset? carbonCachedAt = null;
        if (preserveHiddenCarbon && !validItems.Any(item => item.Id == CarbonQuotaId) &&
            ReadLastKnownQuotaRecord() is { } previous &&
            previous.Items?.Find(item => item is { Id: CarbonQuotaId, IsAvailable: true }) is { } carbon)
        {
            validItems.Add(carbon);
            carbonCachedAt = previous.CarbonCachedAt ?? previous.CachedAt;
        }

        var record = new CachedQuotaRecord(DateTimeOffset.Now, validItems) { CarbonCachedAt = carbonCachedAt };
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

    private static CachedQuotaRecord? ReadLastKnownQuotaRecord()
    {
        if (_inMemoryCache == null)
        {
            try
            {
                string cachePath = GetCacheFilePath();
                if (File.Exists(cachePath))
                {
                    string json = File.ReadAllText(cachePath);
                    _inMemoryCache = JsonSerializer.Deserialize<CachedQuotaRecord>(json);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load Antigravity quota cache: {ex.Message}");
            }
        }

        return _inMemoryCache;
    }

    internal static IReadOnlyList<QuotaItem>? GetLastKnownQuotas(AppSettings settings)
    {
        CachedQuotaRecord? record = ReadLastKnownQuotaRecord();
        if (record == null || record.Items == null || record.Items.Count == 0)
        {
            return null;
        }

        var result = new List<QuotaItem>();

        foreach (QuotaItem item in record.Items)
        {
            if (item == null || !item.IsAvailable || !IsFamilyEnabled(item.Id, settings))
            {
                continue;
            }

            DateTimeOffset cachedAt = item.Id == CarbonQuotaId ? record.CarbonCachedAt ?? record.CachedAt : record.CachedAt;
            string timeStr = cachedAt.ToString("HH:mm");
            string cachedSubtitle = $"Última cota conhecida ({timeStr})";
            string cleanSubtitle = item.Subtitle ?? string.Empty;
            int separatorIdx = cleanSubtitle.IndexOf("•", StringComparison.Ordinal);
            string baseDesc = separatorIdx >= 0 ? cleanSubtitle.Substring(0, separatorIdx).Trim() : cleanSubtitle;

            result.Add(new QuotaItem
            {
                Id = item.Id,
                Provider = item.Provider,
                ProviderBadge = item.ProviderBadge,
                BadgeColorHex = item.BadgeColorHex,
                Title = item.Title,
                UsedPercent = item.UsedPercent,
                ResetTime = item.ResetTime,
                WindowDuration = item.WindowDuration,
                AllowWindowDurationInference = item.AllowWindowDurationInference,
                IsAvailable = true,
                Subtitle = string.IsNullOrWhiteSpace(baseDesc) ? cachedSubtitle : $"{baseDesc} • {cachedSubtitle}",
                SourceDetail = $"Última cota conhecida salva às {timeStr} • Antigravity fechado",
                SortOrder = item.SortOrder
            });
        }

        return result.Count > 0 ? result.OrderBy(x => x.SortOrder).ToList() : null;
    }

    internal static void ClearCache()
    {
        _inMemoryCache = null;
        _lastCliCache = null;
    }

    private static IReadOnlyList<QuotaItem> CreateOfflineFallback(AppSettings settings)
    {
        var items = new List<QuotaItem>();
        if (settings.ShowAntigravityArgon)
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
                SourceDetail = "Inicie o Antigravity para ler as cotas locais",
                SortOrder = 30
            });
        }
        if (settings.ShowAntigravityFlash)
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
                SourceDetail = "Inicie o Antigravity para ler as cotas locais",
                SortOrder = 31
            });
        }
        if (settings.ShowAntigravityGeminiWeekly)
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
                SourceDetail = "Inicie o Antigravity para ler as cotas locais",
                SortOrder = 32
            });
        }
        return items.Count > 0 || settings.ShowAntigravityCarbon ? items : new[]
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
                SourceDetail = "Inicie o Antigravity para ler as cotas locais",
                SortOrder = 30
            }
        };
    }

    private sealed record RawAntigravityModel(string Label, double UsedPercent, DateTimeOffset? ResetTime);
    private sealed record FamilyMetadata(string Id, string DisplayName, string Badge, string ColorHex, int SortOrder);
}
