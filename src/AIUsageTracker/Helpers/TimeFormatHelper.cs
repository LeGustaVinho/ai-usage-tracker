using System;

namespace AIUsageTracker.Helpers;

public static class TimeFormatHelper
{
    public static string FormatResetCountdown(DateTimeOffset? resetTime)
    {
        if (!resetTime.HasValue)
        {
            return string.Empty;
        }

        TimeSpan remaining = resetTime.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "↻ agora";
        }

        if (remaining.TotalDays >= 1.0)
        {
            int days = (int)remaining.TotalDays;
            int hours = remaining.Hours;
            return hours > 0 ? $"↻ {days}d {hours}h" : $"↻ {days}d";
        }

        if (remaining.TotalHours >= 1.0)
        {
            int hours = (int)remaining.TotalHours;
            int minutes = remaining.Minutes;
            return minutes > 0 ? $"↻ {hours}h {minutes}m" : $"↻ {hours}h";
        }

        int mins = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"↻ {mins}m";
    }

    public static string FormatResetFullLocal(DateTimeOffset? resetTime)
    {
        if (!resetTime.HasValue)
        {
            return "Horário de reset indisponível";
        }

        DateTimeOffset local = resetTime.Value.ToLocalTime();
        string countdown = FormatResetCountdown(resetTime);
        return $"Reseta em {local:dd/MM/yyyy HH:mm} ({countdown.Replace("↻ ", "")})";
    }

    public static string FormatWindowDuration(int? seconds)
    {
        if (!seconds.HasValue || seconds.Value <= 0)
        {
            return string.Empty;
        }

        int s = seconds.Value;
        if (s % 86400 == 0)
        {
            int days = s / 86400;
            return $"{days}d";
        }

        if (s % 3600 == 0)
        {
            int hours = s / 3600;
            return $"{hours}h";
        }

        int mins = s / 60;
        return $"{mins}m";
    }

    public static TimeSpan ResolveWindowDuration(Models.QuotaItem item)
    {
        TimeSpan window = item.WindowDuration is { TotalSeconds: > 0 } explicitWindow
            ? explicitWindow
            : InferWindowDurationFromItem(item);

        if (item.ResetTime.HasValue)
        {
            TimeSpan remaining = item.ResetTime.Value - DateTimeOffset.UtcNow;
            if (remaining > window)
            {
                window = remaining <= TimeSpan.FromDays(7)
                    ? TimeSpan.FromDays(7)
                    : remaining;
            }
        }

        return window;
    }

    public static double CalculateResetTimeRemainingPercent(Models.QuotaItem item)
    {
        if (!item.ResetTime.HasValue)
        {
            return 0.0;
        }

        TimeSpan remaining = item.ResetTime.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return 0.0;
        }

        TimeSpan window = ResolveWindowDuration(item);
        if (window.TotalSeconds <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(remaining.TotalSeconds / window.TotalSeconds * 100.0, 0.0, 100.0);
    }

    private static TimeSpan InferWindowDurationFromItem(Models.QuotaItem item)
    {
        if (item.Title.Contains("(5h)", StringComparison.OrdinalIgnoreCase) ||
            item.Id.Equals("claude.session", StringComparison.OrdinalIgnoreCase) ||
            item.Id.Equals("agy.gemini_session", StringComparison.OrdinalIgnoreCase) ||
            item.Id.StartsWith("agy.gemini_flash", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(5);
        }

        if (item.Title.Contains("(7d)", StringComparison.OrdinalIgnoreCase) ||
            item.Id.Contains("weekly", StringComparison.OrdinalIgnoreCase) ||
            item.Id.Contains("argon", StringComparison.OrdinalIgnoreCase) ||
            item.Id.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
            item.Id.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ||
            item.Id.Contains("gpt_oss", StringComparison.OrdinalIgnoreCase) ||
            item.Provider == Models.QuotaProviderType.Codex)
        {
            return TimeSpan.FromDays(7);
        }

        if (item.ResetTime.HasValue)
        {
            TimeSpan remaining = item.ResetTime.Value - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(5))
            {
                return TimeSpan.FromHours(5);
            }
        }

        return TimeSpan.FromDays(7);
    }
}
