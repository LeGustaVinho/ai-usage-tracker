using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageTracker.Models;

namespace AIUsageTracker.Services.Providers;

internal static partial class AntigravityQuotaParser
{
    internal const string SharedSessionId = "agy.gemini_session";
    internal const string SharedWeeklyId = "agy.gemini_weekly";
    internal const string LocalSummary = "Antigravity RetrieveUserQuotaSummary (local)";
    internal const string LocalStatus = "Antigravity language_server Connect-RPC (GetUserStatus)";
    internal const string CliSource = "Antigravity CLI (agy /usage)";

    internal sealed record Family(string Id, string Name, string Badge, string Color, int Order);
    internal static readonly Family Shared = new("agy.gemini", "Gemini", "GEMINI", "#06B6D4", 31);
    internal static readonly Family Flash = new("agy.gemini_flash", "Gemini 3.8 Flash", "GEMINI", "#06B6D4", 31);
    internal static readonly Family Hydrogen = new("agy.gemini_hydrogen", "Gemini 4 Hydrogen", "GEMINI", "#06B6D4", 32);
    internal static readonly Family Argon = new("agy.gemini_argon", "Gemini 4 Argon", "ARGON", "#3B82F6", 30);
    internal static readonly Family Carbon = new("agy.gemini_carbon", "Gemini 4 Carbon", "CARBON", "#3B82F6", 36);
    private static readonly Family OrchestratorArgon = new("agy.orchestrator_argon", "Orchestrator (Argon)", "AGY", "#3B82F6", 39);
    private static readonly Family OrchestratorCarbon = new("agy.orchestrator_carbon", "Orchestrator (Carbon)", "AGY", "#3B82F6", 39);
    internal static readonly Family Opus = new("agy.claude_opus", "Claude Opus 5.5 (AGY)", "AGY", "#A855F7", 33);
    internal static readonly Family Sonnet = new("agy.claude_sonnet", "Claude Sonnet 5.5 (AGY)", "AGY", "#8B5CF6", 34);
    internal static readonly Family Gpt = new("agy.gpt_oss", "GPT-OSS 120B (AGY)", "AGY", "#64748B", 35);
    private static readonly Family ThirdParty = new("agy.3p", "Claude and GPT models", "AGY", "#64748B", 33);

    internal sealed record Model(Family Family, string Label, bool Disabled, double? Fraction = null,
        DateTimeOffset? Reset = null, TimeSpan? Window = null);
    internal sealed record Catalog(IReadOnlyList<Model> Models, bool FromCli = false,
        string? Account = null, bool IsUltra = false);
    internal sealed record Snapshot(IReadOnlyList<QuotaItem> Groups, Catalog Catalog, string Source)
    {
        internal int GeminiGroupCount => Groups.Count(item => item.IsAvailable && IsGeminiGroup(item.Id));
    }

    private static readonly TimeSpan Weekly = TimeSpan.FromDays(7);
    private static readonly TimeSpan Session = TimeSpan.FromHours(5);
    private static readonly Family[] GeminiFamilies = [Flash, Hydrogen, Argon, Carbon];
    private static readonly Family[] GeminiCatalogFamilies = [.. GeminiFamilies, OrchestratorArgon, OrchestratorCarbon];

    [GeneratedRegex(@"\s*\((Low|Medium|High|XHigh|Extra(?: High)?)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex EffortSuffix();

    internal static Snapshot ReadLocal(string? statusJson, string? summaryJson)
    {
        using var status = ReadJson(statusJson);
        using var summary = ReadJson(summaryJson);
        var user = Property(status?.RootElement ?? default, "userStatus");
        var configs = Property(Property(user, "cascadeModelConfigData"), "clientModelConfigs");
        var models = new List<Model>();
        foreach (var config in Entries(configs))
        {
            string? id = String(config, "modelId");
            string? label = String(config, "label");
            Family? family = IdentifyModel(id, label);
            if (family == null)
                continue;

            var quota = Property(config, "quotaInfo");
            var reset = ReadReset(quota);
            var fraction = Property(quota, "remainingFraction", "remaining_fraction");
            bool disabled = Property(config, "disabled").ValueKind == JsonValueKind.True ||
                id?.EndsWith("-disabled", StringComparison.OrdinalIgnoreCase) == true;
            models.Add(new Model(family, label ?? id ?? family.Name, disabled,
                fraction.ValueKind == JsonValueKind.Undefined && reset.HasValue ? 0 : ReadFraction(fraction),
                reset, ReadWindow(String(quota, "window"))));
        }

        string? email = String(user, "email")?.Trim().ToLowerInvariant();
        string? account = string.IsNullOrEmpty(email) ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)));
        var tier = Property(user, "userTier");
        bool ultra = String(tier, "name")?.StartsWith("Google AI Ultra", StringComparison.OrdinalIgnoreCase) == true;
        var catalog = new Catalog(models, Account: account, IsUltra: ultra);
        return new Snapshot(ReadGroups(summary?.RootElement ?? default, catalog, LocalSummary), catalog, LocalSummary);
    }

    internal static Snapshot ReadCli(string? json, Catalog? catalog = null)
    {
        using var doc = ReadJson(json);
        var root = doc?.RootElement ?? default;
        var command = Property(root, "command");
        string? status = String(root, "status");
        string? name = String(command, "name");
        var data = (status == null || Is(status, "SUCCESS")) &&
            (name == null || Is(name, "usage") || Is(name, "quota")) ? Property(command, "data") : default;
        catalog ??= new Catalog([]);
        return new Snapshot(ReadGroups(data, catalog, CliSource), catalog, CliSource);
    }

    internal static Catalog ReadCliCatalog(string output)
    {
        var models = new List<Model>();
        bool hasEntries = false;
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
                continue;
            string id = line[..separator].Trim();
            string label = line[(separator + 1)..].Trim();
            if (id.Any(char.IsWhiteSpace) || label.Length == 0)
                continue;
            hasEntries = true;
            if (IdentifyModel(id, label) is { } family && !id.EndsWith("-disabled", StringComparison.OrdinalIgnoreCase))
                models.Add(new Model(family, label, false));
        }
        // The Antigravity model catalog offers Orchestrator (Carbon) only on Ultra.
        return new Catalog(models, FromCli: hasEntries, IsUltra: models.Any(model => model.Family == OrchestratorCarbon));
    }

    private static IReadOnlyList<QuotaItem> ReadGroups(JsonElement data, Catalog catalog, string source)
    {
        var wrapped = Property(data, "response");
        var groups = Property(wrapped.ValueKind == JsonValueKind.Object ? wrapped : data, "groups");
        var parsed = new List<(Family Family, QuotaItem Item, HashSet<string> ExplicitModels)>();
        foreach (var group in Entries(groups))
        {
            string? groupName = String(group, "displayName", "name");
            var explicitModels = ReadGroupModels(group);
            foreach (var bucket in Entries(Property(group, "buckets")))
            {
                string? id = String(bucket, "bucketId", "id");
                Family? family = string.IsNullOrWhiteSpace(id) ? IdentifyGroup(groupName) : IdentifyBucket(id);
                if (family == null)
                    continue;
                var window = ReadWindowFromId(id) ?? ReadWindow(String(bucket, "window"));
                if (window == null || (family == ThirdParty && window != Weekly))
                    continue;

                var remaining = Property(bucket, "remaining");
                var fraction = Property(remaining, "remainingFraction", "remaining_fraction");
                if (fraction.ValueKind == JsonValueKind.Undefined)
                    fraction = Property(bucket, "remainingFraction", "remaining_fraction");
                var reset = ReadReset(bucket) ?? ReadReset(remaining);
                double? value = Property(bucket, "disabled").ValueKind == JsonValueKind.True ? null : ReadFraction(fraction);
                if (family == ThirdParty)
                {
                    foreach (var model in new[] { Opus, Sonnet, Gpt })
                        parsed.Add((model, Create(model, model.Id, model.Name, value, reset, window, source,
                            subtitle: String(bucket, "description")), explicitModels));
                    continue;
                }

                string quotaId = GroupId(family, window.Value);
                string title = GroupTitle(family, window.Value);
                var modelEntries = catalog.Models.Where(model => model.Family == family).ToArray();
                bool unavailable = modelEntries.Length > 0 && modelEntries.All(model => model.Disabled);
                var item = Create(family, quotaId, title, value, reset, window, source,
                    unavailable ? "Indisponível nesta conta" : null, String(bucket, "description"));
                parsed.Add((family, item, explicitModels));
            }
        }

        var dedicated = parsed.Where(row => row.Family == Argon || row.Family == Carbon)
            .Select(row => row.Family).ToHashSet();
        var shared = parsed.Where(row => row.Family == Shared && row.Item.IsAvailable).Select(row => row.Item).ToArray();
        return parsed.GroupBy(row => row.Item.Id, StringComparer.Ordinal)
            .Select(rows => rows.OrderByDescending(row => row.Item.IsAvailable).First())
            .Select(row => row.Item with
            {
                AssociatedModels = row.Family == Shared
                    ? GeminiCatalogFamilies.Where(family => !dedicated.Contains(family) &&
                        (row.ExplicitModels.Contains(family.Name) && CanUseExplicitMembership(catalog, family) ||
                         IsKnownSharedModel(catalog, family, shared)))
                        .Select(family => family.Name).ToArray()
                    : row.Family == Argon || row.Family == Carbon
                        ? [.. catalog.Models.Where(model => !model.Disabled &&
                                model.Family == (row.Family == Argon ? OrchestratorArgon : OrchestratorCarbon) &&
                                MatchesReading(model, row.Item))
                            .Select(model => model.Family.Name).Distinct()]
                        : null
            })
            .ToArray();
    }

    private static bool CanUseExplicitMembership(Catalog catalog, Family family) =>
        catalog.Models.Any(model => model.Family == family && !model.Disabled) ||
        (!catalog.FromCli && !catalog.Models.Any(model => model.Family == family));

    private static bool IsKnownSharedModel(Catalog catalog, Family family, IReadOnlyList<QuotaItem> shared)
    {
        var active = catalog.Models.Where(model => model.Family == family && !model.Disabled).ToArray();
        if (active.Length == 0)
            return false;
        if (family == Flash || family == Hydrogen || (family == Argon && catalog.IsUltra))
            return true;
        // On Ultra, Carbon can appear in the shared summary without an explicit member list.
        return ((family == Carbon && catalog.IsUltra) || family == OrchestratorCarbon || family == OrchestratorArgon) &&
            active.All(model => shared.Any(item => MatchesReading(model, item)));
    }

    private static bool MatchesReading(Model model, QuotaItem item) =>
        item.IsAvailable && model.Reset.HasValue && model.Reset == item.ResetTime &&
        model.Fraction is > 0 and < 1 && Math.Abs((1 - model.Fraction.Value) * 100 - item.UsedPercent) < 0.000001;

    private static HashSet<string> ReadGroupModels(JsonElement group)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries(Property(group, "models", "modelIds", "model_ids")))
        {
            string? id = entry.ValueKind == JsonValueKind.String ? entry.GetString() : String(entry, "id", "modelId");
            string? label = String(entry, "displayName", "name", "label");
            if (IdentifyModel(id, label) is { } family)
                names.Add(family.Name);
        }
        const string prefix = "Models within this group:";
        if (String(group, "description") is { } description && description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string label in description[prefix.Length..].Split(',', StringSplitOptions.TrimEntries))
                if (IdentifyModel(null, label) is { } family)
                    names.Add(family.Name);
        }
        return names;
    }

    internal static IEnumerable<QuotaItem> ReadModelBalances(Catalog catalog, AppSettings settings, bool gemini)
    {
        foreach (var family in catalog.Models.Where(model => !model.Disabled && model.Fraction.HasValue)
                     .GroupBy(model => model.Family))
        {
            if (family.Key == OrchestratorArgon || family.Key == OrchestratorCarbon ||
                GeminiFamilies.Contains(family.Key) != gemini)
                continue;
            bool groupEfforts = settings.GroupAntigravityEffortLevels;
            var readings = groupEfforts && gemini
                ? family.DistinctBy(model => (model.Fraction, model.Reset, model.Window)).ToArray()
                : groupEfforts ? [family.MinBy(model => model.Fraction)!] : family.ToArray();

            foreach (var model in readings)
            {
                string label = groupEfforts && readings.Length == 1 ? model.Family.Name : model.Label;
                string id = model.Family.Id + (gemini ? ".reported" : "");
                if (!groupEfforts || readings.Length > 1)
                    id += "." + model.Label.ToLowerInvariant().Replace(' ', '-');
                yield return Create(model.Family, id, label, model.Fraction, model.Reset,
                    model.Window ?? (gemini ? null : Weekly), LocalStatus,
                    subtitle: gemini ? "Saldo informado pelo modelo" : null);
            }
        }
    }

    internal static IEnumerable<QuotaItem> ModelPlaceholders(IReadOnlyList<QuotaItem> items, Catalog catalog, string? source)
    {
        foreach (var family in GeminiFamilies)
        {
            if (items.Any(item => IsFamilyItem(item.Id, family) ||
                item.AssociatedModels?.Contains(family.Name, StringComparer.Ordinal) == true))
                continue;
            var entries = catalog.Models.Where(model => model.Family == family).ToArray();
            if (entries.Length == 0 && !catalog.FromCli && family != Argon && family != Carbon)
                continue;
            bool unavailable = entries.Length > 0 ? entries.All(model => model.Disabled) : catalog.FromCli;
            yield return Create(family, family.Id + ".unavailable", family.Name, null, null, null, source,
                unavailable ? "Indisponível nesta conta" : "Cota não reportada");
        }
    }

    internal static QuotaItem Create(Family family, string id, string title, double? fraction, DateTimeOffset? reset,
        TimeSpan? window, string? source, string? unavailable = null, string? subtitle = null) => new()
    {
        Id = id,
        Provider = QuotaProviderType.Antigravity,
        ProviderBadge = family.Badge,
        BadgeColorHex = family.Color,
        Title = title,
        UsedPercent = fraction.HasValue ? (1 - fraction.Value) * 100 : 0,
        IsAvailable = fraction.HasValue,
        ResetTime = reset,
        WindowDuration = window,
        AllowWindowDurationInference = false,
        SourceDetail = source,
        Subtitle = subtitle,
        UnavailableMessage = fraction.HasValue ? null : unavailable ?? "Cota não reportada",
        SortOrder = family.Order + (family == Shared && window == Weekly ? 1 : 0)
    };

    internal static bool IsGeminiGroup(string id) =>
        id is SharedSessionId or SharedWeeklyId ||
        id == Argon.Id || id == Argon.Id + ".session" || id == Carbon.Id || id == Carbon.Id + ".session";

    internal static bool IsFamilyItem(string id, Family family) =>
        id == family.Id || id.StartsWith(family.Id + ".", StringComparison.Ordinal);

    internal static string GroupId(Family family, TimeSpan window) => family == Shared
        ? window == Weekly ? SharedWeeklyId : SharedSessionId
        : family.Id + (window == Session ? ".session" : "");

    internal static string GroupTitle(Family family, TimeSpan window) =>
        family.Name + (window == Weekly ? " (7d)" : " (5h)");

    private static Family? IdentifyGroup(string? name) =>
        Is(name, "Gemini Models") ? Shared : Is(name, Argon.Name) ? Argon :
        Is(name, Carbon.Name) ? Carbon : Is(name, ThirdParty.Name) ? ThirdParty : null;

    private static Family? IdentifyBucket(string id)
    {
        string key = id.ToLowerInvariant();
        return key switch
        {
            "gemini-weekly" or "gemini-5h" => Shared,
            "gemini-argon-wtu-weekly" or "gemini-argon-weekly" or "gemini-argon-5h" or
                "gemini-4-argon-wtu-weekly" or "gemini-4-argon-weekly" or "gemini-4-argon-5h" => Argon,
            "gemini-carbon-wtu-weekly" or "gemini-carbon-weekly" or "gemini-carbon-5h" or
                "gemini-4-carbon-wtu-weekly" or "gemini-4-carbon-weekly" or "gemini-4-carbon-5h" => Carbon,
            "3p-weekly" or "3p-5h" => ThirdParty,
            _ => null
        };
    }

    private static Family? IdentifyModel(string? id, string? label)
    {
        string name = EffortSuffix().Replace(label?.Trim() ?? "", "");
        if (MatchesModelId(id, "gemini-3.8-flash") || Is(name, Flash.Name)) return Flash;
        if (MatchesModelId(id, "gemini-4-hydrogen") || Is(name, Hydrogen.Name)) return Hydrogen;
        if (MatchesModelId(id, "gemini-4-argon") || Is(name, Argon.Name)) return Argon;
        if (MatchesModelId(id, "gemini-4-carbon") || Is(name, Carbon.Name)) return Carbon;
        if (Is(name, OrchestratorArgon.Name) || Is(id, "gemini-pro-orchestrator-g1-pro") ||
            Is(id, "gemini-pro-orchestrator-argon")) return OrchestratorArgon;
        if (Is(name, OrchestratorCarbon.Name) || Is(id, "gemini-pro-orchestrator")) return OrchestratorCarbon;
        if (name.StartsWith("Claude Opus ", StringComparison.OrdinalIgnoreCase)) return Opus with { Name = name + " (AGY)" };
        if (name.StartsWith("Claude Sonnet ", StringComparison.OrdinalIgnoreCase)) return Sonnet with { Name = name + " (AGY)" };
        if (name.StartsWith("GPT-OSS ", StringComparison.OrdinalIgnoreCase)) return Gpt with { Name = name + " (AGY)" };
        return null;
    }

    private static bool MatchesModelId(string? value, string id) =>
        Is(value, id) || value?.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) == true;

    private static bool Is(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ReadWindow(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "weekly" or "7d" => Weekly,
        "5h" => Session,
        _ => null
    };

    private static TimeSpan? ReadWindowFromId(string? id) =>
        id?.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase) == true ? Weekly :
        id?.EndsWith("-5h", StringComparison.OrdinalIgnoreCase) == true ? Session : null;

    private static double? ReadFraction(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double fraction) &&
        fraction >= 0 && fraction <= 1 ? fraction : null;

    private static DateTimeOffset? ReadReset(JsonElement data)
    {
        var value = Property(data, "resetTime", "reset_time");
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset))
            return reset;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long unix) &&
            unix is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
    }

    private static JsonElement.ArrayEnumerator Entries(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : default;

    private static string? String(JsonElement parent, string name, string? alternate = null, string? third = null)
    {
        var property = Property(parent, name, alternate, third);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static JsonElement Property(JsonElement parent, string name, string? alternate = null, string? third = null)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            (parent.TryGetProperty(name, out var property) ||
             (alternate != null && parent.TryGetProperty(alternate, out property)) ||
             (third != null && parent.TryGetProperty(third, out property))))
            return property;
        return default;
    }

    private static JsonDocument? ReadJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }
}
