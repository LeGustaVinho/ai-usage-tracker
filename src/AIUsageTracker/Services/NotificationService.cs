using System;
using System.Collections.Generic;
using System.Media;
using System.Threading.Tasks;
using AIUsageTracker.Models;

namespace AIUsageTracker.Services;

public sealed class NotificationService
{
    private readonly SettingsService _settingsService;
    private readonly Action<string, string, bool> _showNotificationAction;
    private readonly Dictionary<string, double> _previousUsedPercents = new(StringComparer.OrdinalIgnoreCase);
    private bool _isFirstBaseline = true;

    public NotificationService(
        SettingsService settingsService,
        Action<string, string, bool> showNotificationAction)
    {
        _settingsService = settingsService;
        _showNotificationAction = showNotificationAction;
    }

    public void ProcessQuotasUpdate(IReadOnlyList<QuotaItem> items)
    {
        AppSettings settings = _settingsService.Current;

        // Skip alerting on the very first baseline load
        if (_isFirstBaseline)
        {
            _isFirstBaseline = false;
            foreach (var item in items)
            {
                if (item.IsAvailable)
                {
                    _previousUsedPercents[item.Id] = item.UsedPercent;
                }
            }
            return;
        }

        if (!settings.EnableResetNotifications)
        {
            foreach (var item in items)
            {
                if (item.IsAvailable)
                {
                    _previousUsedPercents[item.Id] = item.UsedPercent;
                }
            }
            return;
        }

        foreach (var item in items)
        {
            if (!item.IsAvailable)
            {
                continue;
            }

            if (_previousUsedPercents.TryGetValue(item.Id, out double prevUsed))
            {
                // When used percent drops, available quota increases
                double increase = prevUsed - item.UsedPercent;
                double minIncrease = Math.Max(1.0, settings.MinQuotaIncreaseToNotifyPercent);

                if (increase >= minIncrease)
                {
                    bool isFullReset = item.UsedPercent <= 1.0;
                    string title = isFullReset
                        ? $"Cota Resetada: {item.Title}"
                        : $"Cota Aumentou: {item.Title}";

                    string message = isFullReset
                        ? $"{item.Title} foi totalmente resetado! Cota 100% disponível."
                        : $"{item.Title} aumentou cota disponível em +{increase:0}% (Agora: {item.RemainingPercent:0}% disponível).";

                    TriggerNotification(title, message, settings.PlaySoundOnNotification);
                }
            }

            _previousUsedPercents[item.Id] = item.UsedPercent;
        }
    }

    public void TriggerTestNotification()
    {
        AppSettings settings = _settingsService.Current;
        TriggerNotification(
            "AI Usage Tracker — Teste",
            "Notificação e som funcionando perfeitamente no Windows!",
            settings.PlaySoundOnNotification);
    }

    public void NotifyClaudeTokenExpired()
    {
        AppSettings settings = _settingsService.Current;
        TriggerNotification(
            "Claude Code — Token Expirado",
            "O token de acesso do Claude Code expirou. Clique em Reconectar no HUD para autenticar novamente.",
            settings.PlaySoundOnNotification);
    }

    private void TriggerNotification(string title, string message, bool playSound)
    {
        _showNotificationAction(title, message, playSound);

        if (playSound)
        {
            Task.Run(() =>
            {
                try
                {
                    SystemSounds.Asterisk.Play();
                }
                catch
                {
                    try
                    {
                        SystemSounds.Beep.Play();
                    }
                    catch { }
                }
            });
        }
    }
}
