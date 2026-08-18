#region

using System.Globalization;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.GoldenStats;

/// <summary>
///     Converts our analyzer's per-player stat output (the bench tool's
///     <c>PlayerReport</c> dictionaries, or any equivalent caller-built
///     <see cref="PlayerStatsInput" /> list) into the canonical
///     <see cref="GoldenStatsDocument" /> shape.
///     <para>
///         The internal column-name → canonical-name map lives here. When
///         we rename or add an internal column, update <see cref="_columnMap" />
///         alongside the producing code; the converter is a single chokepoint.
///     </para>
/// </summary>
public static class OursGoldenStatsConverter
{
    /// <summary>
    ///     Internal column name (as our rule chains emit them) → canonical golden
    ///     stat key. The bench tool's <c>PlayerReport.Stats</c> dictionary uses
    ///     these column names verbatim; the converter rekeys them on the way to
    ///     golden format.
    /// </summary>
    private static readonly Dictionary<string, string> _columnMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["TotalK"] = CanonicalStatNames.Kills,
            ["TotalD"] = CanonicalStatNames.Deaths,
            ["TotalA"] = CanonicalStatNames.Assists,
            ["EnemyDmg"] = CanonicalStatNames.EnemyDamage,
            ["ADR"] = CanonicalStatNames.Adr,
            ["HS%"] = CanonicalStatNames.HsPct,
            ["KD"] = CanonicalStatNames.Kd,
            ["KAST%"] = CanonicalStatNames.KastPct,
            ["HLTV"] = CanonicalStatNames.HltvRating,
            ["2K"] = CanonicalStatNames.Multi2K,
            ["3K"] = CanonicalStatNames.Multi3K,
            ["4K"] = CanonicalStatNames.Multi4K,
            ["5K"] = CanonicalStatNames.Multi5K,
            ["Survived"] = CanonicalStatNames.RoundsSurvived,
            ["TrdK"] = CanonicalStatNames.TradeKills,
            ["CTW"] = CanonicalStatNames.CtRoundsWon,
            ["TW"] = CanonicalStatNames.RoundsWon,
            ["HitFoe"] = CanonicalStatNames.ShotsHitFoe,
            ["Shots"] = CanonicalStatNames.ShotsFired
        };

    /// <summary>
    ///     Build a golden record. <paramref name="demo" /> supplies match-level
    ///     metadata (tick count, map name); pass <c>null</c> when you have only
    ///     the per-player inputs and want match metadata omitted.
    /// </summary>
    public static GoldenStatsDocument Convert(
        string demoFileName,
        string? demoSha256,
        ParsedDemo? demo,
        IReadOnlyList<PlayerStatsInput> players,
        string? providerVersion = null,
        string? generatedAt = null)
    {
        MatchMetadata match = new(
            demo?.MapName,
            demo?.TickCount);

        Dictionary<string, PlayerStatsRecord> playersOut = new(StringComparer.Ordinal);
        foreach (PlayerStatsInput p in players)
        {
            if (string.IsNullOrEmpty(p.Name))
            {
                continue;
            }

            Dictionary<string, double?> stats = new(StringComparer.Ordinal);
            foreach ((string column, object? rawValue) in p.Stats)
            {
                if (!_columnMap.TryGetValue(column, out string? canonical))
                {
                    continue;
                }

                stats[canonical] = ToNullableDouble(rawValue);
            }

            playersOut[p.Name] = new PlayerStatsRecord(
                p.Team,
                p.PlayerSlot,
                null, // not available in our output today
                stats);
        }

        return new GoldenStatsDocument(
            GoldenStatsDocument.CurrentSchemaVersion,
            demoFileName,
            demoSha256,
            "ours",
            providerVersion,
            generatedAt ?? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            match,
            playersOut);
    }

    /// <summary>
    ///     Coerces the bench's loosely-typed stat value (int, double, string,
    ///     bool, null) into the canonical <c>double?</c> shape. Anything that
    ///     can't be parsed becomes <c>null</c> — comparison treats it as
    ///     "provider didn't report this stat" rather than a false zero.
    /// </summary>
    private static double? ToNullableDouble(object? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            decimal m => (double)m,
            bool b => b ? 1.0 : 0.0,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double sd) => sd,
            _ => null
        };
    }
}
