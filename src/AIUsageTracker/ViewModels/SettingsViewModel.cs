using System;
using System.Windows.Input;
using AIUsageTracker.Models;
using AIUsageTracker.Services;

namespace AIUsageTracker.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly Action _closeWindowAction;
    private AppSettings _draft;

    public AppSettings Draft
    {
        get => _draft;
        private set => SetProperty(ref _draft, value);
    }

    public string SettingsFilePath => _settingsService.SettingsFilePath;

    public bool ShowUsedMode
    {
        get => !Draft.ShowRemainingInsteadOfUsed;
        set
        {
            if (Draft.ShowRemainingInsteadOfUsed == value)
            {
                Draft.ShowRemainingInsteadOfUsed = !value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowRemainingMode));
            }
        }
    }

    public bool ShowRemainingMode
    {
        get => Draft.ShowRemainingInsteadOfUsed;
        set
        {
            if (Draft.ShowRemainingInsteadOfUsed != value)
            {
                Draft.ShowRemainingInsteadOfUsed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowUsedMode));
            }
        }
    }

    public double OpacityPercent
    {
        get => Math.Round(Draft.WidgetOpacity * 100.0);
        set
        {
            double clamped = Math.Clamp(value, 25.0, 100.0);
            Draft.WidgetOpacity = clamped / 100.0;
            OnPropertyChanged();
        }
    }

    private readonly Action? _testNotificationAction;

    public bool IsClaudeTokenExpired => ClaudeAuthService.IsTokenExpiredStatic(_settingsService.Current);
    public string ClaudeTokenStatusText => IsClaudeTokenExpired ? "Token Expirado (Reautenticação necessária)" : "Token Ativo (Autenticado)";
    public string ClaudeTokenStatusColorHex => IsClaudeTokenExpired ? "#EF4444" : "#10B981";

    public ICommand SaveCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ResetPositionCommand { get; }
    public ICommand RestoreDefaultsCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand TestNotificationCommand { get; }
    public ICommand ReconnectClaudeCommand { get; }

    public SettingsViewModel(SettingsService settingsService, Action closeWindowAction, Action? testNotificationAction = null)
    {
        _settingsService = settingsService;
        _closeWindowAction = closeWindowAction;
        _testNotificationAction = testNotificationAction;
        _draft = _settingsService.Current.Clone();

        TestNotificationCommand = new RelayCommand(() => _testNotificationAction?.Invoke());
        ReconnectClaudeCommand = new RelayCommand(() => ClaudeAuthService.LaunchInteractiveLogin());
        ApplyCommand = new RelayCommand(Apply);
        SaveCommand = new RelayCommand(() =>
        {
            Apply();
            _closeWindowAction();
        });
        ResetPositionCommand = new RelayCommand(() =>
        {
            Draft.WidgetLeft = null;
            Draft.WidgetTop = null;
            Apply();
        });
        RestoreDefaultsCommand = new RelayCommand(() =>
        {
            Draft = new AppSettings();
            OnPropertyChanged(nameof(ShowUsedMode));
            OnPropertyChanged(nameof(ShowRemainingMode));
            OnPropertyChanged(nameof(OpacityPercent));
        });
        CancelCommand = new RelayCommand(_closeWindowAction);
    }

    private void Apply()
    {
        if (Draft.CriticalThresholdPercent <= Draft.WarningThresholdPercent)
        {
            Draft.CriticalThresholdPercent = Math.Min(100.0, Draft.WarningThresholdPercent + 10.0);
        }

        Draft.RefreshIntervalSeconds = Math.Clamp(Draft.RefreshIntervalSeconds, 10, 3600);
        Draft.WidgetWidth = Math.Clamp(Draft.WidgetWidth, 260.0, 600.0);
        _settingsService.Update(Draft.Clone());
    }
}
