using System;
using AIUsageTracker.Models;
using AIUsageTracker.Services.Providers;
using Xunit;

namespace AIUsageTracker.Tests;

public class AntigravityQuotaProviderTests
{
    private const string StatusJson = """
        {
          "userStatus": {
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                { "label": "Gemini 3.8 Flash (Low)", "quotaInfo": { "remainingFraction": 0.80, "resetTime": "2026-10-18T15:00:00Z" } },
                { "label": "Gemini 3.8 Flash (High)", "quotaInfo": { "remainingFraction": 0.75, "resetTime": "2026-10-18T15:00:00Z" } }
              ]
            }
          }
        }
        """;

    [Fact]
    public void QuotaSummary_AddsSharedGeminiWeeklyAlongsideFlashSession()
    {
        const string summary = """
            {
              "response": {
                "groups": [
                  {
                    "displayName": "Gemini Models",
                    "buckets": [
                      { "bucketId": "gemini-5h", "displayName": "Five Hour Limit", "window": "5h", "remainingFraction": 0.75 },
                      { "bucketId": "gemini-weekly", "displayName": "Weekly Limit", "window": "weekly", "remainingFraction": 0.42, "resetTime": "2026-10-24T15:00:00Z" }
                    ]
                  },
                  {
                    "displayName": "Claude and GPT models",
                    "buckets": [
                      { "bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 0.01 }
                    ]
                  }
                ]
              }
            }
            """;

        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, summary, new AppSettings());

        var session = Assert.Single(result, x => x.Id == "agy.gemini_flash");
        Assert.Equal(25, session.UsedPercent, 4);
        Assert.Equal("Gemini 3.8 Flash (5h)", session.Title);
        Assert.True(session.IsAvailable);

        var weekly = Assert.Single(result, x => x.Id == "agy.gemini_weekly");
        Assert.Equal("Gemini Models (7d)", weekly.Title);
        Assert.Equal(58, weekly.UsedPercent, 4);
        Assert.Equal(DateTimeOffset.Parse("2026-10-24T15:00:00Z"), weekly.ResetTime);
        Assert.True(weekly.IsAvailable);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void QuotaSummary_ReadsNestedRemainingAndIgnoresUnknownUsage()
    {
        const string summary = """
            {
              "response": {
                "groups": [{
                  "displayName": "Gemini Models",
                  "buckets": [
                    { "bucketId": "gemini-weekly", "window": "weekly", "remaining": { "remainingFraction": 0.93, "resetTime": "2026-10-25T00:00:00Z" }, "description": "Resets Sunday" },
                    { "bucketId": "gemini-weekly-other", "window": "weekly", "remaining": {} },
                    { "bucketId": "gemini-5h", "window": "5h", "remaining": { "remainingFraction": 0.20 } }
                  ]
                }]
              }
            }
            """;

        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, summary, new AppSettings());
        var weekly = Assert.Single(result, x => x.Id == "agy.gemini_weekly");
        Assert.Equal(7, weekly.UsedPercent, 4);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:00:00Z"), weekly.ResetTime);
        Assert.Contains("Resets Sunday", weekly.Subtitle);
    }

    [Fact]
    public void SummaryOnlyServer_ProvidesSessionAndWeeklyFromSharedGeminiBuckets()
    {
        const string summary = """
            {
              "response": {
                "groups": [{
                  "displayName": "Gemini Models",
                  "buckets": [
                    { "bucketId": "gemini-5h", "window": "5h", "remaining": { "remainingFraction": 0.75 } },
                    { "bucketId": "gemini-weekly", "window": "weekly", "remaining": { "remainingFraction": 0.90 } }
                  ]
                }]
              }
            }
            """;

        var result = AntigravityQuotaProvider.ParseQuotaResponses(null, summary, new AppSettings());

        Assert.Equal(25, Assert.Single(result, x => x.Id == "agy.gemini_session").UsedPercent, 4);
        Assert.Equal(10, Assert.Single(result, x => x.Id == "agy.gemini_weekly").UsedPercent, 4);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void WeeklyOnlyPlan_DoesNotInventFiveHourQuota()
    {
        const string summary = """
            {
              "response": {
                "groups": [{
                  "displayName": "Gemini Models",
                  "buckets": [
                    { "bucketId": "gemini-weekly", "displayName": "Weekly Limit", "remainingFraction": 1.0, "resetTime": 1792972800 }
                  ]
                }]
              }
            }
            """;

        var result = AntigravityQuotaProvider.ParseQuotaResponses(null, summary, new AppSettings());

        var weekly = Assert.Single(result);
        Assert.Equal("agy.gemini_weekly", weekly.Id);
        Assert.Equal(0, weekly.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792972800), weekly.ResetTime);
        Assert.True(weekly.IsAvailable);
    }

    [Fact]
    public void WithoutWeeklySummary_ShowsUnavailableWeeklyInsteadOfFabricatingUsage()
    {
        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, null, new AppSettings());

        var flash = Assert.Single(result, x => x.Id == "agy.gemini_flash");
        Assert.True(flash.IsAvailable);
        var weekly = Assert.Single(result, x => x.Id == "agy.gemini_weekly");
        Assert.False(weekly.IsAvailable);
        Assert.Contains("RetrieveUserQuotaSummary", weekly.SourceDetail);
    }

    [Fact]
    public void WeeklyIndicatorCanBeDisabled()
    {
        var settings = new AppSettings { ShowAntigravityGeminiWeekly = false };
        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, null, settings);

        Assert.Single(result);
        Assert.DoesNotContain(result, x => x.Id == "agy.gemini_weekly");
    }

    [Fact]
    public void InvalidSummaryDoesNotHideAvailableSessionQuota()
    {
        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, "{not json", new AppSettings());

        Assert.Contains(result, x => x.Id == "agy.gemini_flash" && x.IsAvailable);
        Assert.Contains(result, x => x.Id == "agy.gemini_weekly" && !x.IsAvailable);
    }
}
