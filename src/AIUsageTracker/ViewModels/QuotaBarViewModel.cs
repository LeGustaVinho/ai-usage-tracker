using System;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using AIUsageTracker.Helpers;
using AIUsageTracker.Models;

namespace AIUsageTracker.ViewModels;

public sealed class QuotaBarViewModel : ObservableObject
{
    private static readonly Brush NormalBrush = CreateGradient("#10B981", "#34D399");
    private static readonly Brush WarningBrush = CreateGradient("#F59E0B", "#FBBF24");
    private static readonly Brush CriticalBrush = CreateGradient("#EF4444", "#F87171");
    private static readonly Brush OfflineBrush = CreateSolid("#475569");

    private static readonly Brush NormalTextBrush = CreateSolid("#34D399");
    private static readonly Brush WarningTextBrush = CreateSolid("#FBBF24");
    private static readonly Brush CriticalTextBrush = CreateSolid("#F87171");
    private static readonly Brush OfflineTextBrush = CreateSolid("#94A3B8");

    public string Id { get; }
    public string ProviderBadge { get; }
    public Brush BadgeBrush { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public bool ShowSubtitle { get; }
    public bool IsAvailable { get; }
    public double UsedPercent { get; }
    public double RemainingPercent { get; }
    public double DisplayPercentValue { get; }
    public string DisplayPercentText { get; }
    public string ResetCountdownText { get; }
    public bool HasResetCountdown { get; }
    public QuotaStatusLevel StatusLevel { get; }
    public Brush BarFillBrush { get; }
    public Brush StatusAccentBrush { get; }
    public string ToolTipText { get; }

    public QuotaBarViewModel(QuotaItem item, AppSettings settings)
    {
        Id = item.Id;
        ProviderBadge = item.ProviderBadge;
        BadgeBrush = CreateSolid(item.BadgeColorHex);
        Title = item.Title;
        Subtitle = item.Subtitle;
        ShowSubtitle = settings.ShowSubtitles && !settings.CompactMode && !string.IsNullOrWhiteSpace(item.Subtitle);
        IsAvailable = item.IsAvailable;
        UsedPercent = item.UsedPercent;
        RemainingPercent = item.RemainingPercent;

        if (!item.IsAvailable)
        {
            StatusLevel = QuotaStatusLevel.Offline;
            DisplayPercentValue = 0;
            DisplayPercentText = "OFFLINE";
            ResetCountdownText = string.Empty;
            HasResetCountdown = false;
            BarFillBrush = OfflineBrush;
            StatusAccentBrush = OfflineTextBrush;
        }
        else
        {
            if (item.UsedPercent >= settings.CriticalThresholdPercent)
            {
                StatusLevel = QuotaStatusLevel.Critical;
                BarFillBrush = CriticalBrush;
                StatusAccentBrush = CriticalTextBrush;
            }
            else if (item.UsedPercent >= settings.WarningThresholdPercent)
            {
                StatusLevel = QuotaStatusLevel.Warning;
                BarFillBrush = WarningBrush;
                StatusAccentBrush = WarningTextBrush;
            }
            else
            {
                StatusLevel = QuotaStatusLevel.Normal;
                BarFillBrush = NormalBrush;
                StatusAccentBrush = NormalTextBrush;
            }

            DisplayPercentValue = settings.ShowRemainingInsteadOfUsed
                ? item.RemainingPercent
                : item.UsedPercent;

            string numStr = FormatPercentNumber(DisplayPercentValue);
            string suffix = settings.CompactMode
                ? "%"
                : (settings.ShowRemainingInsteadOfUsed ? "% rest." : "% usado");

            DisplayPercentText = $"{numStr}{suffix}";

            string countdown = settings.ShowResetCountdown
                ? TimeFormatHelper.FormatResetCountdown(item.ResetTime)
                : string.Empty;

            ResetCountdownText = countdown;
            HasResetCountdown = !string.IsNullOrWhiteSpace(countdown);
        }

        ToolTipText = BuildToolTip(item);
    }

    private static string FormatPercentNumber(double value)
    {
        if (value <= 0.05) return "0";
        if (value >= 99.95) return "100";
        if (Math.Abs(value - Math.Round(value)) < 0.15)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
        return value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string BuildToolTip(QuotaItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{item.Title} [{item.ProviderBadge}]");
        if (item.IsAvailable)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Consumido: {item.UsedPercent:0.0}%  |  Restante: {item.RemainingPercent:0.0}%"));
            if (item.ResetTime.HasValue)
            {
                sb.AppendLine(TimeFormatHelper.FormatResetFullLocal(item.ResetTime));
            }
        }
        else
        {
            sb.AppendLine("Status: Indisponível / Offline");
        }

        if (!string.IsNullOrWhiteSpace(item.Subtitle))
        {
            sb.AppendLine(item.Subtitle);
        }

        if (!string.IsNullOrWhiteSpace(item.SourceDetail))
        {
            sb.Append($"Fonte: {item.SourceDetail}");
        }

        return sb.ToString().TrimEnd();
    }

    private static Brush CreateSolid(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            var fallback = new SolidColorBrush(Colors.DodgerBlue);
            fallback.Freeze();
            return fallback;
        }
    }

    private static Brush CreateGradient(string startHex, string endHex)
    {
        var c1 = (Color)ColorConverter.ConvertFromString(startHex);
        var c2 = (Color)ColorConverter.ConvertFromString(endHex);
        var brush = new LinearGradientBrush(c1, c2, 0.0);
        brush.Freeze();
        return brush;
    }
}
