using System;
using System.Text.Json;
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
    private const string FlashSummaryGroup = """
        { "displayName": "Gemini Models", "buckets": [
          { "bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.8 },
          { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.6 }
        ] }
        """;
    private const string CarbonSummaryGroup = """
        { "displayName": "Gemini 4 Carbon", "buckets": [
          { "bucketId": "gemini-carbon-wtu-weekly", "window": "weekly", "remainingFraction": 0.75,
            "resetTime": "2026-11-17T09:00:00Z" }
        ] }
        """;
    private const string ProStatus = """
        { "userStatus": {
          "email": "pro@example.invalid", "userTier": { "id": "g1-pro-tier", "name": "Google AI Pro" },
          "cascadeModelConfigData": { "clientModelConfigs": [
            { "modelId": "gemini-3.8-flash-high", "label": "Gemini 3.8 Flash (High)", "quotaInfo": {
              "remainingFraction": 0.02, "resetTime": "2026-11-13T23:49:53Z" } },
            { "modelId": "gemini-4-hydrogen-low", "label": "Gemini 4 Hydrogen (Low)", "quotaInfo": {
              "remainingFraction": 0.02, "resetTime": "2026-11-13T23:49:53Z" } },
            { "modelId": "gemini-4-hydrogen-xhigh", "label": "Gemini 4 Hydrogen (XHigh)", "quotaInfo": {
              "remainingFraction": 0.02, "resetTime": "2026-11-13T23:49:53Z" } },
            { "modelId": "gemini-4-argon-high", "label": "Gemini 4 Argon (High)", "quotaInfo": {
              "remainingFraction": 0.07, "resetTime": "2026-11-14T00:23:18Z" } },
            { "modelId": "gemini-pro-orchestrator-g1-pro", "label": "Orchestrator (Argon)", "quotaInfo": {
              "remainingFraction": 0.07, "resetTime": "2026-11-14T00:23:18Z" } },
            { "modelId": "gemini-4-carbon-disabled", "label": "Gemini 4 Carbon", "disabled": true }
          ] }
        } }
        """;
    private const string ProCli = """
        { "status": "SUCCESS", "num_turns": 0, "command": { "name": "usage", "data": { "groups": [
          { "name": "Gemini Models", "description": "Models within this group: Gemini Flash, Gemini Pro",
            "buckets": [
              { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.02, "reset_time": "2026-11-13T23:49:53Z" },
              { "id": "gemini-5h", "window": "5h", "remaining_fraction": 0.95, "reset_time": "2026-11-13T16:06:37Z" }
            ] },
          { "name": "Gemini 4 Argon", "buckets": [
            { "id": "gemini-argon-wtu-weekly", "window": "weekly",
              "remaining_fraction": 0.07, "reset_time": "2026-11-14T00:23:18Z" }
          ] }
        ] } } }
        """;
    private const string ProCatalog = """
        Fetching available models...
        gemini-3.8-flash-high	Gemini 3.8 Flash (High)
        gemini-4-hydrogen-low	Gemini 4 Hydrogen (Low)
        gemini-4-hydrogen-xhigh	Gemini 4 Hydrogen (XHigh)
        gemini-4-argon-high	Gemini 4 Argon (High)
        gemini-pro-orchestrator-g1-pro	Orchestrator (Argon)
        """;

    [Fact]
    public void ProLocalAndCliReadTheSameGroupsAndWindows()
    {
        using var cli = JsonDocument.Parse(ProCli);
        string summary = cli.RootElement.GetProperty("command").GetProperty("data").GetRawText()
            .Replace("\"name\"", "\"displayName\"").Replace("\"id\"", "\"bucketId\"")
            .Replace("remaining_fraction", "remainingFraction").Replace("reset_time", "resetTime");
        var settings = new AppSettings { GroupAntigravityEffortLevels = false };
        var local = AntigravityQuotaProvider.ParseQuotaResponses(ProStatus, summary, settings);
        var fallback = AntigravityQuotaProvider.ParseCliQuotaResponse(ProCli, settings, ProCatalog);

        foreach (var id in new[] { "agy.gemini_weekly", "agy.gemini_session", "agy.gemini_argon" })
        {
            var ide = Assert.Single(local, item => item.Id == id);
            var agy = Assert.Single(fallback, item => item.Id == id);
            Assert.Equal(agy.UsedPercent, ide.UsedPercent, 8);
            Assert.Equal(agy.ResetTime, ide.ResetTime);
            Assert.Equal(agy.WindowDuration, ide.WindowDuration);
        }
        foreach (var items in new[] { local, fallback })
        {
            var weekly = Assert.Single(items, item => item.Id == "agy.gemini_weekly");
            Assert.Equal(new[] { "Gemini 3.8 Flash", "Gemini 4 Hydrogen" }, weekly.AssociatedModels);
            var carbon = Assert.Single(items, item => item.Id == "agy.gemini_carbon.unavailable");
            Assert.Equal("Indisponível nesta conta", carbon.UnavailableMessage);
        }
        Assert.Equal(new[] { "Orchestrator (Argon)" }, Assert.Single(local, item => item.Id == "agy.gemini_argon").AssociatedModels);
    }

    [Fact]
    public void ExplicitSharedMembershipListsAllFourModelsAndHonorsVisibility()
    {
        const string summary = """
            { "groups": [{ "displayName": "Gemini Models",
              "models": ["gemini-3.8-flash", "gemini-4-hydrogen", "gemini-4-carbon", "gemini-4-argon"],
              "buckets": [{ "window": "weekly", "remainingFraction": 0.6 }] }] }
            """;
        var settings = new AppSettings();
        var weekly = Assert.Single(AntigravityQuotaProvider.ParseQuotaResponses(null, summary, settings));
        Assert.Equal(new[] { "Gemini 3.8 Flash", "Gemini 4 Hydrogen", "Gemini 4 Argon", "Gemini 4 Carbon" },
            weekly.AssociatedModels);
        settings.ShowAntigravityArgon = false;
        settings.ShowAntigravityCarbon = false;
        weekly = Assert.Single(AntigravityQuotaProvider.ParseQuotaResponses(null, summary, settings));
        Assert.Equal(new[] { "Gemini 3.8 Flash", "Gemini 4 Hydrogen" }, weekly.AssociatedModels);
    }

    [Fact]
    public void UltraCatalogAssociatesArgonAndRequiresMatchingReadingForCarbon()
    {
        const string status = """
            { "userStatus": { "userTier": { "name": "Google AI Ultra" },
              "cascadeModelConfigData": { "clientModelConfigs": [
                { "modelId": "gemini-4-argon-high", "quotaInfo": { "remainingFraction": 0.75 } },
                { "modelId": "gemini-4-carbon-high", "quotaInfo": {
                  "remainingFraction": 0.6, "resetTime": "2026-11-13T23:49:53Z" } }
              ] } } }
            """;
        const string summary = """
            { "groups": [{ "displayName": "Gemini Models", "buckets": [
              { "bucketId": "gemini-weekly", "remainingFraction": 0.6, "resetTime": "2026-11-13T23:49:53Z" }
            ] }] }
            """;
        var settings = new AppSettings();
        var items = AntigravityQuotaProvider.ParseQuotaResponses(status, summary, settings);
        Assert.Equal(new[] { "Gemini 4 Argon", "Gemini 4 Carbon" }, Assert.Single(items).AssociatedModels);

        items = AntigravityQuotaProvider.ParseQuotaResponses(
            status.Replace("23:49:53Z", "23:50:00Z"), summary, settings);
        Assert.Equal(new[] { "Gemini 4 Argon" }, items.Single(item => item.Id == "agy.gemini_weekly").AssociatedModels);
        Assert.Equal("Cota não reportada", items.Single(item => item.Id == "agy.gemini_carbon.unavailable").UnavailableMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(", \"remaining_fraction\": \"0\"")]
    [InlineData(", \"remaining_fraction\": -0.01")]
    [InlineData(", \"remaining_fraction\": 1.01")]
    public void InvalidOrAbsentGroupFractionDoesNotLookExhausted(string fraction)
    {
        string cli = $$"""
            { "command": { "data": { "groups": [{ "name": "Gemini Models", "buckets": [
              { "id": "gemini-weekly", "reset_time": "2026-11-13T23:49:53Z" {{fraction}} },
              { "id": "future-gemini-weekly", "window": "weekly", "remaining_fraction": 0.9 }
            ] }] } } }
            """;
        var weekly = Assert.Single(AntigravityQuotaProvider.ParseCliQuotaResponse(cli, SharedSettings()));
        Assert.Equal("agy.gemini_weekly", weekly.Id);
        Assert.False(weekly.IsAvailable);
        Assert.Equal("Cota não reportada", weekly.UnavailableMessage);
        Assert.Empty(weekly.AssociatedModels!);
    }

    [Fact]
    public async Task CliWinsOverModelBalancesAndCacheDoesNotCrossKnownAccounts()
    {
        await WithIsolatedCacheAsync(async _ =>
        {
            var endpoint = new AntigravityQuotaProvider.LanguageServerEndpoint(1, "http://127.0.0.1:10101", "test");
            string status = ProStatus;
            var cliCalls = new List<string>();
            AntigravityQuotaProvider Provider(bool cliWorks) => new(() => [endpoint],
                (_, _) => Task.FromResult<AntigravityQuotaProvider.QuotaResponses?>(new(status, null)),
                (_, args, _) =>
                {
                    cliCalls.Add(args[0]);
                    return Task.FromResult<string?>(!cliWorks ? null : args[0] == "models" ? ProCatalog : ProCli);
                });

            var settings = new AppSettings();
            var provider = Provider(true);
            var cli = await provider.FetchQuotasAsync(settings);
            Assert.Contains(cli, item => item.Id == "agy.gemini_weekly" && !item.IsCached);
            Assert.DoesNotContain(cli, item => item.Id.Contains(".reported"));
            await provider.FetchQuotasAsync(settings);
            Assert.Equal(new[] { "-p", "models" }, cliCalls);
            settings.CustomAgyCliPath = "alternate-agy.exe";
            await provider.FetchQuotasAsync(settings);
            Assert.Equal(new[] { "-p", "models", "-p", "models" }, cliCalls);

            string cliPath = settings.GetEffectiveAgyCliPath();
            var previous = AntigravityQuotaProvider.ParseCliQuotaResponse(ProCli, settings, ProCatalog);
            var account = AntigravityQuotaParser.ReadLocal(ProStatus, null).Catalog.Account;
            AntigravityQuotaProvider.SaveLastKnownQuotas(previous, account, cliPath);
            var offline = Provider(false);
            var cached = await offline.FetchQuotasAsync(settings);
            Assert.Contains(cached, item => item.Id == "agy.gemini_weekly" && item.IsCached);

            status = ProStatus.Replace("pro@example.invalid", "other@example.invalid");
            var reported = await offline.FetchQuotasAsync(settings);
            Assert.DoesNotContain(reported, item => item.IsCached || item.Id == "agy.gemini_weekly");
            var hydrogen = Assert.Single(reported, item => item.Id == "agy.gemini_hydrogen.reported");
            Assert.Equal("Saldo informado pelo modelo", hydrogen.Subtitle);
            Assert.Null(hydrogen.WindowDuration);
        });
    }

    [Fact]
    public void QuotaSummary_UsesReportedSharedWindowsInsteadOfModelBalances()
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

        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, summary, SharedSettings());

        var session = Assert.Single(result, x => x.Id == "agy.gemini_session");
        Assert.Equal(25, session.UsedPercent, 4);
        Assert.Equal("Gemini (5h)", session.Title);
        Assert.True(session.IsAvailable);

        var weekly = Assert.Single(result, x => x.Id == "agy.gemini_weekly");
        Assert.Equal("Gemini (7d)", weekly.Title);
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

        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, summary, SharedSettings());
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

        var result = AntigravityQuotaProvider.ParseQuotaResponses(null, summary, SharedSettings());

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

        var result = AntigravityQuotaProvider.ParseQuotaResponses(null, summary, SharedSettings());

        var weekly = Assert.Single(result);
        Assert.Equal("agy.gemini_weekly", weekly.Id);
        Assert.Equal(0, weekly.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792972800), weekly.ResetTime);
        Assert.True(weekly.IsAvailable);
    }

    [Fact]
    public void WithoutWeeklySummary_ShowsUnavailableWeeklyInsteadOfFabricatingUsage()
    {
        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, null, SharedSettings());

        Assert.Equal(2, result.Count);
        Assert.All(result, flash =>
        {
            Assert.StartsWith("agy.gemini_flash.reported.", flash.Id);
            Assert.Equal("Saldo informado pelo modelo", flash.Subtitle);
            Assert.True(flash.IsAvailable);
            Assert.Null(flash.WindowDuration);
        });
    }

    [Fact]
    public void WeeklyIndicatorCanBeDisabled()
    {
        var settings = SharedSettings();
        settings.ShowAntigravityGeminiWeekly = false;
        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, null, settings);

        Assert.All(result, item => Assert.StartsWith("agy.gemini_flash.reported", item.Id));
        Assert.DoesNotContain(result, x => x.Id == "agy.gemini_weekly");
    }

    [Fact]
    public void InvalidSummaryDoesNotHideAvailableSessionQuota()
    {
        var result = AntigravityQuotaProvider.ParseQuotaResponses(StatusJson, "{not json", SharedSettings());

        Assert.Contains(result, x => x.Id.StartsWith("agy.gemini_flash.reported.") && x.IsAvailable);
        Assert.DoesNotContain(result, x => x.Id == "agy.gemini_weekly");
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
            ShowAntigravityArgon = true,
            ShowAntigravityCarbon = false
        };

        var items = AntigravityQuotaProvider.ParseCliQuotaResponse(cliJson, settings);
        Assert.Equal(3, items.Count);

        var flash5h = Assert.Single(items, x => x.Id == "agy.gemini_session");
        Assert.Equal("Gemini (5h)", flash5h.Title);
        Assert.InRange(flash5h.UsedPercent, 11.9, 12.1);
        Assert.InRange(flash5h.RemainingPercent, 87.9, 88.1);
        Assert.Equal("Antigravity CLI (agy /usage)", flash5h.SourceDetail);
        Assert.True(flash5h.IsAvailable);

        var flash7d = Assert.Single(items, x => x.Id == "agy.gemini_weekly");
        Assert.Equal("Gemini (7d)", flash7d.Title);
        Assert.InRange(flash7d.UsedPercent, 47.9, 48.1);
        Assert.InRange(flash7d.RemainingPercent, 51.9, 52.1);
        Assert.Equal("Antigravity CLI (agy /usage)", flash7d.SourceDetail);
        Assert.True(flash7d.IsAvailable);

        var argon = Assert.Single(items, x => x.Id == "agy.gemini_argon");
        Assert.Equal("Gemini 4 Argon (7d)", argon.Title);
        Assert.Equal(100.0, argon.UsedPercent);
        Assert.Equal(0.0, argon.RemainingPercent);
        Assert.Equal("Antigravity CLI (agy /usage)", argon.SourceDetail);
        Assert.True(argon.IsAvailable);
    }

    [Fact]
    public void CarbonSummaryTakesPrecedenceAndKeepsSharedGeminiBucketsSeparate()
    {
        const string summary = """
        {
          "response": {
            "groups": [{
              "displayName": "Gemini Models",
              "buckets": [
                { "bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.8 },
                { "bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.7 },
                { "bucketId": "gemini-carbon-5h", "window": "5h", "remainingFraction": 0.1 },
                { "bucketId": "gemini-carbon-wtu-weekly", "window": "weekly",
                  "remaining": { "remainingFraction": 0.4, "resetTime": "2026-11-17T09:00:00Z" } }
              ]
            }]
          }
        }
        """;
        string status = CarbonStatusJson("""{ "label": "Gemini 4 Carbon", "quotaInfo": { "remainingFraction": 0.1 } }""");

        var items = AntigravityQuotaProvider.ParseQuotaResponses(status, summary, new AppSettings());

        var carbon = Assert.Single(items, item => item.Id == "agy.gemini_carbon");
        Assert.Equal(60, carbon.UsedPercent, 4);
        var session = Assert.Single(items, item => item.Id == "agy.gemini_carbon.session");
        Assert.Equal(90, session.UsedPercent, 4);
        Assert.Equal(TimeSpan.FromHours(5), session.WindowDuration);
        Assert.Equal(TimeSpan.FromDays(7), carbon.WindowDuration);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), carbon.ResetTime);
        Assert.Equal(20, Assert.Single(items, item => item.Id == "agy.gemini_weekly").UsedPercent, 4);
        Assert.Equal(30, Assert.Single(items, item => item.Id == "agy.gemini_session").UsedPercent, 4);
    }

    [Fact]
    public void CarbonSummaryWorksWithFlashWeeklyDisabledAndIncompleteStatus()
    {
        const string summary = """
        { "groups": [{
          "displayName": "Gemini 4 Carbon",
          "buckets": [{ "displayName": "Weekly Limit Remaining", "window": "weekly", "remainingFraction": 0 }]
        }] }
        """;
        var settings = new AppSettings { ShowAntigravityGeminiWeekly = false, ShowAntigravityArgon = false };

        var carbon = Assert.Single(AntigravityQuotaProvider.ParseQuotaResponses("{", summary, settings));

        Assert.Equal("agy.gemini_carbon", carbon.Id);
        Assert.True(carbon.IsAvailable);
        Assert.Equal(100, carbon.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), carbon.WindowDuration);
    }

    [Theory]
    [InlineData("""{ "remainingFraction": 0 }""")]
    [InlineData("""{ "resetTime": "2026-11-17T09:00:00Z" }""")]
    public void CarbonStatusAcceptsExplicitAndProtobufZeroWithoutInventingDuration(string quota)
    {
        string status = CarbonStatusJson($$"""{ "label": "Gemini 4 Carbon (High)", "quotaInfo": {{quota}} }""");
        var settings = new AppSettings { ShowAntigravityArgon = false };

        var carbon = Assert.Single(AntigravityQuotaProvider.ParseQuotaResponses(status, null, settings));

        Assert.Equal("agy.gemini_carbon.reported", carbon.Id);
        Assert.True(carbon.IsAvailable);
        Assert.Equal(100, carbon.UsedPercent);
        Assert.Null(carbon.WindowDuration);
        Assert.False(carbon.AllowWindowDurationInference);
    }

    [Theory]
    [InlineData("""{ "label": "Gemini 4 Carbon", "quotaInfo": {} }""")]
    [InlineData("""{ "label": "Gemini 4 Carbon", "disabled": true, "quotaInfo": { "remainingFraction": 0 } }""")]
    [InlineData("""{ "label": "Gemini 4 Carbon", "modelId": "gemini-4-carbon-disabled", "quotaInfo": { "resetTime": "2026-11-17T09:00:00Z" } }""")]
    [InlineData("""{ "label": "Gemini 4 Carbon", "quotaInfo": { "remainingFraction": "0", "resetTime": "2026-11-17T09:00:00Z" } }""")]
    [InlineData("""{ "label": "Gemini 4 Carbon", "quotaInfo": { "resetTime": "unknown" } }""")]
    public void InvalidOrDisabledCarbonStatusShowsOnlyTheUnavailableIndicator(string config)
    {
        var settings = new AppSettings { ShowAntigravityArgon = false };
        var carbon = Assert.Single(AntigravityQuotaProvider.ParseQuotaResponses(CarbonStatusJson(config), null, settings));
        Assert.Equal("agy.gemini_carbon.unavailable", carbon.Id);
        Assert.False(carbon.IsAvailable);
        Assert.Equal(config.Contains("disabled") ? "Indisponível nesta conta" : "Cota não reportada", carbon.UnavailableMessage);
        Assert.Null(carbon.ResetTime);

        settings.ShowAntigravityCarbon = false;
        Assert.Empty(AntigravityQuotaProvider.ParseQuotaResponses(CarbonStatusJson(config), null, settings));
    }

    [Fact]
    public void CarbonCliQuotaCoexistsWithArgonAndFlashAndHonorsVisibility()
    {
        const string json = """
        { "command": { "data": { "groups": [
          { "name": "Gemini Models", "buckets": [
            { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0.9 }
          ] },
          { "name": "Gemini 4 Argon", "buckets": [
            { "id": "gemini-argon-wtu-weekly", "window": "weekly", "remaining_fraction": 0.8 }
          ] },
          { "name": "Gemini 4 Carbon", "buckets": [
            { "name": "Weekly Limit Remaining", "window": "weekly", "remaining_fraction": 0,
              "reset_time": "2026-11-17T09:00:00Z" },
            { "name": "Unknown quota", "window": "weekly" }
          ] }
        ] } } }
        """;
        var settings = new AppSettings();

        var items = AntigravityQuotaProvider.ParseCliQuotaResponse(json, settings);
        Assert.Equal(3, items.Count);
        var carbon = Assert.Single(items, item => item.Id == "agy.gemini_carbon");
        Assert.True(carbon.IsAvailable);
        Assert.Equal(100, carbon.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), carbon.WindowDuration);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), carbon.ResetTime);
        Assert.Equal(20, Assert.Single(items, item => item.Id == "agy.gemini_argon").UsedPercent, 4);
        Assert.Equal(10, Assert.Single(items, item => item.Id == "agy.gemini_weekly").UsedPercent, 4);

        settings.ShowAntigravityCarbon = false;
        Assert.Equal(2, AntigravityQuotaProvider.ParseCliQuotaResponse(json, settings).Count);
        var fromStatus = AntigravityQuotaProvider.ParseQuotaResponses(CarbonStatusJson(
            """{ "label": "Gemini 4 Carbon (High)", "quotaInfo": { "remainingFraction": 0.75 } }"""), null, settings);
        Assert.DoesNotContain(fromStatus, item => item.Id.StartsWith("agy.gemini_carbon"));
    }

    [Fact]
    public async Task LocalGroupSummaryWinsOverStatusOnlyEndpointsAndCli()
    {
        await WithIsolatedCacheAsync(async _ =>
        {
            var first = new AntigravityQuotaProvider.LanguageServerEndpoint(1, "http://127.0.0.1:10101", "test");
            var second = new AntigravityQuotaProvider.LanguageServerEndpoint(2, "http://127.0.0.1:10102", "test");
            var attempts = new List<AntigravityQuotaProvider.LanguageServerEndpoint>();
            var provider = new AntigravityQuotaProvider(
                () => [first, second],
                (endpoint, _) =>
                {
                    attempts.Add(endpoint);
                    return Task.FromResult<AntigravityQuotaProvider.QuotaResponses?>(endpoint == first
                        ? new(StatusJson, null) : new(null, WrapSummary(FlashSummaryGroup, CarbonSummaryGroup)));
                },
                (_, _, _) => throw new InvalidOperationException("CLI should not run"));

            var settings = new AppSettings { ShowAntigravityArgon = false };
            var items = await provider.FetchQuotasAsync(settings);
            Assert.Equal(new[] { first, second }, attempts);
            Assert.Equal(25, Assert.Single(items, item => item.Id == "agy.gemini_carbon").UsedPercent);
            Assert.Contains(items, item => item.Id == "agy.gemini_weekly" && item.IsAvailable);

            attempts.Clear();
            await provider.FetchQuotasAsync(settings);
            Assert.Equal(second, Assert.Single(attempts));
        });
    }

    [Fact]
    public async Task LiveRefreshWhileCarbonIsHiddenPreservesItsQuotaForOfflineUse()
    {
        await WithIsolatedCacheAsync(async path =>
        {
            var endpoint = new AntigravityQuotaProvider.LanguageServerEndpoint(1, "http://127.0.0.1:10101", "test");
            bool online = true;
            var provider = new AntigravityQuotaProvider(
                () => [endpoint],
                (_, _) => Task.FromResult<AntigravityQuotaProvider.QuotaResponses?>(
                    online ? new(null, WrapSummary(FlashSummaryGroup, CarbonSummaryGroup)) : null));
            var settings = new AppSettings { EnableAntigravityCliFallback = false };
            var initial = Assert.Single(await provider.FetchQuotasAsync(settings), item => item.Id == "agy.gemini_carbon");
            Assert.True(initial.IsAvailable);

            AntigravityQuotaProvider.ClearCache();
            settings.ShowAntigravityCarbon = false;
            var hidden = await provider.FetchQuotasAsync(settings);
            Assert.DoesNotContain(hidden, item => item.Id == initial.Id);
            Assert.Contains(hidden, item => item.Id == "agy.gemini_weekly" && item.IsAvailable);
            var refreshedCache = JsonSerializer.Deserialize<AntigravityQuotaProvider.CachedQuotaRecord>(File.ReadAllText(path))!;
            Assert.Contains(refreshedCache.Items, item => item.Id == initial.Id);

            AntigravityQuotaProvider.ClearCache();
            online = false;
            settings.ShowAntigravityCarbon = true;
            var cached = Assert.Single(await provider.FetchQuotasAsync(settings), item => item.Id == initial.Id);
            Assert.True(cached.IsAvailable);
            Assert.Equal(initial.UsedPercent, cached.UsedPercent);
            Assert.Equal(initial.ResetTime, cached.ResetTime);
            Assert.Contains($"Última cota conhecida ({refreshedCache.CachedAt:HH:mm})", cached.Subtitle);
        });
    }

    private static AppSettings SharedSettings() => new()
    {
        ShowAntigravityArgon = false,
        ShowAntigravityCarbon = false
    };

    private static string WrapSummary(params string[] groups) =>
        $$"""{ "groups": [{{string.Join(",", groups)}}] }""";

    private static async Task WithIsolatedCacheAsync(Func<string, Task> verify)
    {
        string path = Path.Combine(Path.GetTempPath(), $"agy_quota_test_{Guid.NewGuid():N}.json");
        AntigravityQuotaProvider.CustomCacheFilePath = path;
        AntigravityQuotaProvider.ClearCache();
        try
        {
            await verify(path);
        }
        finally
        {
            AntigravityQuotaProvider.CustomCacheFilePath = string.Empty;
            AntigravityQuotaProvider.ClearCache();
            File.Delete(path);
        }
    }

    [Fact]
    public void CarbonCacheRestoresKnownQuotaAndDiscardsLegacyRows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"agy_carbon_test_{Guid.NewGuid():N}.json");
        AntigravityQuotaProvider.CustomCacheFilePath = path;
        AntigravityQuotaProvider.ClearCache();

        try
        {
            var settings = new AppSettings { ShowAntigravityArgon = false };
            string status = CarbonStatusJson("""
                { "label": "Gemini 4 Carbon", "quotaInfo": { "remainingFraction": 0.75, "resetTime": "2026-11-17T09:00:00Z" } }
                """);
            var carbon = Assert.Single(AntigravityQuotaProvider.ParseQuotaResponses(status, null, settings));
            var legacy = new[] { "agy.gemini_4_carbon", "agy.gemini_4_carbon.0" }
                .Select(id => new QuotaItem
                {
                    Id = id,
                    Provider = QuotaProviderType.Antigravity,
                    ProviderBadge = "AGY",
                    BadgeColorHex = "#3B82F6",
                    Title = "Gemini 4 Carbon",
                    UsedPercent = 100
                });

            File.WriteAllText(path, JsonSerializer.Serialize(new AntigravityQuotaProvider.CachedQuotaRecord(
                DateTimeOffset.Now, legacy.Append(carbon with { Id = "agy.gemini_carbon" }).ToList())));
            AntigravityQuotaProvider.ClearCache();

            var cached = Assert.Single(AntigravityQuotaProvider.GetLastKnownQuotas());
            Assert.Equal(carbon.Id, cached.Id);
            Assert.Equal(carbon.UsedPercent, cached.UsedPercent);
            Assert.Equal(carbon.ResetTime, cached.ResetTime);
            Assert.False(cached.AllowWindowDurationInference);
            Assert.Contains("Última cota conhecida", cached.Subtitle);

        }
        finally
        {
            AntigravityQuotaProvider.CustomCacheFilePath = string.Empty;
            AntigravityQuotaProvider.ClearCache();
            File.Delete(path);
        }
    }

    private static string CarbonStatusJson(string config) => $$"""
        { "userStatus": { "cascadeModelConfigData": { "clientModelConfigs": [{{config}}] } } }
        """;

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

            File.WriteAllText(tempCache, JsonSerializer.Serialize(new AntigravityQuotaProvider.CachedQuotaRecord(
                DateTimeOffset.Now, initialItems)));

            // Clear in-memory cache to force disk load
            AntigravityQuotaProvider.ClearCache();

            var restored = AntigravityQuotaProvider.GetLastKnownQuotas();
            Assert.Equal(3, restored.Count);

            var flash = Assert.Single(restored, x => x.Id == "agy.gemini_flash.reported");
            Assert.True(flash.IsAvailable);
            Assert.Null(flash.WindowDuration);
            Assert.Equal(15.0, flash.UsedPercent);
            Assert.Contains("Última cota conhecida", flash.Subtitle);

            var weekly = Assert.Single(restored, x => x.Id == "agy.gemini_weekly");
            Assert.Equal("Gemini (7d)", weekly.Title);
            Assert.True(weekly.IsAvailable);
            Assert.Equal(45.0, weekly.UsedPercent);
            Assert.Contains("Última cota conhecida", weekly.Subtitle);

            var argon = Assert.Single(restored, x => x.Id == "agy.gemini_argon.reported");
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
