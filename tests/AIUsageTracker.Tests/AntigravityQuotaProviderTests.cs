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
        Assert.Equal("Gemini 3.8 Flash (7d)", weekly.Title);
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

    [Fact]
    public void AntigravityQuotaProvider_ParsesCliQuotaResponseCorrectly()
    {
        const string cliJson = """
            {
              "conversation_id": "",
              "status": "SUCCESS",
              "command": {
                "name": "usage",
                "data": {
                  "groups": [
                    {
                      "name": "Gemini Models",
                      "description": "Models within this group: Gemini Flash, Gemini Pro",
                      "buckets": [
                        {
                          "id": "gemini-weekly",
                          "name": "Weekly Limit Remaining",
                          "description": "Weekly refresh in 5 days",
                          "window": "weekly",
                          "remaining_fraction": 0.52,
                          "reset_time": "2026-10-24T03:24:10Z"
                        },
                        {
                          "id": "gemini-5h",
                          "name": "Five Hour Limit Remaining",
                          "description": "5h refresh in 3 hours",
                          "window": "5h",
                          "remaining_fraction": 0.88,
                          "reset_time": "2026-10-18T15:33:14Z"
                        }
                      ]
                    },
                    {
                      "name": "Gemini 4 Argon",
                      "buckets": [
                        {
                          "id": "gemini-argon-wtu-weekly",
                          "name": "Gemini 4 Argon Weekly Limit",
                          "window": "weekly",
                          "remaining_fraction": 0.0,
                          "reset_time": "2026-10-24T01:43:58Z"
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """;

        var settings = new AppSettings
        {
            ShowAntigravityFlash = true,
            ShowAntigravityGeminiWeekly = true,
            ShowAntigravityArgon = true
        };

        var items = AntigravityQuotaProvider.ParseCliQuotaResponse(cliJson, settings);
        Assert.Equal(3, items.Count);

        var flash5h = Assert.Single(items, x => x.Id == "agy.gemini_flash");
        Assert.Equal("Gemini 3.8 Flash (5h)", flash5h.Title);
        Assert.InRange(flash5h.UsedPercent, 11.9, 12.1);
        Assert.InRange(flash5h.RemainingPercent, 87.9, 88.1);
        Assert.Equal("Antigravity CLI (agy /quota)", flash5h.SourceDetail);
        Assert.True(flash5h.IsAvailable);

        var flash7d = Assert.Single(items, x => x.Id == "agy.gemini_weekly");
        Assert.Equal("Gemini 3.8 Flash (7d)", flash7d.Title);
        Assert.InRange(flash7d.UsedPercent, 47.9, 48.1);
        Assert.InRange(flash7d.RemainingPercent, 51.9, 52.1);
        Assert.Equal("Antigravity CLI (agy /quota)", flash7d.SourceDetail);
        Assert.True(flash7d.IsAvailable);

        var argon = Assert.Single(items, x => x.Id == "agy.gemini_argon");
        Assert.Equal("Gemini 4 Argon", argon.Title);
        Assert.Equal(100.0, argon.UsedPercent);
        Assert.Equal(0.0, argon.RemainingPercent);
        Assert.Equal("Antigravity CLI (agy /quota)", argon.SourceDetail);
        Assert.True(argon.IsAvailable);
    }

    [Fact]
    public void AntigravityQuotaProvider_SavesAndRestoresLastKnownQuotasWhenOffline()
    {
        string tempCache = Path.Combine(Path.GetTempPath(), $"agy_quota_test_{Guid.NewGuid():N}.json");
        AntigravityQuotaProvider.CustomCacheFilePath = tempCache;
        AntigravityQuotaProvider.ClearCache();

        try
        {
            var initialItems = new List<QuotaItem>
            {
                new()
                {
                    Id = "agy.gemini_flash",
                    Provider = QuotaProviderType.Antigravity,
                    ProviderBadge = "GEMINI",
                    BadgeColorHex = "#06B6D4",
                    Title = "Gemini 3.8 Flash (5h)",
                    UsedPercent = 15.0,
                    ResetTime = DateTimeOffset.UtcNow.AddHours(3),
                    Subtitle = "Cota de 5 horas Gemini 3.8 Flash",
                    SourceDetail = "Antigravity language_server Connect-RPC",
                    IsAvailable = true,
                    SortOrder = 31
                },
                new()
                {
                    Id = "agy.gemini_weekly",
                    Provider = QuotaProviderType.Antigravity,
                    ProviderBadge = "GEMINI",
                    BadgeColorHex = "#06B6D4",
                    Title = "Gemini 3.8 Flash (7d)",
                    UsedPercent = 45.0,
                    ResetTime = DateTimeOffset.UtcNow.AddDays(5),
                    Subtitle = "Cota semanal Gemini 3.8 Flash",
                    SourceDetail = "Antigravity RetrieveUserQuotaSummary",
                    IsAvailable = true,
                    SortOrder = 32
                },
                new()
                {
                    Id = "agy.gemini_argon",
                    Provider = QuotaProviderType.Antigravity,
                    ProviderBadge = "ARGON",
                    BadgeColorHex = "#3B82F6",
                    Title = "Gemini 4 Argon",
                    UsedPercent = 100.0,
                    ResetTime = DateTimeOffset.UtcNow.AddDays(5),
                    Subtitle = "Cota compartilhada",
                    SourceDetail = "Antigravity language_server Connect-RPC",
                    IsAvailable = true,
                    SortOrder = 30
                }
            };

            AntigravityQuotaProvider.SaveLastKnownQuotas(initialItems);

            // Clear in-memory cache to force disk load
            AntigravityQuotaProvider.ClearCache();

            var settings = new AppSettings
            {
                ShowAntigravityFlash = true,
                ShowAntigravityGeminiWeekly = true,
                ShowAntigravityArgon = true
            };

            var restored = AntigravityQuotaProvider.GetLastKnownQuotas(settings);
            Assert.NotNull(restored);
            Assert.Equal(3, restored.Count);

            var flash = Assert.Single(restored, x => x.Id == "agy.gemini_flash");
            Assert.True(flash.IsAvailable);
            Assert.Equal(15.0, flash.UsedPercent);
            Assert.Contains("Última cota conhecida", flash.Subtitle);

            var weekly = Assert.Single(restored, x => x.Id == "agy.gemini_weekly");
            Assert.True(weekly.IsAvailable);
            Assert.Equal(45.0, weekly.UsedPercent);
            Assert.Contains("Última cota conhecida", weekly.Subtitle);

            var argon = Assert.Single(restored, x => x.Id == "agy.gemini_argon");
            Assert.True(argon.IsAvailable);
            Assert.Equal(100.0, argon.UsedPercent);
            Assert.Contains("Última cota conhecida", argon.Subtitle);
        }
        finally
        {
            AntigravityQuotaProvider.CustomCacheFilePath = string.Empty;
            AntigravityQuotaProvider.ClearCache();
            if (File.Exists(tempCache))
            {
                File.Delete(tempCache);
            }
        }
    }
}
