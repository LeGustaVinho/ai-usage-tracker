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

    [Fact]
    public void NotificationService_TriggersOnQuotaResetOrIncrease()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"notif_test_{Guid.NewGuid():N}.json");
        try
        {
            var settingsService = new SettingsService(tempPath);
            string? notifiedTitle = null;
            string? notifiedMsg = null;
            bool soundPlayed = false;

            var notifService = new NotificationService(settingsService, (title, msg, playSound) =>
            {
                notifiedTitle = title;
                notifiedMsg = msg;
                soundPlayed = playSound;
            });

            var item1 = new QuotaItem
            {
                Id = "claude.session",
                Provider = QuotaProviderType.ClaudeCode,
                ProviderBadge = "CLAUDE",
                BadgeColorHex = "#D97757",
                Title = "Claude Code (5h)",
                UsedPercent = 100.0,
                IsAvailable = true
            };

            // Baseline: should not trigger
            notifService.ProcessQuotasUpdate(new[] { item1 });
            Assert.Null(notifiedTitle);

            // Quota resets to 0% used: MUST trigger notification with sound!
            var itemReset = new QuotaItem
            {
                Id = "claude.session",
                Provider = QuotaProviderType.ClaudeCode,
                ProviderBadge = "CLAUDE",
                BadgeColorHex = "#D97757",
                Title = "Claude Code (5h)",
                UsedPercent = 0.0,
                IsAvailable = true
            };

            notifService.ProcessQuotasUpdate(new[] { itemReset });
            Assert.NotNull(notifiedTitle);
            Assert.Contains("Claude Code (5h)", notifiedTitle);
            Assert.Contains("resetado", notifiedMsg);
            Assert.True(soundPlayed);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void ClaudeAuthService_DetectsExpiredAndValidTokensCorrectly()
    {
        string tempCreds = Path.Combine(Path.GetTempPath(), $"claude_test_creds_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings
            {
                CustomClaudeCredentialsPath = tempCreds
            };

            // Test 1: Empty token and expired timestamp
            string expiredJson = @"{""claudeAiOauth"":{""accessToken"":"""",""expiresAt"":0}}";
            File.WriteAllText(tempCreds, expiredJson);
            Assert.True(ClaudeAuthService.IsTokenExpiredStatic(settings));

            // Test 2: Past timestamp
            long pastMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
            string pastJson = @$"{{""claudeAiOauth"":{{""accessToken"":""mock_token"",""expiresAt"":{pastMs}}}}}";
            File.WriteAllText(tempCreds, pastJson);
            Assert.True(ClaudeAuthService.IsTokenExpiredStatic(settings));

            // Test 3: Valid future token
            long futureMs = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
            string validJson = @$"{{""claudeAiOauth"":{{""accessToken"":""mock_token"",""expiresAt"":{futureMs}}}}}";
            File.WriteAllText(tempCreds, validJson);
            Assert.False(ClaudeAuthService.IsTokenExpiredStatic(settings));
        }
        finally
        {
            if (File.Exists(tempCreds)) File.Delete(tempCreds);
        }
    }

    [Fact]
    public void ClaudeAuthService_DetectsTokenExpiringSoonCorrectly()
    {
        string tempCreds = Path.Combine(Path.GetTempPath(), $"claude_test_creds_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings
            {
                CustomClaudeCredentialsPath = tempCreds
            };

            // Token expiring in 2 minutes
            long expiringSoonMs = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds();
            string expiringSoonJson = @$"{{""claudeAiOauth"":{{""accessToken"":""mock_token"",""expiresAt"":{expiringSoonMs}}}}}";
            File.WriteAllText(tempCreds, expiringSoonJson);

            // With 5 minutes buffer, it SHOULD flag as expiring soon
            Assert.True(ClaudeAuthService.IsTokenExpiringSoonStatic(settings, TimeSpan.FromMinutes(5)));
            // With 1 minute buffer, it should NOT flag yet
            Assert.False(ClaudeAuthService.IsTokenExpiringSoonStatic(settings, TimeSpan.FromMinutes(1)));
        }
        finally
        {
            if (File.Exists(tempCreds)) File.Delete(tempCreds);
        }
    }
}

