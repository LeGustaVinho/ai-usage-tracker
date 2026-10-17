using System.Windows;
using AIUsageTracker.Services;
using AIUsageTracker.ViewModels;

namespace AIUsageTracker.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(settingsService, Close);
    }
}
