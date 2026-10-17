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
}
