using AIUsageTracker.Models;
using AIUsageTracker.Services;
using AIUsageTracker.Services.Providers;
using AIUsageTracker.ViewModels;

namespace AIUsageTracker.Tests;

public class QuotaPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableIndicatorsShowMessageWithoutPercentOrCountdown(bool compactMode)
    {
        var settings = new AppSettings { CompactMode = compactMode, ShowSubtitles = false };

        foreach (QuotaItem item in GetUnavailableIndicators(settings))
        {
            var bar = new QuotaBarViewModel(item, settings);

            Assert.True(bar.HasUnavailableMessage);
            Assert.Equal(item.UnavailableMessage, bar.UnavailableMessage);
            Assert.False(bar.ShowQuotaBar);
            Assert.Empty(bar.DisplayPercentText);
            Assert.False(bar.HasResetCountdown);
            Assert.False(bar.HasResetProgress);
            Assert.DoesNotContain("%", bar.ToolTipText);
        }
    }

    [Fact]
    public void CarbonWithoutReportedDurationShowsResetTimeWithoutCycleProgress()
    {
        string reset = DateTimeOffset.UtcNow.AddHours(2).ToString("O");
        string status = $$"""
        { "userStatus": { "cascadeModelConfigData": { "clientModelConfigs": [{
            "label": "Gemini 4 Carbon",
            "quotaInfo": { "remainingFraction": 0.6, "resetTime": "{{reset}}" }
        }] } } }
        """;
        var settings = new AppSettings { ShowAntigravityArgon = false };
        var carbon = Assert.Single(AntigravityQuotaProvider.ParseQuotaResponses(status, null, settings));

        var bar = new QuotaBarViewModel(carbon, settings);

        Assert.Equal("40% usado", bar.DisplayPercentText);
        Assert.True(bar.HasResetCountdown);
        Assert.False(bar.HasResetProgress);
        Assert.DoesNotContain("Ciclo restante", bar.ToolTipText);
    }

    [Fact]
    public void TransitionToUnavailableDoesNotNotifyAQuotaReset()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quota_notification_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path);
            var notifications = new List<string>();
            var notifier = new NotificationService(service, (title, _, _) => notifications.Add(title));
            QuotaItem[] unavailable = GetUnavailableIndicators(service.Current);
            var available = unavailable.Select(item => new QuotaItem
            {
                Id = item.Id,
                Provider = item.Provider,
                ProviderBadge = item.ProviderBadge,
                BadgeColorHex = item.BadgeColorHex,
                Title = item.Title,
                UsedPercent = 80
            }).ToList();

            notifier.ProcessQuotasUpdate(available);
            notifier.ProcessQuotasUpdate(unavailable);

            Assert.Empty(notifications);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CachedBalanceDoesNotChangeTheLiveNotificationBaseline()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quota_notification_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = service.Current.Clone();
            settings.PlaySoundOnNotification = false;
            service.Update(settings);
            var notifications = new List<string>();
            var notifier = new NotificationService(service, (title, _, _) => notifications.Add(title));
            var live = AntigravityQuotaProvider.ParseQuotaResponses(null, """
                { "groups": [{ "buckets": [
                  { "bucketId": "gemini-weekly", "remainingFraction": 0.2 }
                ] }] }
                """, settings).Single(item => item.Id == "agy.gemini_weekly");

            notifier.ProcessQuotasUpdate([live]);
            notifier.ProcessQuotasUpdate([live with { IsCached = true, UsedPercent = 0 }]);
            notifier.ProcessQuotasUpdate([live with { SourceDetail = "Antigravity CLI", UsedPercent = 80 }]);
            Assert.Empty(notifications);

            notifier.ProcessQuotasUpdate([live with { SourceDetail = "Antigravity CLI", UsedPercent = 0 }]);
            Assert.Single(notifications);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(null)]
    public void ChangingCarbonWindowStartsANewNotificationBaseline(int? hours)
    {
        string path = Path.Combine(Path.GetTempPath(), $"quota_notification_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = service.Current.Clone();
            settings.PlaySoundOnNotification = false;
            service.Update(settings);
            var notifications = new List<string>();
            var notifier = new NotificationService(service, (title, _, _) => notifications.Add(title));
            TimeSpan? newWindow = hours is int duration ? TimeSpan.FromHours(duration) : null;

            notifier.ProcessQuotasUpdate([CarbonSample(80, TimeSpan.FromDays(7))]);
            notifier.ProcessQuotasUpdate([CarbonSample(0, newWindow)]);
            Assert.Empty(notifications);

            notifier.ProcessQuotasUpdate([CarbonSample(80, newWindow)]);
            notifier.ProcessQuotasUpdate([CarbonSample(0, newWindow)]);
            Assert.Equal("Cota Resetada: Gemini 4 Carbon", Assert.Single(notifications));
        }
        finally
        {
            File.Delete(path);
        }

        static QuotaItem CarbonSample(double usedPercent, TimeSpan? window) => new()
        {
            Id = "agy.gemini_carbon",
            Provider = QuotaProviderType.Antigravity,
            ProviderBadge = "CARBON",
            BadgeColorHex = "#3B82F6",
            Title = "Gemini 4 Carbon",
            UsedPercent = usedPercent,
            WindowDuration = window
        };
    }

    private static QuotaItem[] GetUnavailableIndicators(AppSettings settings)
    {
        var claude = ClaudeCodeQuotaProvider.SynthesizeQuotas(settings, null,
            new ClaudeCodeQuotaProvider.SessionDetails(null, null, null), null, false);
        const string status = """
            { "userStatus": { "cascadeModelConfigData": { "clientModelConfigs": [
              { "label": "Gemini 4 Carbon", "disabled": true }
            ] } } }
            """;
        var carbon = AntigravityQuotaProvider.ParseQuotaResponses(status, null, settings);
        return [Assert.Single(claude, item => item.Id == "claude.fable"),
            Assert.Single(carbon, item => item.Id == "agy.gemini_carbon.unavailable")];
    }
}
