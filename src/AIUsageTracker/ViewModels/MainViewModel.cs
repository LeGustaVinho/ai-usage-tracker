using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using AIUsageTracker.Models;
using AIUsageTracker.Services;

namespace AIUsageTracker.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly QuotaPollingService _pollingService;
    private readonly Action _openSettingsAction;
    private readonly ClaudeAuthService? _claudeAuthService;

    private bool _isRefreshing;
    private string _lastUpdatedText = "Carregando...";
    private QuotaStatusLevel _overallStatus = QuotaStatusLevel.Normal;
    private string _traySummaryToolTip = "AI Usage Tracker";

    public ObservableCollection<QuotaBarViewModel> QuotaBars { get; } = new();

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public QuotaStatusLevel OverallStatus
    {
        get => _overallStatus;
        private set => SetProperty(ref _overallStatus, value);
    }

    public string TraySummaryToolTip
    {
        get => _traySummaryToolTip;
        private set => SetProperty(ref _traySummaryToolTip, value);
    }

    public bool CompactMode => _settingsService.Current.CompactMode;
    public bool AlwaysOnTop => _settingsService.Current.AlwaysOnTop;
    public bool ClickThrough => _settingsService.Current.ClickThrough;
    public bool LockWidgetPosition => _settingsService.Current.LockWidgetPosition;
    public double WidgetOpacity => _settingsService.Current.WidgetOpacity;
    public double WidgetWidth => _settingsService.Current.WidgetWidth;
    public bool ShowRemainingInsteadOfUsed => _settingsService.Current.ShowRemainingInsteadOfUsed;
    public string DisplayModeLabel => _settingsService.Current.ShowRemainingInsteadOfUsed ? "% RESTANTE" : "% USADA";
    public bool IsClaudeTokenExpired => _claudeAuthService?.IsExpired ?? false;

    public ICommand RefreshCommand { get; }
    public ICommand ToggleDisplayModeCommand { get; }
    public ICommand ToggleCompactModeCommand { get; }
    public ICommand ToggleClickThroughCommand { get; }
    public ICommand ToggleAlwaysOnTopCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ReconnectClaudeCommand { get; }

    public event EventHandler? UiStateUpdated;

    public MainViewModel(
        SettingsService settingsService,
        QuotaPollingService pollingService,
        Action openSettingsAction,
        ClaudeAuthService? claudeAuthService = null)
    {
        _settingsService = settingsService;
        _pollingService = pollingService;
        _openSettingsAction = openSettingsAction;
        _claudeAuthService = claudeAuthService;

        RefreshCommand = new RelayCommand(_pollingService.RequestRefresh);
        ToggleDisplayModeCommand = new RelayCommand(ToggleDisplayMode);
        ToggleCompactModeCommand = new RelayCommand(ToggleCompactMode);
        ToggleClickThroughCommand = new RelayCommand(ToggleClickThrough);
        ToggleAlwaysOnTopCommand = new RelayCommand(ToggleAlwaysOnTop);
        OpenSettingsCommand = new RelayCommand(() => _openSettingsAction());
        ReconnectClaudeCommand = new RelayCommand(() =>
        {
            if (_claudeAuthService != null)
            {
                _claudeAuthService.TryAutoLogin(force: true);
            }
            else
            {
                ClaudeAuthService.LaunchInteractiveLogin(_settingsService.Current);
            }
        });

        _pollingService.QuotasUpdated += OnQuotasUpdated;
        _pollingService.RefreshStateChanged += OnRefreshStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        if (_claudeAuthService != null)
        {
            _claudeAuthService.TokenExpiredStateChanged += (_, _) =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    OnPropertyChanged(nameof(IsClaudeTokenExpired));
                });
            };
        }
    }

    private void OnRefreshStateChanged(object? sender, bool refreshing)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsRefreshing = refreshing;
            if (refreshing)
            {
                LastUpdatedText = "Atualizando...";
            }
        });
    }

    private void OnQuotasUpdated(object? sender, IReadOnlyList<QuotaItem> items)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            RebuildBars(items);
        });
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(CompactMode));
            OnPropertyChanged(nameof(AlwaysOnTop));
            OnPropertyChanged(nameof(ClickThrough));
            OnPropertyChanged(nameof(LockWidgetPosition));
            OnPropertyChanged(nameof(WidgetOpacity));
            OnPropertyChanged(nameof(WidgetWidth));
            OnPropertyChanged(nameof(ShowRemainingInsteadOfUsed));
            OnPropertyChanged(nameof(DisplayModeLabel));

            RebuildBars(_pollingService.LatestItems);
            UiStateUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RebuildBars(IReadOnlyList<QuotaItem> items)
    {
        AppSettings cfg = _settingsService.Current;

        QuotaBars.Clear();
        foreach (QuotaItem item in items)
        {
            QuotaBars.Add(new QuotaBarViewModel(item, cfg));
        }

        if (_pollingService.LastUpdated.HasValue)
        {
            LastUpdatedText = $"Atualizado {_pollingService.LastUpdated.Value:HH:mm:ss}";
        }

        // Compute overall status for System Tray icon
        var availableBars = QuotaBars.Where(b => b.IsAvailable).ToList();
        if (availableBars.Count == 0)
        {
            OverallStatus = QuotaStatusLevel.Offline;
        }
        else if (availableBars.Any(b => b.StatusLevel == QuotaStatusLevel.Critical))
        {
            OverallStatus = QuotaStatusLevel.Critical;
        }
        else if (availableBars.Any(b => b.StatusLevel == QuotaStatusLevel.Warning))
        {
            OverallStatus = QuotaStatusLevel.Warning;
        }
        else
        {
            OverallStatus = QuotaStatusLevel.Normal;
        }

        TraySummaryToolTip = BuildTraySummary(QuotaBars);
        UiStateUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildTraySummary(IEnumerable<QuotaBarViewModel> bars)
    {
        var sb = new StringBuilder("AI Usage Tracker");
        foreach (QuotaBarViewModel bar in bars.Take(4))
        {
            string val = bar.IsAvailable
                ? string.Create(CultureInfo.InvariantCulture, $"{bar.UsedPercent:0}%")
                : "Off";
            sb.Append($"\n• {bar.Title}: {val}");
        }

        string text = sb.ToString();
        // NotifyIcon.Text limit is 127 chars in WinForms
        return text.Length > 120 ? text[..120] + "..." : text;
    }

    public void ToggleDisplayMode()
    {
        AppSettings clone = _settingsService.Current.Clone();
        clone.ShowRemainingInsteadOfUsed = !clone.ShowRemainingInsteadOfUsed;
        _settingsService.Update(clone);
    }

    public void ToggleCompactMode()
    {
        AppSettings clone = _settingsService.Current.Clone();
        clone.CompactMode = !clone.CompactMode;
        _settingsService.Update(clone);
    }

    public void ToggleClickThrough()
    {
        AppSettings clone = _settingsService.Current.Clone();
        clone.ClickThrough = !clone.ClickThrough;
        _settingsService.Update(clone);
    }

    public void ToggleAlwaysOnTop()
    {
        AppSettings clone = _settingsService.Current.Clone();
        clone.AlwaysOnTop = !clone.AlwaysOnTop;
        _settingsService.Update(clone);
    }

    public void ToggleShowDesktopWidget()
    {
        AppSettings clone = _settingsService.Current.Clone();
        clone.ShowDesktopWidget = !clone.ShowDesktopWidget;
        _settingsService.Update(clone);
    }
}
