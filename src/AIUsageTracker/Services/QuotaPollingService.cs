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
    private readonly IReadOnlyList<IQuotaProvider> _providers;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _lastProviderConfigSignature;

    public IReadOnlyList<QuotaItem> LatestItems { get; private set; } = Array.Empty<QuotaItem>();
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool IsRefreshing { get; private set; }

    public event EventHandler<IReadOnlyList<QuotaItem>>? QuotasUpdated;
    public event EventHandler<bool>? RefreshStateChanged;

    public QuotaPollingService(SettingsService settingsService)
    {
        _settingsService = settingsService;
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
        _timer.Tick += async (_, _) => await RefreshNowAsync();

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Start()
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(10, _settingsService.Current.RefreshIntervalSeconds));
        _timer.Start();
        _ = RefreshNowAsync();
    }

    private void OnSettingsChanged(object? sender, AppSettings newSettings)
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(10, newSettings.RefreshIntervalSeconds));

        string newSig = ComputeProviderSignature(newSettings);
        if (newSig != _lastProviderConfigSignature || LatestItems.Count == 0)
        {
            _lastProviderConfigSignature = newSig;
            _ = RefreshNowAsync();
        }
    }

    private static string ComputeProviderSignature(AppSettings s)
    {
        return string.Join("|",
            s.EnableCodex, s.ShowCodexPrimary, s.ShowCodexSecondary, s.CustomCodexAuthPath,
            s.EnableClaudeCode, s.ShowClaudeSession5h, s.ShowClaudeWeekly7d, s.ShowClaudeOpus,
            s.ShowClaudeSonnet, s.ShowClaudeDedicatedEvenIfEmpty, s.ShowClaudeSessionCostSubtitle, s.CustomClaudeCredentialsPath,
            s.EnableAntigravity, s.GroupAntigravityEffortLevels, s.ShowAntigravityArgon,
            s.ShowAntigravityFlash, s.ShowAntigravityClaudeOpus, s.ShowAntigravityClaudeSonnet, s.ShowAntigravityGptOss);
    }

    public async Task<IReadOnlyList<QuotaItem>> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return LatestItems;
        }

        try
        {
            IsRefreshing = true;
            RefreshStateChanged?.Invoke(this, true);

            AppSettings snapshot = _settingsService.Current;
            Task<IReadOnlyList<QuotaItem>>[] tasks = _providers
                .Select(p => p.FetchQuotasAsync(snapshot, cancellationToken))
                .ToArray();

            IReadOnlyList<QuotaItem>[] results = await Task.WhenAll(tasks);

            List<QuotaItem> combined = results
                .SelectMany(r => r)
                .OrderBy(item => item.SortOrder)
                .ToList();

            LatestItems = combined;
            LastUpdated = DateTimeOffset.Now;
            QuotasUpdated?.Invoke(this, combined);
            return combined;
        }
        finally
        {
            IsRefreshing = false;
            RefreshStateChanged?.Invoke(this, false);
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _refreshLock.Dispose();
    }
}
