using System;
using System.IO;
using System.Threading.Tasks;
using AIUsageTracker.Helpers;
using AIUsageTracker.Models;
using AIUsageTracker.Services;
using AIUsageTracker.Services.Providers;
using AIUsageTracker.ViewModels;
using Xunit;

namespace AIUsageTracker.Tests;

public class QuotaTrackerTests
{
    [Fact]
    public void TimeFormatHelper_FormatsWindowDurationAndCountdownCorrectly()
    {
        Assert.Equal("7d", TimeFormatHelper.FormatWindowDuration(604800));
        Assert.Equal("5h", TimeFormatHelper.FormatWindowDuration(18000));
        Assert.Equal("30m", TimeFormatHelper.FormatWindowDuration(1800));
        Assert.Equal(string.Empty, TimeFormatHelper.FormatWindowDuration(null));

        string futureDays = TimeFormatHelper.FormatResetCountdown(DateTimeOffset.UtcNow.AddDays(3).AddHours(5));
        Assert.StartsWith("↻ 3d", futureDays);

        string expired = TimeFormatHelper.FormatResetCountdown(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal("↻ agora", expired);
    }

    [Fact]
    public void QuotaBarViewModel_RespectsUsedVsRemainingAndThresholds()
    {
        var settings = new AppSettings
        {
            WarningThresholdPercent = 60.0,
            CriticalThresholdPercent = 85.0,
            ShowRemainingInsteadOfUsed = false
        };

        var itemWarning = new QuotaItem
        {
            Id = "test.codex",
            Provider = QuotaProviderType.Codex,
            ProviderBadge = "CODEX",
            BadgeColorHex = "#10A37F",
            Title = "ChatGPT / Codex",
            UsedPercent = 80.0,
            IsAvailable = true
        };

        var vmUsed = new QuotaBarViewModel(itemWarning, settings);
        Assert.Equal(QuotaStatusLevel.Warning, vmUsed.StatusLevel);
        Assert.Equal(80.0, vmUsed.DisplayPercentValue);
        Assert.Equal("80% usado", vmUsed.DisplayPercentText);

        // Invert to Remaining % mode: status must STILL be Warning, but bar shows 20% rest.
        settings.ShowRemainingInsteadOfUsed = true;
        var vmRemaining = new QuotaBarViewModel(itemWarning, settings);
        Assert.Equal(QuotaStatusLevel.Warning, vmRemaining.StatusLevel);
        Assert.Equal(20.0, vmRemaining.DisplayPercentValue);
        Assert.Equal("20% rest.", vmRemaining.DisplayPercentText);
    }

    [Fact]
    public void SettingsService_SavesAndReloadsConfiguration()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"ai_tracker_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(tempPath);
            var modified = service.Current.Clone();
            modified.WidgetWidth = 410.0;
            modified.ShowRemainingInsteadOfUsed = true;
            modified.WarningThresholdPercent = 55.0;
            service.Update(modified);

            var reloaded = new SettingsService(tempPath);
            Assert.Equal(410.0, reloaded.Current.WidgetWidth);
            Assert.True(reloaded.Current.ShowRemainingInsteadOfUsed);
            Assert.Equal(55.0, reloaded.Current.WarningThresholdPercent);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public async Task Providers_ReturnValidQuotaItemsFromEnvironment()
    {
        var settings = new AppSettings();

        var codexProvider = new CodexQuotaProvider();
        var codexItems = await codexProvider.FetchQuotasAsync(settings);
        Assert.NotEmpty(codexItems);

        var claudeProvider = new ClaudeCodeQuotaProvider();
        var claudeItems = await claudeProvider.FetchQuotasAsync(settings);
        Assert.NotEmpty(claudeItems);

        var agyProvider = new AntigravityQuotaProvider();
        var agyItems = await agyProvider.FetchQuotasAsync(settings);
        Assert.NotEmpty(agyItems);
    }
}
