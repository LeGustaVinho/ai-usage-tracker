using AIUsageTracker.Models;
using AIUsageTracker.Services.Providers;

namespace AIUsageTracker.Tests;

public class ClaudeFableQuotaTests
{
    private static readonly ClaudeCodeQuotaProvider.SessionDetails EmptySession = new(null, null, null);

    [Fact]
    public void ReadsWeeklyFablePercentAndResetFromItsOwnScope()
    {
        const string payload = """
        {
          "seven_day": { "utilization": 28 },
          "limits": [
            { "kind": "weekly_all", "scope": { "model": { "display_name": "Fable" } }, "percent": 50 },
            { "kind": "weekly_scoped", "scope": { "model": { "display_name": "Sonnet" } }, "percent": 65 },
            { "kind": "weekly_scoped", "scope": { "model": { "id": null, "display_name": "Fable" } },
              "percent": 32.5, "resets_at": "2026-11-16T15:30:00Z", "is_active": false }
          ],
          "weekly_scoped_shares": [{ "used_percent_of_weekly": 16.25, "allowance_percent_of_weekly": 50 }]
        }
        """;

        var items = ClaudeCodeQuotaProvider.SynthesizeQuotas(new AppSettings(), null, EmptySession, payload, false);

        var fable = Assert.Single(items, item => item.Id == "claude.fable");
        Assert.True(fable.IsAvailable);
        Assert.Equal("Claude Fable (7d)", fable.Title);
        Assert.Equal(32.5, fable.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 15, 30, 0, TimeSpan.Zero), fable.ResetTime);
        Assert.Equal(TimeSpan.FromDays(7), fable.WindowDuration);
        Assert.Equal(28, Assert.Single(items, item => item.Id == "claude.weekly").UsedPercent);
    }

    [Fact]
    public void ShowsZeroConsumptionFromCacheAndHonorsVisibility()
    {
        const string payload = """
        { "limits": [{ "kind": "weekly_scoped", "scope": { "model": { "id": "claude-fable-5-2" } },
                       "percent": 0, "is_active": false }] }
        """;
        var settings = new AppSettings();
        var items = ClaudeCodeQuotaProvider.SynthesizeQuotas(settings, null, EmptySession, payload, false, usingCachedApi: true);

        var fable = Assert.Single(items, item => item.Id == "claude.fable");
        Assert.True(fable.IsAvailable);
        Assert.Equal(0, fable.UsedPercent);
        Assert.Null(fable.ResetTime);
        Assert.Equal("Última cota conhecida", fable.Subtitle);

        settings.ShowClaudeFable = false;
        var hidden = ClaudeCodeQuotaProvider.SynthesizeQuotas(settings, null, EmptySession, payload, false, usingCachedApi: true);
        Assert.DoesNotContain(hidden, item => item.Id == "claude.fable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"limits":null}""")]
    [InlineData("""{"limits":[null, {"kind":"weekly_scoped","scope":{}}]}""")]
    [InlineData("""{"limits":[{"kind":"weekly_scoped","scope":{"model":{"display_name":"Fable"}},"percent":"0"}]}""")]
    [InlineData("""{"limits":[{"kind":"weekly_scoped","scope":{"model":{"display_name":"Fable"}},"percent":101}]}""")]
    public void MissingOrInvalidFableQuotaShowsUnavailable(string? payload)
    {
        var items = ClaudeCodeQuotaProvider.SynthesizeQuotas(new AppSettings(), null, EmptySession, payload, false);

        var fable = Assert.Single(items, item => item.Id == "claude.fable");
        Assert.False(fable.IsAvailable);
        Assert.Equal("Sem cota disponível", fable.UnavailableMessage);
        Assert.Null(fable.ResetTime);
    }
}
