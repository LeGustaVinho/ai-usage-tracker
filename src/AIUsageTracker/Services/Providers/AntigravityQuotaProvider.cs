using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageTracker.Helpers;
using AIUsageTracker.Models;
using static AIUsageTracker.Services.Providers.AntigravityQuotaParser;

namespace AIUsageTracker.Services.Providers;

public sealed partial class AntigravityQuotaProvider : IQuotaProvider
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    }) { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly Regex CsrfRegex = new(@"--csrf_token(?:=|\s+)([^\s""]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string RequestBodyJson =
        """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}""";
    private const string SummaryRequestBodyJson =
        """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en","ideVersion":"unknown"}}""";

    private readonly Func<IEnumerable<LanguageServerEndpoint>> _discoverEndpoints;
    private readonly Func<LanguageServerEndpoint, CancellationToken, Task<QuotaResponses?>> _fetchEndpointAsync;
    private readonly Func<string, string[], CancellationToken, Task<string?>> _runCliAsync;
    private LanguageServerEndpoint? _cachedEndpoint;
    private string? _lastLocalAccount;
    private bool _localAccountChanged;
    private readonly Dictionary<string, CliCache> _cliCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cliLock = new(1, 1);

    public QuotaProviderType ProviderType => QuotaProviderType.Antigravity;

    internal sealed record LanguageServerEndpoint(int ProcessId, string BaseUrl, string CsrfToken);
    internal sealed record QuotaResponses(string? UserStatusJson, string? SummaryJson);
    private sealed record CliCache(DateTimeOffset QuotaAt, Snapshot? Snapshot,
        DateTimeOffset CatalogAt, Catalog? Catalog, string? LocalAccount);

    public AntigravityQuotaProvider() : this(DiscoverLanguageServerEndpoints, TryFetchEndpointAsync) { }

    internal AntigravityQuotaProvider(
        Func<IEnumerable<LanguageServerEndpoint>> discoverEndpoints,
        Func<LanguageServerEndpoint, CancellationToken, Task<QuotaResponses?>> fetchEndpointAsync,
        Func<string, string[], CancellationToken, Task<string?>>? runCliAsync = null)
    {
        _discoverEndpoints = discoverEndpoints;
        _fetchEndpointAsync = fetchEndpointAsync;
        _runCliAsync = runCliAsync ?? RunCliAsync;
    }

    public async Task<IReadOnlyList<QuotaItem>> FetchQuotasAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.EnableAntigravity)
            return [];

        Snapshot? local = null;
        try
        {
            local = await QueryLanguageServerAsync(settings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Antigravity LanguageServer query failed: {ex.Message}");
        }
        if (local?.Catalog.Account is { } account)
        {
            _localAccountChanged |= _lastLocalAccount != null && _lastLocalAccount != account;
            _lastLocalAccount = account;
        }

        if (local is { GeminiGroupCount: > 0 })
        {
            var items = GetLocalItems(local, settings);
            SaveLastKnownQuotas(items, local.Catalog.Account);
            return Present(items, settings, local.Catalog, local.Source);
        }

        string? cliPath = settings.EnableAntigravityCliFallback ? settings.GetEffectiveAgyCliPath() : null;
        var cli = cliPath == null ? null : await FetchCliAsync(cliPath, _lastLocalAccount, cancellationToken);
        if (cli is { GeminiGroupCount: > 0 })
        {
            SaveLastKnownQuotas(cli.Groups, cliPath: cliPath);
            return Present(cli.Groups, settings, cli.Catalog, cli.Source);
        }

        var cached = ApplyLocalAvailability(GetLastKnownQuotas(_lastLocalAccount, cliPath, _localAccountChanged), local?.Catalog);
        if (cached.Any(item => IsGeminiGroup(item.Id) && item.IsAvailable))
            return Present(cached, settings, local?.Catalog ?? new Catalog([]), "Cache do Antigravity");

        if (local != null)
        {
            var items = GetLocalItems(local, settings);
            if (items.Any(item => item.IsAvailable))
            {
                SaveLastKnownQuotas(items, local.Catalog.Account);
                return Present(items, settings, local.Catalog, LocalStatus);
            }
        }

        if (cached.Count > 0)
            return Present(cached, settings, local?.Catalog ?? new Catalog([]), "Cache do Antigravity");

        if (cli?.Groups.Count > 0)
            return Present(cli.Groups, settings, cli.Catalog, cli.Source);

        if (local != null || cli != null)
        {
            var available = local ?? cli!;
            return Present(available.Groups, settings, available.Catalog, available.Source);
        }

        var offline = new[] { TimeSpan.FromHours(5), TimeSpan.FromDays(7) }
            .Select(window => Create(Shared, GroupId(Shared, window), GroupTitle(Shared, window),
                null, null, window, null, "Antigravity offline")).ToArray();
        return Present(offline, settings, new Catalog([]), null);
    }

    private static IReadOnlyList<QuotaItem> ApplyLocalAvailability(IReadOnlyList<QuotaItem> items, Catalog? catalog)
    {
        if (catalog == null)
            return items;
        var disabled = catalog.Models.GroupBy(model => model.Family)
            .Where(family => family.All(model => model.Disabled)).Select(family => family.Key).ToArray();
        return items.Where(item => !disabled.Any(family => IsFamilyItem(item.Id, family)))
            .Select(item => item.AssociatedModels == null ? item : item with
            {
                AssociatedModels = item.AssociatedModels.Where(name => !disabled.Any(family => family.Name == name)).ToArray()
            }).ToArray();
    }

    private async Task<Snapshot?> QueryLanguageServerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        LanguageServerEndpoint? cached = _cachedEndpoint;
        Snapshot? best = null;
        int bestScore = -1;

        if (cached != null && await TryEndpointAsync(cached))
            return best;

        foreach (var endpoint in _discoverEndpoints())
        {
            if (endpoint != cached && await TryEndpointAsync(endpoint))
                break;
        }

        return best;

        async Task<bool> TryEndpointAsync(LanguageServerEndpoint endpoint)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuotaResponses? response = await _fetchEndpointAsync(endpoint, cancellationToken);
            if (response == null)
                return false;

            var snapshot = ReadLocal(response.UserStatusJson, response.SummaryJson);
            var shared = snapshot.Groups.Where(item => item.IsAvailable).Select(item => item.Id).ToHashSet();
            int score = snapshot.GeminiGroupCount * 10 + (snapshot.Catalog.Models.Count > 0 ? 1 : 0);
            if (score > bestScore)
            {
                best = snapshot;
                bestScore = score;
                _cachedEndpoint = endpoint;
            }

            bool requestedSharedPresent = (!settings.ShowAntigravityFlash || shared.Contains(SharedSessionId)) &&
                (!settings.ShowAntigravityGeminiWeekly || shared.Contains(SharedWeeklyId));
            bool AccountModelResolved(Family family, bool enabled) => !enabled ||
                (snapshot.Catalog.Models.Any(model => model.Family == family && model.Disabled) &&
                 !snapshot.Catalog.Models.Any(model => model.Family == family && !model.Disabled)) ||
                snapshot.Groups.Any(item => item.IsAvailable &&
                    (IsFamilyItem(item.Id, family) || item.AssociatedModels?.Contains(family.Name) == true));
            return snapshot.GeminiGroupCount > 0 && requestedSharedPresent &&
                AccountModelResolved(Argon, settings.ShowAntigravityArgon) &&
                AccountModelResolved(Carbon, settings.ShowAntigravityCarbon);
        }
    }

    private static async Task<QuotaResponses?> TryFetchEndpointAsync(LanguageServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        var status = TryCallRpcAsync(endpoint, "GetUserStatus", RequestBodyJson, "\"userStatus\"", cancellationToken);
        var summary = TryCallRpcAsync(endpoint, "RetrieveUserQuotaSummary", SummaryRequestBodyJson, "\"groups\"", cancellationToken);
        await Task.WhenAll(status, summary);
        return status.Result != null || summary.Result != null ? new QuotaResponses(status.Result, summary.Result) : null;
    }

    private static async Task<string?> TryCallRpcAsync(LanguageServerEndpoint endpoint, string method, string body,
        string expectedField, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"{endpoint.BaseUrl}/exa.language_server_pb.LanguageServerService/{method}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", endpoint.CsrfToken);
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (content.Contains(expectedField, StringComparison.Ordinal))
                    return content;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException) { }

        return null;
    }

    private async Task<Snapshot?> FetchCliAsync(string path, string? localAccount, CancellationToken cancellationToken)
    {
        await _cliLock.WaitAsync(cancellationToken);
        try
        {
            _cliCache.TryGetValue(path, out var cached);
            if (cached != null && cached.LocalAccount != localAccount)
                cached = null;
            if (cached?.Snapshot != null && DateTimeOffset.UtcNow - cached.QuotaAt < TimeSpan.FromSeconds(20))
                return cached.Snapshot;

            string? output = await _runCliAsync(path, ["-p", "/usage", "--output-format", "json"], cancellationToken);
            var quota = ReadCli(output);
            if (quota.Groups.Count == 0)
                return null;

            var catalog = cached?.Catalog;
            var catalogAt = cached?.CatalogAt ?? DateTimeOffset.MinValue;
            if (quota.GeminiGroupCount > 0 && DateTimeOffset.UtcNow - catalogAt >= TimeSpan.FromMinutes(5))
            {
                string? models = await _runCliAsync(path, ["models"], cancellationToken);
                catalog = ReadCliCatalog(models ?? "");
                catalogAt = DateTimeOffset.UtcNow;
            }

            var snapshot = ReadCli(output, catalog);
            _cliCache[path] = new CliCache(DateTimeOffset.UtcNow, snapshot, catalogAt, catalog, localAccount);
            return snapshot;
        }
        finally
        {
            _cliLock.Release();
        }
    }

    private static async Task<string?> RunCliAsync(string path, string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (!process.Start())
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Debug.WriteLine($"Antigravity CLI failed: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<QuotaItem> GetLocalItems(Snapshot snapshot, AppSettings settings)
    {
        // The local status still supplies provider-specific Claude/GPT rows when needed.
        var balances = ReadModelBalances(snapshot.Catalog, settings, gemini: false);
        if (snapshot.GeminiGroupCount == 0)
            balances = balances.Concat(ReadModelBalances(snapshot.Catalog, settings, gemini: true));
        return snapshot.Groups.Where(group => IsGeminiGroup(group.Id)).Concat(balances).ToArray();
    }

    internal static IReadOnlyList<QuotaItem> ParseQuotaResponses(string? statusJson, string? summaryJson, AppSettings settings)
    {
        var snapshot = ReadLocal(statusJson, summaryJson);
        return Present(GetLocalItems(snapshot, settings), settings, snapshot.Catalog,
            snapshot.GeminiGroupCount > 0 ? LocalSummary : LocalStatus);
    }

    internal static IReadOnlyList<QuotaItem> ParseCliQuotaResponse(string json, AppSettings settings, string? models = null)
    {
        var snapshot = ReadCli(json, models == null ? null : ReadCliCatalog(models));
        return Present(snapshot.Groups, settings, snapshot.Catalog, snapshot.Source);
    }

    private static IReadOnlyList<QuotaItem> Present(IReadOnlyList<QuotaItem> items, AppSettings settings, Catalog catalog, string? source)
    {
        return Filter(items.Concat(ModelPlaceholders(items, catalog, source)), settings);
    }

    private static IReadOnlyList<QuotaItem> Filter(IEnumerable<QuotaItem> items, AppSettings settings)
    {
        return items.Where(item => IsVisible(item.Id, settings))
            .Select(item => item.AssociatedModels == null ? item : item with
            {
                AssociatedModels = item.AssociatedModels.Where(name =>
                    (name != Argon.Name || settings.ShowAntigravityArgon) &&
                    (name != Carbon.Name || settings.ShowAntigravityCarbon)).ToArray()
            })
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool IsVisible(string id, AppSettings settings)
    {
        if (id == SharedWeeklyId) return settings.ShowAntigravityGeminiWeekly;
        if (id == SharedSessionId) return settings.ShowAntigravityFlash;
        if (IsFamilyItem(id, Flash) || IsFamilyItem(id, Hydrogen))
            return settings.ShowAntigravityFlash || settings.ShowAntigravityGeminiWeekly;
        if (IsFamilyItem(id, Argon)) return settings.ShowAntigravityArgon;
        if (IsFamilyItem(id, Carbon)) return settings.ShowAntigravityCarbon;
        if (IsFamilyItem(id, Opus)) return settings.ShowAntigravityClaudeOpus;
        if (IsFamilyItem(id, Sonnet)) return settings.ShowAntigravityClaudeSonnet;
        if (IsFamilyItem(id, Gpt)) return settings.ShowAntigravityGptOss;
        return false;
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

}
