using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIUsageTracker.Models;
using static AIUsageTracker.Services.Providers.AntigravityQuotaParser;

namespace AIUsageTracker.Services.Providers;

public sealed partial class AntigravityQuotaProvider
{
    public sealed record CachedQuotaRecord(DateTimeOffset CachedAt, List<QuotaItem> Items)
    {
        public DateTimeOffset? CarbonCachedAt { get; init; }
        public int Version { get; init; }
        public string? Account { get; init; }
        public string? CliPath { get; init; }
    }

    private const int CacheVersion = 1;
    private static readonly object CacheLock = new();
    private static CachedQuotaRecord? _inMemoryCache;
    public static string CustomCacheFilePath { get; set; } = string.Empty;

    internal static string GetCacheFilePath() => string.IsNullOrWhiteSpace(CustomCacheFilePath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIUsageTracker", "antigravity_last_known_quota.json")
        : CustomCacheFilePath;

    internal static void SaveLastKnownQuotas(IReadOnlyList<QuotaItem> items, string? account = null, string? cliPath = null)
    {
        var valid = items.Where(item => item.IsAvailable && !item.IsCached).ToList();
        if (valid.Count == 0)
            return;

        var record = new CachedQuotaRecord(DateTimeOffset.Now, valid)
        {
            Version = CacheVersion, Account = account, CliPath = cliPath
        };
        lock (CacheLock)
        {
            _inMemoryCache = record;
            string? temporaryPath = null;
            try
            {
                string path = Path.GetFullPath(GetCacheFilePath());
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record));
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch (IOException ex) { Debug.WriteLine($"Failed to persist Antigravity quota cache: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Debug.WriteLine($"Failed to persist Antigravity quota cache: {ex.Message}"); }
            finally
            {
                if (temporaryPath != null && File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    internal static IReadOnlyList<QuotaItem> GetLastKnownQuotas(string? account = null, string? cliPath = null,
        bool requireKnownAccount = false)
    {
        lock (CacheLock)
        {
            var record = _inMemoryCache ??= ReadCache();
            if (record?.Items == null || (requireKnownAccount && record.Account == null) ||
                (account != null && record.Account != null && account != record.Account) ||
                (cliPath != null && record.CliPath != null && !cliPath.Equals(record.CliPath, StringComparison.OrdinalIgnoreCase)))
                return [];

            return record.Items.Where(item => item is { IsAvailable: true })
                .Select(item => record.Version == 0 ? NormalizeLegacy(item) : item)
                .OfType<QuotaItem>()
                .DistinctBy(item => item.Id)
                .Select(item =>
                {
                    DateTimeOffset timestamp = IsFamilyItem(item.Id, Carbon)
                        ? record.CarbonCachedAt ?? record.CachedAt : record.CachedAt;
                    return item with
                    {
                        IsCached = true,
                        Title = item.Id switch
                        {
                            SharedSessionId => GroupTitle(Shared, TimeSpan.FromHours(5)),
                            SharedWeeklyId => GroupTitle(Shared, TimeSpan.FromDays(7)),
                            _ => item.Title
                        },
                        Subtitle = item.Subtitle == "Saldo informado pelo modelo"
                            ? $"Saldo informado pelo modelo • Última cota conhecida ({timestamp:HH:mm})"
                            : $"Última cota conhecida ({timestamp:HH:mm})",
                        SourceDetail = string.IsNullOrEmpty(item.SourceDetail)
                            ? "Cache do Antigravity" : $"Cache do Antigravity • {item.SourceDetail}"
                    };
                }).ToArray();
        }
    }

    private static CachedQuotaRecord? ReadCache()
    {
        try
        {
            string path = GetCacheFilePath();
            return File.Exists(path) ? JsonSerializer.Deserialize<CachedQuotaRecord>(File.ReadAllText(path)) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static QuotaItem? NormalizeLegacy(QuotaItem item)
    {
        if (item.Id.StartsWith("agy.gemini_4_carbon", StringComparison.Ordinal))
            return null;

        bool fromSummary = item.SourceDetail?.Contains("RetrieveUserQuotaSummary", StringComparison.OrdinalIgnoreCase) == true;
        bool fromCli = item.SourceDetail?.StartsWith("Antigravity CLI", StringComparison.OrdinalIgnoreCase) == true;
        var family = item.Id is SharedSessionId or SharedWeeklyId || IsFamilyItem(item.Id, Flash) ? Flash
            : IsFamilyItem(item.Id, Argon) ? Argon : IsFamilyItem(item.Id, Carbon) ? Carbon
            : item.Id.StartsWith("agy.gemini_4_hydrogen", StringComparison.Ordinal) ? Hydrogen : null;
        if (family == null)
            return item;

        if (fromSummary || fromCli)
        {
            var group = family == Flash ? Shared : family;
            var duration = item.Id == SharedWeeklyId ? TimeSpan.FromDays(7)
                : family == Flash ? TimeSpan.FromHours(5) : item.WindowDuration;
            if (duration == TimeSpan.FromDays(7) || duration == TimeSpan.FromHours(5))
            {
                return item with
                {
                    Id = GroupId(group, duration.Value),
                    Title = GroupTitle(group, duration.Value),
                    WindowDuration = duration,
                    AllowWindowDurationInference = false
                };
            }
        }

        string suffix = item.Id.StartsWith(family.Id + ".", StringComparison.Ordinal) ? item.Id[family.Id.Length..] : "";
        if (family == Hydrogen)
        {
            int lastDot = item.Id.LastIndexOf('.');
            suffix = lastDot > "agy".Length ? item.Id[lastDot..] : "";
        }
        return item with
        {
            Id = family.Id + ".reported" + suffix,
            Title = family.Name,
            WindowDuration = null,
            AllowWindowDurationInference = false,
            Subtitle = "Saldo informado pelo modelo"
        };
    }

    internal static void ClearCache()
    {
        lock (CacheLock)
            _inMemoryCache = null;
    }
}
