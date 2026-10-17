using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIUsageTracker.Services;
using AIUsageTracker.ViewModels;
using AIUsageTracker.Views;

namespace AIUsageTracker;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private SettingsService? _settingsService;
    private QuotaPollingService? _pollingService;
    private MainViewModel? _mainViewModel;
    private DesktopWidgetWindow? _desktopWidgetWindow;
    private TrayFlyoutWindow? _trayFlyoutWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIconService? _trayIconService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. CLI verification mode (--verify-providers <outputPath>)
        int verifyIdx = Array.FindIndex(e.Args, a => a.Equals("--verify-providers", StringComparison.OrdinalIgnoreCase));
        if (verifyIdx >= 0)
        {
            string outPath = verifyIdx + 1 < e.Args.Length
                ? e.Args[verifyIdx + 1]
                : Path.Combine(AppContext.BaseDirectory, "providers-verification.json");

            await RunProviderVerificationAsync(outPath);
            Shutdown(0);
            return;
        }

        // 2. CLI visual preview capture mode (--capture-preview <pngPath>)
        int captureIdx = Array.FindIndex(e.Args, a => a.Equals("--capture-preview", StringComparison.OrdinalIgnoreCase));
        if (captureIdx >= 0 && captureIdx + 1 < e.Args.Length)
        {
            string pngPath = e.Args[captureIdx + 1];
            await RunCapturePreviewAsync(pngPath);
            Shutdown(0);
            return;
        }

        // 3. Normal Single-Instance Desktop HUD + System Tray Application
        _singleInstanceMutex = new Mutex(true, @"Local\AIUsageTracker_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        _settingsService = new SettingsService();
        _pollingService = new QuotaPollingService(_settingsService);
        _mainViewModel = new MainViewModel(_settingsService, _pollingService, OpenSettingsWindow);

        _desktopWidgetWindow = new DesktopWidgetWindow(_settingsService, _mainViewModel);
        _trayFlyoutWindow = new TrayFlyoutWindow(_mainViewModel, OpenSettingsWindow, ExitApplication);

        _trayIconService = new TrayIconService(
            _settingsService,
            _mainViewModel,
            ToggleTrayFlyout,
            OpenSettingsWindow,
            ExitApplication);

        if (_settingsService.Current.ShowDesktopWidget)
        {
            _desktopWidgetWindow.Show();
        }

        _pollingService.Start();
    }

    private async Task RunProviderVerificationAsync(string outputPath)
    {
        var settings = new SettingsService();
        using var polling = new QuotaPollingService(settings);
        var items = await polling.RefreshNowAsync();

        string json = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json);
    }

    private async Task RunCapturePreviewAsync(string pngPath)
    {
        var settings = new SettingsService();
        using var polling = new QuotaPollingService(settings);
        var vm = new MainViewModel(settings, polling, () => { });

        await polling.RefreshNowAsync();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        double targetWidth = Math.Clamp(settings.Current.WidgetWidth, 320, 500);
        var widget = new DesktopWidgetWindow(settings, vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -2000,
            Top = -2000,
            Width = targetWidth,
            MaxWidth = targetWidth,
            Opacity = 1.0
        };

        widget.Show();
        widget.Width = targetWidth;
        widget.UpdateLayout();
        await Task.Delay(250);
        widget.UpdateLayout();

        var rootElement = (FrameworkElement)widget.Content;
        int pixelWidth = Math.Max(350, (int)Math.Ceiling(rootElement.ActualWidth + 20));
        int pixelHeight = Math.Max(250, (int)Math.Ceiling(rootElement.ActualHeight + 20));

        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(rootElement);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        string? dir = Path.GetDirectoryName(pngPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using (var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
        {
            encoder.Save(fs);
        }

        widget.ForceClose();
    }

    private void ToggleTrayFlyout()
    {
        _trayFlyoutWindow?.ToggleFlyout();
    }

    private void OpenSettingsWindow()
    {
        if (_settingsService == null) return;

        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsService);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitApplication()
    {
        _trayIconService?.Dispose();
        _pollingService?.Dispose();
        _trayFlyoutWindow?.ForceClose();
        _desktopWidgetWindow?.ForceClose();
        _settingsWindow?.Close();

        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore mutex release errors
            }
            _singleInstanceMutex.Dispose();
        }

        Shutdown(0);
    }
}
