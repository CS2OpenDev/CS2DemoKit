#region

using System.Globalization;
using System.Text.Json;

#endregion

namespace CS2DemoKit.Analysis.GoldenStats;

/// <summary>
///     Converts a Leetify <c>?include=playerStats</c> API JSON response (or the
///     equivalent <c>&lt;id&gt;.leetify.json</c> file we cache under
///     <c>demos/benchmarks/</c>) into the canonical <see cref="GoldenStatsDocument" />
///     shape. The Leetify-specific knowledge — key naming, percent scaling
///     (Leetify emits <c>hsp = 0.4375</c>; we want <c>43.75</c>) — is
///     centralised in this file so downstream comparison code stays
///     provider-agnostic.
///     <para>
///         The converter reads only the fields it knows about and ignores
///         everything else. Leetify ships new fields routinely; nothing
///         breaks when that happens — the corresponding canonical stat just
///         stays unmapped until someone adds the mapping here.
///     </para>
/// </summary>
public static class LeetifyGoldenStatsConverter
{
    /// <summary>
    ///     Build a golden record from a Leetify JSON string. Throws
    ///     <see cref="JsonException" /> on malformed input.
    /// </summary>
    public static GoldenStatsDocument Convert(
        string leetifyJson,
        string demoFileName,
        string? demoSha256 = null,
        string? generatedAt = null)
    {
        using JsonDocument doc = JsonDocument.Parse(leetifyJson);
        JsonElement root = doc.RootElement;

        MatchMetadata match = ConvertMatchMetadata(root);
        Dictionary<string, PlayerStatsRecord> players = ConvertPlayerStats(root);

        return new GoldenStatsDocument(
            GoldenStatsDocument.CurrentSchemaVersion,
            demoFileName,
            demoSha256,
            "leetify",
            null,
            generatedAt ?? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            match,
            players);
    }

    private static MatchMetadata ConvertMatchMetadata(JsonElement root)
    {
        string? map = root.TryGetProperty("mapName", out JsonElement mapEl) && mapEl.ValueKind == JsonValueKind.String
            ? mapEl.GetString()
            : null;

        // Leetify doesn't report tick count or round winners — match metadata
        // from Leetify is sparse, which is fine; comparison code treats null
        // as "this provider doesn't report it."
        return new MatchMetadata(map);
    }

    private static Dictionary<string, PlayerStatsRecord> ConvertPlayerStats(JsonElement root)
    {
        Dictionary<string, PlayerStatsRecord> players = new(StringComparer.Ordinal);

        if (!root.TryGetProperty("playerStats", out JsonElement playerStatsArr) ||
            playerStatsArr.ValueKind != JsonValueKind.Array)
        {
            return players;
        }

        foreach (JsonElement p in playerStatsArr.EnumerateArray())
        {
            string? name = p.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            int team = ReadInt(p, "initialTeamNumber") ?? 0;
            ulong? steamId = ReadUInt64String(p, "steam64Id");

            Dictionary<string, double?> stats = new(StringComparer.Ordinal)
            {
                // Counts — direct copy
                [CanonicalStatNames.Kills] = ReadDouble(p, "totalKills"),
                [CanonicalStatNames.Deaths] = ReadDouble(p, "totalDeaths"),
                [CanonicalStatNames.Assists] = ReadDouble(p, "totalAssists"),
                [CanonicalStatNames.EnemyDamage] = ReadDouble(p, "totalDamage"),
                [CanonicalStatNames.RoundsSurvived] = ReadDouble(p, "roundsSurvived"),
                [CanonicalStatNames.TradeKills] = ReadDouble(p, "tradeKillsSucceeded"),
                [CanonicalStatNames.CtRoundsWon] = ReadDouble(p, "ctRoundsWon"),
                [CanonicalStatNames.RoundsWon] = ReadDouble(p, "tRoundsWon"),
                [CanonicalStatNames.ShotsHitFoe] = ReadDouble(p, "shotsHitFoe"),
                [CanonicalStatNames.ShotsFired] = ReadDouble(p, "shotsFired"),
                [CanonicalStatNames.Multi2K] = ReadDouble(p, "multi2k"),
                [CanonicalStatNames.Multi3K] = ReadDouble(p, "multi3k"),
                [CanonicalStatNames.Multi4K] = ReadDouble(p, "multi4k"),
                [CanonicalStatNames.Multi5K] = ReadDouble(p, "multi5k"),

                // Floats / ratios — direct copy
                [CanonicalStatNames.Adr] = ReadDouble(p, "dpr"),
                [CanonicalStatNames.Kd] = ReadDouble(p, "kdRatio"),
                [CanonicalStatNames.HltvRating] = ReadDouble(p, "hltvRating"),

                // Percentages — Leetify emits 0–1 (e.g. hsp=0.4375); golden uses 0–100.
                [CanonicalStatNames.KastPct] = ScalePercent(ReadDouble(p, "kast")),
                [CanonicalStatNames.HsPct] = ScalePercent(ReadDouble(p, "hsp"))
            };

            players[name] = new PlayerStatsRecord(
                team,
                null, // Leetify has no user-id concept
                steamId,
                stats);
        }

        return players;
    }

    private static double? ReadDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el))
        {
            return null;
        }

        return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;
    }

    // ── JSON read helpers ─────────────────────────────────────────────────────

    private static int? ReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el))
        {
            return null;
        }

        return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
    }

    /// <summary>
    ///     Leetify emits steam64Id as a string (JSON numbers can't represent
    ///     the full 64-bit Steam ID range without precision loss). Coerce.
    /// </summary>
    private static ulong? ReadUInt64String(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el))
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return ulong.TryParse(el.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong v)
            ? v
            : null;
    }

    /// <summary>
    ///     Scale a 0–1 ratio to a 0–100 percentage. Null passes through.
    /// </summary>
    private static double? ScalePercent(double? raw) => raw is null ? null : raw.Value * 100.0;
}
