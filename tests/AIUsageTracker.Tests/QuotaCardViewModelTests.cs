using AIUsageTracker.Models;
using AIUsageTracker.ViewModels;

namespace AIUsageTracker.Tests;

public class QuotaCardViewModelTests
{
    [Fact]
    public void GroupsOnlyTheMatchingSharedWindows()
    {
        var bars = new[]
        {
            Bar("codex.primary", QuotaProviderType.Codex, TimeSpan.FromDays(7)),
            Bar("codex.secondary", QuotaProviderType.Codex, TimeSpan.FromHours(5)),
            Bar("claude.session", QuotaProviderType.ClaudeCode),
            Bar("claude.weekly", QuotaProviderType.ClaudeCode),
            Bar("claude.fable", QuotaProviderType.ClaudeCode, TimeSpan.FromDays(7)),
            Bar("agy.gemini_flash.0", QuotaProviderType.Antigravity),
            Bar("agy.gemini_flash.1", QuotaProviderType.Antigravity),
            Bar("agy.gemini_weekly", QuotaProviderType.Antigravity),
            Bar("agy.gemini_carbon", QuotaProviderType.Antigravity, TimeSpan.FromHours(5))
        };

        var cards = QuotaCardViewModel.Group(bars, new HashSet<string>());

        Assert.Equal(new[] { "codex.primary", "claude.weekly", "claude.fable", "agy.gemini_weekly", "agy.gemini_carbon" },
            cards.Select(card => card.Quota.Id));
        Assert.Same(bars[1], Assert.Single(cards[0].SessionQuotas));
        Assert.Same(bars[2], Assert.Single(cards[1].SessionQuotas));
        Assert.Empty(cards[2].SessionQuotas);
        Assert.Equal(new[] { bars[5], bars[6] }, cards[3].SessionQuotas);
        Assert.Empty(cards[4].SessionQuotas);
    }

    [Fact]
    public void PreservesStandaloneSessionAndRestoresCollapseWhenWeeklyCardReturns()
    {
        var collapsed = new HashSet<string>();
        var session = Bar("agy.gemini_session", QuotaProviderType.Antigravity);
        var weekly = Bar("agy.gemini_weekly", QuotaProviderType.Antigravity);

        var card = Assert.Single(QuotaCardViewModel.Group([session, weekly], collapsed));
        Assert.True(card.IsSessionExpanded);
        card.IsSessionExpanded = false;

        var standalone = Assert.Single(QuotaCardViewModel.Group([session], collapsed));
        Assert.Same(session, standalone.Quota);
        Assert.False(standalone.HasSessionQuotas);

        var restored = Assert.Single(QuotaCardViewModel.Group([session, weekly], collapsed));
        Assert.False(restored.IsSessionExpanded);
        restored.IsSessionExpanded = true;
        Assert.True(Assert.Single(QuotaCardViewModel.Group([session, weekly], collapsed)).IsSessionExpanded);
    }

    [Fact]
    public void SessionPreviewUsesTheMostConsumedAvailableSession()
    {
        const QuotaProviderType provider = QuotaProviderType.Antigravity;
        var weekly = Bar("agy.gemini_weekly", provider);
        var low = Bar("agy.gemini_flash.0", provider, usedPercent: 15);
        var high = Bar("agy.gemini_flash.1", provider, usedPercent: 80);
        var unavailable = Bar("agy.gemini_flash.2", provider, usedPercent: 100, available: false);

        Assert.Same(high, Assert.Single(QuotaCardViewModel.Group([low, high, unavailable, weekly], new HashSet<string>()))
            .SessionPreview);
        Assert.Null(Assert.Single(QuotaCardViewModel.Group([unavailable, weekly], new HashSet<string>())).SessionPreview);
    }

    private static QuotaBarViewModel Bar(string id, QuotaProviderType provider, TimeSpan? duration = null,
        double usedPercent = 0, bool available = true) =>
        new(new QuotaItem
        {
            Id = id,
            Provider = provider,
            ProviderBadge = "TEST",
            BadgeColorHex = "#3B82F6",
            Title = id,
            WindowDuration = duration,
            UsedPercent = usedPercent,
            IsAvailable = available
        }, new AppSettings());
}
