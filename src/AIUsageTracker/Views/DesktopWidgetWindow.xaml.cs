using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AIUsageTracker.Helpers;
using AIUsageTracker.Models;
using AIUsageTracker.Services;
using AIUsageTracker.ViewModels;

namespace AIUsageTracker.Views;

public partial class DesktopWidgetWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly MainViewModel _viewModel;
    private IntPtr _hwnd;
    private DispatcherOperation? _restoreOperation;
    private bool _allowClose;

    public DesktopWidgetWindow(SettingsService settingsService, MainViewModel viewModel)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _viewModel = viewModel;
        DataContext = _viewModel;

        Width = Math.Clamp(_settingsService.Current.WidgetWidth, 260, 600);
        ApplySavedOrDefaultPosition();
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.SetToolWindow(_hwnd);
        Win32Interop.SetClickThrough(_hwnd, _settingsService.Current.ClickThrough);
        HwndSource.FromHwnd(_hwnd)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32Interop.WM_WINDOWPOSCHANGED)
        {
            QueueTopmostRestore();
        }

        return IntPtr.Zero;
    }

    private bool ShouldKeepOnTop => !_allowClose && _hwnd != IntPtr.Zero && IsVisible
        && _settingsService.Current.ShowDesktopWidget && _settingsService.Current.AlwaysOnTop;

    private void QueueTopmostRestore()
    {
        if (_restoreOperation != null || !ShouldKeepOnTop || Win32Interop.IsTopmostWindowVisible(_hwnd))
        {
            return;
        }

        // Let WPF finish processing the native position change before restoring the HWND.
        _restoreOperation = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (ShouldKeepOnTop)
                {
                    Win32Interop.RestoreTopmostWindow(_hwnd);
                }
            }
            finally
            {
                _restoreOperation = null;
            }
        }, DispatcherPriority.Background);
    }

    private void OnSettingsChanged(object? sender, AppSettings _)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_allowClose) return;

            AppSettings settings = _settingsService.Current;
            Width = Math.Clamp(settings.WidgetWidth, 260, 600);
            if (_hwnd != IntPtr.Zero)
            {
                Win32Interop.SetClickThrough(_hwnd, settings.ClickThrough);
            }

            if (!settings.WidgetLeft.HasValue || !settings.WidgetTop.HasValue)
            {
                ApplySavedOrDefaultPosition();
            }

            if (settings.ShowDesktopWidget && !IsVisible)
            {
                Show();
            }
            else if (!settings.ShowDesktopWidget && IsVisible)
            {
                Hide();
            }

            QueueTopmostRestore();
        });
    }

    private void ApplySavedOrDefaultPosition()
    {
        AppSettings cfg = _settingsService.Current;
        Rect workArea = SystemParameters.WorkArea;
        double width = Math.Clamp(cfg.WidgetWidth, 260, 600);
        Width = width;

        if (cfg.WidgetLeft.HasValue && cfg.WidgetTop.HasValue)
        {
            Left = Math.Clamp(cfg.WidgetLeft.Value, workArea.Left - 50, workArea.Right - 100);
            Top = Math.Clamp(cfg.WidgetTop.Value, workArea.Top, workArea.Bottom - 80);
        }
        else
        {
            Left = Math.Max(workArea.Left + 20, workArea.Right - width - 24);
            Top = workArea.Top + 24;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_settingsService.Current.LockWidgetPosition)
        {
            try
            {
                DragMove();
                _settingsService.SavePositionOnly(Left, Top);
            }
            catch
            {
                // Ignore if mouse released before DragMove started
            }
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleShowDesktopWidget();
    }

    public void ForceClose()
    {
        _allowClose = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _restoreOperation?.Abort();
        HwndSource.FromHwnd(_hwnd)?.RemoveHook(OnWindowMessage);
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _viewModel.ToggleShowDesktopWidget();
            return;
        }

        base.OnClosing(e);
    }
}
