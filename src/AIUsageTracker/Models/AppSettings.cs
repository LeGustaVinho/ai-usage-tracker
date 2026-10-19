using System;
using System.IO;
using System.Text.Json;

namespace AIUsageTracker.Models;

public sealed class AppSettings
{
    // Presença e Janela
    public bool ShowDesktopWidget { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public bool LockWidgetPosition { get; set; } = false;
    public bool CompactMode { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;

    // Coordenadas salvas do Widget
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public double WidgetWidth { get; set; } = 365.0;
    public double WidgetOpacity { get; set; } = 0.92;

    // Exibição e Semântica das Barras
    public bool ShowRemainingInsteadOfUsed { get; set; } = false;
    public bool ShowResetCountdown { get; set; } = true;
    public bool ShowSubtitles { get; set; } = true;
    public double WarningThresholdPercent { get; set; } = 60.0;
    public double CriticalThresholdPercent { get; set; } = 85.0;
    public int RefreshIntervalSeconds { get; set; } = 60;

    // Notificações e Sons
    public bool EnableResetNotifications { get; set; } = true;
    public bool PlaySoundOnNotification { get; set; } = true;
    public double MinQuotaIncreaseToNotifyPercent { get; set; } = 5.0;

    // ChatGPT / Codex
    public bool EnableCodex { get; set; } = true;
    public bool ShowCodexPrimary { get; set; } = true;
    public bool ShowCodexSecondary { get; set; } = true;
    public string CustomCodexAuthPath { get; set; } = string.Empty;

    // Claude Code CLI (Opus / Sonnet / Sessão / Semanal)
    public bool EnableClaudeCode { get; set; } = true;
    public bool ShowClaudeSession5h { get; set; } = true;
    public bool ShowClaudeWeekly7d { get; set; } = true;
    public bool ShowClaudeOpus { get; set; } = true;
    public bool ShowClaudeSonnet { get; set; } = true;
    public bool ShowClaudeDedicatedEvenIfEmpty { get; set; } = false;
    public bool ShowClaudeSessionCostSubtitle { get; set; } = true;
    public bool AutoPromptClaudeLoginOnExpiration { get; set; } = true;
    public string CustomClaudeCredentialsPath { get; set; } = string.Empty;

    // Gemini / Antigravity
    public bool EnableAntigravity { get; set; } = true;
    public bool GroupAntigravityEffortLevels { get; set; } = true;
    public bool ShowAntigravityArgon { get; set; } = true;
    public bool ShowAntigravityFlash { get; set; } = true;
    public bool ShowAntigravityGeminiWeekly { get; set; } = true;
    public bool ShowAntigravityClaudeOpus { get; set; } = true;
    public bool ShowAntigravityClaudeSonnet { get; set; } = true;
    public bool ShowAntigravityGptOss { get; set; } = false;
    public bool EnableAntigravityCliFallback { get; set; } = true;
    public string CustomAgyCliPath { get; set; } = string.Empty;

    public string GetEffectiveAgyCliPath()
    {
        if (!string.IsNullOrWhiteSpace(CustomAgyCliPath))
        {
            return Environment.ExpandEnvironmentVariables(CustomAgyCliPath);
        }
        string localAgy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
        if (File.Exists(localAgy))
        {
            return localAgy;
        }
        return "agy.exe";
    }

    public string GetEffectiveCodexAuthPath()
    {
        if (!string.IsNullOrWhiteSpace(CustomCodexAuthPath))
        {
            return Environment.ExpandEnvironmentVariables(CustomCodexAuthPath);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
    }

    public string GetEffectiveClaudeCredentialsPath()
    {
        if (!string.IsNullOrWhiteSpace(CustomClaudeCredentialsPath))
        {
            return Environment.ExpandEnvironmentVariables(CustomClaudeCredentialsPath);
        }
        string? configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
            return Path.Combine(Environment.ExpandEnvironmentVariables(configDir), ".credentials.json");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
    }

    public AppSettings Clone()
    {
        string json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }
}
