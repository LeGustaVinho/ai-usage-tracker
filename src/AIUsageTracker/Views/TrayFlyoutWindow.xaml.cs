using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using AIUsageTracker.Helpers;
using AIUsageTracker.ViewModels;

namespace AIUsageTracker.Views;

public partial class TrayFlyoutWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Action _openSettingsAction;
    private readonly Action _exitApplicationAction;
    private bool _allowClose;

    public TrayFlyoutWindow(
        MainViewModel viewModel,
        Action openSettingsAction,
        Action exitApplicationAction)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _openSettingsAction = openSettingsAction;
        _exitApplicationAction = exitApplicationAction;
        DataContext = _viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.SetToolWindow(hwnd);
    }

    public void ToggleFlyout()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        UpdateLayout();
        PositionNearTaskbarCorner();
        Activate();
    }

    private void PositionNearTaskbarCorner()
    {
        Rect workArea = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 320;

        Left = Math.Max(workArea.Left + 8, workArea.Right - w - 8);
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - h - 8);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void ToggleDesktopWidget_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleShowDesktopWidget();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _openSettingsAction();
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _exitApplicationAction();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
