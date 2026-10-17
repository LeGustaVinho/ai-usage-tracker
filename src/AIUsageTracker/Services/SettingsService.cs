using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIUsageTracker.Models;
using Microsoft.Win32;

namespace AIUsageTracker.Services;

public sealed class SettingsService
{
    private const string StartupRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "AIUsageTracker";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsFilePath { get; }
    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsService(string? customSettingsPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customSettingsPath))
        {
            SettingsFilePath = customSettingsPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "AIUsageTracker");
            Directory.CreateDirectory(dir);
            SettingsFilePath = Path.Combine(dir, "settings.json");
        }

        Current = LoadFromDisk();
    }

    private AppSettings LoadFromDisk()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.StartWithWindows = IsStartupRegistered();
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupted file
        }

        var defaults = new AppSettings
        {
            StartWithWindows = IsStartupRegistered()
        };
        SaveToDisk(defaults);
        return defaults;
    }

    public void Update(AppSettings newSettings, bool notify = true)
    {
        Current = newSettings;
        ApplyWindowsStartup(newSettings.StartWithWindows);
        SaveToDisk(newSettings);
        if (notify)
        {
            SettingsChanged?.Invoke(this, Current);
        }
    }

    public void SavePositionOnly(double left, double top)
    {
        Current.WidgetLeft = left;
        Current.WidgetTop = top;
        SaveToDisk(Current);
    }

    private void SaveToDisk(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore transient IO lock errors
        }
    }

    public static bool IsStartupRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryKeyPath, writable: false);
            return key?.GetValue(StartupValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyWindowsStartup(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(StartupValueName, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(StartupValueName) != null)
                {
                    key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Ignore registry permission issues
        }
    }
}
