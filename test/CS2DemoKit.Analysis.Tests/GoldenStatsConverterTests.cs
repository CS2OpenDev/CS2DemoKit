#region

using CS2DemoKit.Analysis.GoldenStats;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pure-function tests for the GoldenStats converters. Synthetic inputs,
///     no demo file required — these run in milliseconds and provide the
///     coverage that the demo-dependent integration tests can't.
/// </summary>
[Category("Unit")]
public class GoldenStatsConverterTests
{
    /// <summary>Golden stats_round trips through json.</summary>
    [Test]
    public async Task GoldenStats_RoundTripsThroughJson()
    {
        // Build → serialize → deserialize → equal-by-value.
        PlayerStatsInput[] input = new[]
        {
            new PlayerStatsInput("Alice", 3, 0,
                new Dictionary<string, object?>
                {
                    ["TotalK"] = 20,
                    ["ADR"] = 80.5
                }),
            new PlayerStatsInput("Bob", 2, 5,
                new Dictionary<string, object?>
                {
                    ["TotalK"] = 15,
                    ["ADR"] = 70.0
                })
        };

        GoldenStatsDocument original = OursGoldenStatsConverter.Convert(
            "match.dem", "deadbeef", null, input,
            generatedAt: "2026-05-21T00:00:00Z");

        string json = GoldenStatsSerializer.Serialize(original);
        GoldenStatsDocument round = GoldenStatsSerializer.Deserialize(json);

        await Assert.That(round.DemoFileName).IsEqualTo("match.dem");
        await Assert.That(round.DemoSha256).IsEqualTo("deadbeef");
        await Assert.That(round.Provider).IsEqualTo("ours");
        await Assert.That(round.GeneratedAt).IsEqualTo("2026-05-21T00:00:00Z");
        await Assert.That(round.Players.Count).IsEqualTo(2);
        await Assert.That(round.Players["Alice"].Team).IsEqualTo(3);
        await Assert.That(round.Players["Alice"].Stats[CanonicalStatNames.Kills]).IsEqualTo(20.0);
        await Assert.That(round.Players["Bob"].Stats[CanonicalStatNames.Adr]).IsEqualTo(70.0);
    }

    /// <summary>Leetify converter_empty player stats_produces empty players dict.</summary>
    [Test]
    public async Task LeetifyConverter_EmptyPlayerStats_ProducesEmptyPlayersDict()
    {
        const string LeetifyJson = """
                                   { "mapName": "de_inferno", "playerStats": [] }
                                   """;

        GoldenStatsDocument g = LeetifyGoldenStatsConverter.Convert(LeetifyJson, "x.dem");

        await Assert.That(g.Players.Count).IsEqualTo(0);
        await Assert.That(g.Match.Map).IsEqualTo("de_inferno");
    }

    /// <summary>Leetify converter_handles missing fields as null stats.</summary>
    [Test]
    public async Task LeetifyConverter_HandlesMissingFieldsAsNullStats()
    {
        // A real Leetify response can omit fields when data is incomplete
        // (early-disconnect player, FACEIT vs MM differences, etc.). Missing
        // fields should become null in the output, not throw or default to 0.
        const string LeetifyJson = """
                                   {
                                     "playerStats": [
                                       { "name": "p1", "initialTeamNumber": 2, "totalKills": 10 }
                                     ]
                                   }
                                   """;

        GoldenStatsDocument g = LeetifyGoldenStatsConverter.Convert(LeetifyJson, "x.dem");
        Dictionary<string, double?> s = g.Players["p1"].Stats;

        await Assert.That(s[CanonicalStatNames.Kills]).IsEqualTo(10.0);
        await Assert.That(s[CanonicalStatNames.Adr]).IsNull();
        await Assert.That(s[CanonicalStatNames.HltvRating]).IsNull();
        await Assert.That(s[CanonicalStatNames.KastPct]).IsNull();
    }

    // ── LeetifyGoldenStatsConverter ───────────────────────────────────────────
    /// <summary>Leetify converter_maps leetify keys to canonical.</summary>
    [Test]
    public async Task LeetifyConverter_MapsLeetifyKeysToCanonical()
    {
        // Minimal Leetify-shaped fixture. Every mapping the converter knows
        // about should land in the right canonical key.
        const string LeetifyJson = """
                                   {
                                     "mapName": "de_mirage",
                                     "playerStats": [
                                       {
                                         "name": "ZywOo",
                                         "steam64Id": "76561198029679050",
                                         "initialTeamNumber": 3,
                                         "totalKills": 24,
                                         "totalDeaths": 17,
                                         "totalAssists": 5,
                                         "totalDamage": 1860,
                                         "dpr": 89.2,
                                         "kdRatio": 1.41,
                                         "hltvRating": 1.32,
                                         "kast": 0.785,
                                         "hsp": 0.52,
                                         "multi2k": 3,
                                         "multi3k": 1,
                                         "multi4k": 0,
                                         "multi5k": 0,
                                         "roundsSurvived": 12,
                                         "tradeKillsSucceeded": 3,
                                         "ctRoundsWon": 7,
                                         "tRoundsWon": 5,
                                         "shotsHitFoe": 142,
                                         "shotsFired": 312
                                       }
                                     ]
                                   }
                                   """;

        GoldenStatsDocument g = LeetifyGoldenStatsConverter.Convert(LeetifyJson, "match.dem");

        await Assert.That(g.Provider).IsEqualTo("leetify");
        await Assert.That(g.Match.Map).IsEqualTo("de_mirage");
        await Assert.That(g.Players).ContainsKey("ZywOo");

        PlayerStatsRecord p = g.Players["ZywOo"];
        await Assert.That(p.Team).IsEqualTo(3);
        await Assert.That(p.SteamId).IsEqualTo(76561198029679050UL);
        // Float comparisons use Within(epsilon) — the percent scaling
        // (kast * 100, hsp * 100) produces values that may not be bit-exact in
        // IEEE 754 even when they look round. Counts are integer-valued doubles
        // so exact equality is safe.
        await Assert.That(p.Stats[CanonicalStatNames.Kills]).IsEqualTo(24.0);
        await Assert.That(p.Stats[CanonicalStatNames.Multi3K]).IsEqualTo(1.0);
        await Assert.That(p.Stats[CanonicalStatNames.RoundsSurvived]).IsEqualTo(12.0);
        await Assert.That(p.Stats[CanonicalStatNames.CtRoundsWon]).IsEqualTo(7.0);
        await Assert.That(p.Stats[CanonicalStatNames.ShotsFired]).IsEqualTo(312.0);

        await Assert.That(p.Stats[CanonicalStatNames.Adr]!.Value).IsEqualTo(89.2).Within(0.0001);
        await Assert.That(p.Stats[CanonicalStatNames.HltvRating]!.Value).IsEqualTo(1.32).Within(0.0001);
    }

    /// <summary>Leetify converter_scales percentage stats by100.</summary>
    [Test]
    public async Task LeetifyConverter_ScalesPercentageStatsBy100()
    {
        // Leetify emits kast and hsp as 0–1 ratios. Golden format uses 0–100.
        const string LeetifyJson = """
                                   {
                                     "playerStats": [
                                       { "name": "p1", "initialTeamNumber": 2, "kast": 0.7368, "hsp": 0.4375 }
                                     ]
                                   }
                                   """;

        GoldenStatsDocument g = LeetifyGoldenStatsConverter.Convert(LeetifyJson, "x.dem");

        await Assert.That(g.Players["p1"].Stats[CanonicalStatNames.KastPct]!.Value)
            .IsEqualTo(73.68).Within(0.0001);
        await Assert.That(g.Players["p1"].Stats[CanonicalStatNames.HsPct]!.Value)
            .IsEqualTo(43.75).Within(0.0001);
    }

    /// <summary>Leetify converter_skips players without name.</summary>
    [Test]
    public async Task LeetifyConverter_SkipsPlayersWithoutName()
    {
        const string LeetifyJson = """
                                   {
                                     "playerStats": [
                                       { "initialTeamNumber": 2, "totalKills": 10 },
                                       { "name": "valid", "initialTeamNumber": 3, "totalKills": 20 }
                                     ]
                                   }
                                   """;

        GoldenStatsDocument g = LeetifyGoldenStatsConverter.Convert(LeetifyJson, "x.dem");

        await Assert.That(g.Players.Count).IsEqualTo(1);
        await Assert.That(g.Players).ContainsKey("valid");
    }

    /// <summary>Ours converter_coerces mixed numeric types to double.</summary>
    [Test]
    public async Task OursConverter_CoercesMixedNumericTypesToDouble()
    {
        PlayerStatsInput[] input = new[]
        {
            new PlayerStatsInput("p1", 2, 0, new Dictionary<string, object?>
            {
                ["TotalK"] = 10, // int
                ["ADR"] = 89.2, // double
                ["HS%"] = "52.0", // string (bench emits some stats as strings)
                ["TotalA"] = (long)5 // long
            })
        };

        GoldenStatsDocument g = OursGoldenStatsConverter.Convert("x.dem", null, null, input);
        Dictionary<string, double?> s = g.Players["p1"].Stats;

        await Assert.That(s[CanonicalStatNames.Kills]).IsEqualTo(10.0);
        await Assert.That(s[CanonicalStatNames.Adr]).IsEqualTo(89.2);
        await Assert.That(s[CanonicalStatNames.HsPct]).IsEqualTo(52.0);
        await Assert.That(s[CanonicalStatNames.Assists]).IsEqualTo(5.0);
    }

    // ── OursGoldenStatsConverter ──────────────────────────────────────────────
    /// <summary>Ours converter_maps internal columns to canonical names.</summary>
    [Test]
    public async Task OursConverter_MapsInternalColumnsToCanonicalNames()
    {
        PlayerStatsInput[] input = new[]
        {
            new PlayerStatsInput(
                "ZywOo",
                3,
                0,
                new Dictionary<string, object?>
                {
                    ["TotalK"] = 24,
                    ["TotalD"] = 17,
                    ["TotalA"] = 5,
                    ["EnemyDmg"] = 1860,
                    ["ADR"] = 89.2,
                    ["HS%"] = 52.0,
                    ["KD"] = 1.41,
                    ["KAST%"] = 78.5,
                    ["HLTV"] = 1.32,
                    ["2K"] = 3,
                    ["3K"] = 1,
                    ["4K"] = 0,
                    ["5K"] = 0,
                    ["Survived"] = 12,
                    ["TrdK"] = 3,
                    ["CTW"] = 7,
                    ["TW"] = 5,
                    ["HitFoe"] = 142,
                    ["Shots"] = 312
                })
        };

        GoldenStatsDocument g = OursGoldenStatsConverter.Convert(
            "test.dem",
            null,
            null,
            input);

        await Assert.That(g.Provider).IsEqualTo("ours");
        await Assert.That(g.SchemaVersion).IsEqualTo(GoldenStatsDocument.CurrentSchemaVersion);
        await Assert.That(g.Players).ContainsKey("ZywOo");

        PlayerStatsRecord p = g.Players["ZywOo"];
        await Assert.That(p.Team).IsEqualTo(3);
        await Assert.That(p.Stats[CanonicalStatNames.Kills]).IsEqualTo(24.0);
        await Assert.That(p.Stats[CanonicalStatNames.Deaths]).IsEqualTo(17.0);
        await Assert.That(p.Stats[CanonicalStatNames.Adr]).IsEqualTo(89.2);
        await Assert.That(p.Stats[CanonicalStatNames.HltvRating]).IsEqualTo(1.32);
        await Assert.That(p.Stats[CanonicalStatNames.KastPct]).IsEqualTo(78.5);
        await Assert.That(p.Stats[CanonicalStatNames.Multi3K]).IsEqualTo(1.0);
        await Assert.That(p.Stats[CanonicalStatNames.CtRoundsWon]).IsEqualTo(7.0);
        await Assert.That(p.Stats[CanonicalStatNames.ShotsFired]).IsEqualTo(312.0);
    }

    /// <summary>Ours converter_omits unknown columns.</summary>
    [Test]
    public async Task OursConverter_OmitsUnknownColumns()
    {
        // Bench may emit columns the converter doesn't know about (custom user
        // rules). Those should be silently dropped, not cause a crash.
        PlayerStatsInput[] input = new[]
        {
            new PlayerStatsInput("p1", 2, 0, new Dictionary<string, object?>
            {
                ["TotalK"] = 10,
                ["NoSuchColumn"] = 999
            })
        };

        GoldenStatsDocument g = OursGoldenStatsConverter.Convert("x.dem", null, null, input);

        await Assert.That(g.Players["p1"].Stats).ContainsKey(CanonicalStatNames.Kills);
        await Assert.That(g.Players["p1"].Stats).DoesNotContainKey("NoSuchColumn");
        await Assert.That(g.Players["p1"].Stats).DoesNotContainKey("no_such_column");
    }

    /// <summary>Ours converter_skips players with empty name.</summary>
    [Test]
    public async Task OursConverter_SkipsPlayersWithEmptyName()
    {
        PlayerStatsInput[] input = new[]
        {
            new PlayerStatsInput("", 2, 0, new Dictionary<string, object?>
            {
                ["TotalK"] = 1
            }),
            new PlayerStatsInput("valid", 3, 1, new Dictionary<string, object?>
            {
                ["TotalK"] = 5
            })
        };

        GoldenStatsDocument g = OursGoldenStatsConverter.Convert("x.dem", null, null, input);

        await Assert.That(g.Players.Count).IsEqualTo(1);
        await Assert.That(g.Players).ContainsKey("valid");
    }
}
