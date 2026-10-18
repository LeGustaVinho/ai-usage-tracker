using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AIUsageTracker.Models;
using AIUsageTracker.Services.Providers;

namespace AIUsageTracker.Services;

public sealed class QuotaPollingService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly ClaudeAuthService? _claudeAuthService;
    private readonly IReadOnlyList<IQuotaProvider> _providers;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile bool _disposed;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly EventHandler? _credentialsChangedHandler;
    private string _lastProviderConfigSignature;

    public IReadOnlyList<QuotaItem> LatestItems { get; private set; } = Array.Empty<QuotaItem>();
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool IsRefreshing { get; private set; }

    public event EventHandler<IReadOnlyList<QuotaItem>>? QuotasUpdated;
    public event EventHandler<bool>? RefreshStateChanged;

    public QuotaPollingService(SettingsService settingsService, ClaudeAuthService? claudeAuthService = null)
    {
        _settingsService = settingsService;
        _claudeAuthService = claudeAuthService;
        _lastProviderConfigSignature = ComputeProviderSignature(_settingsService.Current);
        _providers = new IQuotaProvider[]
        {
            new CodexQuotaProvider(),
            new ClaudeCodeQuotaProvider(),
            new AntigravityQuotaProvider()
        };

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(10, _settingsService.Current.RefreshIntervalSeconds))
        };
        _timer.Tick += async (_, _) =>
        {
            try { await RefreshNowAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Polling failed: {ex}"); }
        };

        _settingsService.SettingsChanged += OnSettingsChanged;

        if (_claudeAuthService != null)
        {
            _credentialsChangedHandler = (_, _) => QueueRefresh();
            _claudeAuthService.CredentialsChanged += _credentialsChangedHandler;
        }
    }

    public void Start()
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(10, _settingsService.Current.RefreshIntervalSeconds));
        _timer.Start();
        QueueRefresh();
    }

    private void OnSettingsChanged(object? sender, AppSettings newSettings)
    {
        if (_disposed) return;
        if (!_timer.Dispatcher.CheckAccess())
        {
            _timer.Dispatcher.BeginInvoke(new Action(() => OnSettingsChanged(sender, newSettings)));
            return;
        }
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(10, newSettings.RefreshIntervalSeconds));

        string newSig = ComputeProviderSignature(newSettings);
        if (newSig != _lastProviderConfigSignature || LatestItems.Count == 0)
        {
            _lastProviderConfigSignature = newSig;
            QueueRefresh();
        }
    }

    private async void QueueRefresh()
    {
        try { await RefreshNowAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Refresh failed: {ex}"); }
    }

    private static string ComputeProviderSignature(AppSettings s)
    {
        return string.Join("|",
            s.EnableCodex, s.ShowCodexPrimary, s.ShowCodexSecondary, s.CustomCodexAuthPath,
            s.EnableClaudeCode, s.ShowClaudeSession5h, s.ShowClaudeWeekly7d, s.ShowClaudeOpus,
            s.ShowClaudeSonnet, s.ShowClaudeDedicatedEvenIfEmpty, s.ShowClaudeSessionCostSubtitle, s.CustomClaudeCredentialsPath,
            s.EnableAntigravity, s.GroupAntigravityEffortLevels, s.ShowAntigravityArgon,
            s.ShowAntigravityFlash, s.ShowAntigravityGeminiWeekly, s.ShowAntigravityClaudeOpus, s.ShowAntigravityClaudeSonnet, s.ShowAntigravityGptOss,
            s.EnableAntigravityCliFallback, s.CustomAgyCliPath);
    }

    public async Task<IReadOnlyList<QuotaItem>> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return LatestItems;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
        CancellationToken token = linkedCancellation.Token;
        // Wait for the running refresh, then fetch again with current credentials/settings.
        await _refreshLock.WaitAsync(token);

        try
        {
            {
                if (_disposed) return LatestItems;
                IsRefreshing = true;
                RefreshStateChanged?.Invoke(this, true);
                AppSettings snapshot = _settingsService.Current;

                if (_claudeAuthService != null && snapshot.EnableClaudeCode)
                    _claudeAuthService.UpdateTokenExpiredStatus();
                Task<IReadOnlyList<QuotaItem>>[] tasks = _providers
                    .Select(p => Task.Run(() => FetchProviderSafeAsync(p, snapshot, token), token))
                    .ToArray();

                IReadOnlyList<QuotaItem>[] results = await Task.WhenAll(tasks);

                List<QuotaItem> combined = results
                    .SelectMany(r => r)
                    .OrderBy(item => item.SortOrder)
                    .ToList();

                if (_disposed) return LatestItems;
                LatestItems = combined;
                LastUpdated = DateTimeOffset.Now;
                if (_timer.Dispatcher.CheckAccess())
                    QuotasUpdated?.Invoke(this, combined);
                else if (!_timer.Dispatcher.HasShutdownStarted)
                    await _timer.Dispatcher.InvokeAsync(() => { if (!_disposed) QuotasUpdated?.Invoke(this, combined); });
                if (_claudeAuthService != null && snapshot.EnableClaudeCode &&
                    snapshot.AutoPromptClaudeLoginOnExpiration && _claudeAuthService.UpdateTokenExpiredStatus())
                    _claudeAuthService.TryAutoLogin(force: false);
            }

            return LatestItems;
        }
        finally
        {
            IsRefreshing = false;
            if (!_disposed) RefreshStateChanged?.Invoke(this, false);
            _refreshLock.Release();
        }
    }

    private static async Task<IReadOnlyList<QuotaItem>> FetchProviderSafeAsync(
        IQuotaProvider provider, AppSettings settings, CancellationToken cancellationToken)
    {
        try { return await provider.FetchQuotasAsync(settings, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{provider.ProviderType} fetch failed: {ex}");
            return Array.Empty<QuotaItem>();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _disposeCancellation.Cancel();
        _timer.Stop();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        if (_claudeAuthService != null && _credentialsChangedHandler != null)
            _claudeAuthService.CredentialsChanged -= _credentialsChangedHandler;
        // An async refresh may still be releasing the semaphore.
    }
}
