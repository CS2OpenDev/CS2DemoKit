#region

using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Pure mapping from a <see cref="VisibilityAnalyzer.Report" /> (the oracle-tested 3D
///     line-of-sight accumulation) to two <see cref="MetricTable" />s (deferred-features plan
///     F4 — 3D visibility stat columns):
///     <list type="bullet">
///         <item>
///             <c>player_visibility_stats</c> — one row per sampled player:
///             <c>ExposedToEnemiesSec</c> / <c>CouldSeeEnemySec</c> plus the sampled-time shares
///             <c>ExposedShare</c> / <c>VisionShare</c> (÷ <see cref="VisibilityAnalyzer.Report.SampledSeconds" />;
///             0 when nothing was sampled). Joins the scoreboard tables on <c>player_slot</c>.
///         </item>
///         <item>
///             <c>visibility_pairs</c> — the directed viewer→target matrix
///             (<c>exposed_sec</c> / <c>could_see_sec</c>) for downstream analysis.
///         </item>
///     </list>
///     Player names/teams are read from <see cref="ParsedDemo.Players" />. Unlike the built-in
///     <see cref="IOutputProjector" />s this maps a visibility report, not an evaluation — the
///     visibility replay is architecturally separate from the rule graph (computed on demand,
///     never part of <c>DemoAnalysis.Run</c>).
/// </summary>
public sealed class VisibilityStatsProjector
{
    /// <summary>The per-player table identity.</summary>
    public const string PlayersTableName = "player_visibility_stats";

    /// <summary>The directed-pair table identity.</summary>
    public const string PairsTableName = "visibility_pairs";

    // Dimension column keys (snake_case, matching the scoreboard projectors).
    private const string DimMatchId = "match_id";
    private const string DimMap = "map";
    private const string DimPlayerSlot = "player_slot";
    private const string DimPlayerName = "player_name";
    private const string DimTeam = "team";
    private const string DimViewerSlot = "viewer_slot";
    private const string DimViewerName = "viewer_name";
    private const string DimTargetSlot = "target_slot";
    private const string DimTargetName = "target_name";

    // Value column keys. The player columns are PascalCase like rule-declared stat columns
    // (they render as scoreboard-style headers); the pair columns follow the plan's naming.
    private const string ValExposedSec = "ExposedToEnemiesSec";
    private const string ValCouldSeeSec = "CouldSeeEnemySec";
    private const string ValExposedShare = "ExposedShare";
    private const string ValVisionShare = "VisionShare";
    private const string ValPairExposed = "exposed_sec";
    private const string ValPairCouldSee = "could_see_sec";

    /// <summary>
    ///     The match identifier used in the <c>match_id</c> dimension (typically the demo filename).
    ///     Optional — when null the dimension is omitted from rows (column stays in the schema).
    /// </summary>
    public string? MatchId { get; init; }

    /// <summary>
    ///     Projects <paramref name="report" /> into the two visibility tables
    ///     (<see cref="PlayersTableName" /> first, <see cref="PairsTableName" /> second).
    /// </summary>
    public IReadOnlyList<MetricTable> Project(VisibilityAnalyzer.Report report, ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(demo);

        return [ProjectPlayers(report, demo), ProjectPairs(report, demo)];
    }

    private MetricTable ProjectPlayers(VisibilityAnalyzer.Report report, ParsedDemo demo)
    {
        string[] dimensionColumns = [DimMatchId, DimMap, DimPlayerSlot, DimPlayerName, DimTeam];
        string[] valueColumns = [ValExposedSec, ValCouldSeeSec, ValExposedShare, ValVisionShare];

        // One row per slot the replay actually sampled (union of the two per-slot accumulators) —
        // these are the live pawns; userinfo-only entries (casters, GOTV) never appear.
        SortedSet<int> slots = new(report.ExposedToAnyEnemySeconds.Keys);
        slots.UnionWith(report.CouldSeeAnyEnemySeconds.Keys);

        List<MetricRow> rows = new(slots.Count);
        foreach (int slot in slots)
        {
            double exposed = report.ExposedToAnyEnemySeconds.GetValueOrDefault(slot);
            double couldSee = report.CouldSeeAnyEnemySeconds.GetValueOrDefault(slot);

            Dictionary<string, object?> dimensions = new(StringComparer.Ordinal)
            {
                [DimMap] = demo.MapName,
                [DimPlayerSlot] = slot,
                [DimPlayerName] = PlayerName(demo, slot),
                [DimTeam] = demo.Players.TryGetValue(slot, out PlayerInfo? pi) ? pi.Team : 0
            };
            if (MatchId is not null)
            {
                dimensions[DimMatchId] = MatchId;
            }

            Dictionary<string, object?> values = new(StringComparer.Ordinal)
            {
                [ValExposedSec] = exposed,
                [ValCouldSeeSec] = couldSee,
                [ValExposedShare] = Share(exposed, report.SampledSeconds),
                [ValVisionShare] = Share(couldSee, report.SampledSeconds)
            };

            rows.Add(new MetricRow(dimensions, values));
        }

        return new MetricTable(PlayersTableName, dimensionColumns, valueColumns, rows);
    }

    private MetricTable ProjectPairs(VisibilityAnalyzer.Report report, ParsedDemo demo)
    {
        string[] dimensionColumns =
            [DimMatchId, DimMap, DimViewerSlot, DimViewerName, DimTargetSlot, DimTargetName];
        string[] valueColumns = [ValPairExposed, ValPairCouldSee];

        List<MetricRow> rows = new(report.Pairs.Count);
        foreach (VisibilityAnalyzer.PairStat pair in report.Pairs
                     .OrderBy(p => p.ViewerSlot).ThenBy(p => p.TargetSlot))
        {
            Dictionary<string, object?> dimensions = new(StringComparer.Ordinal)
            {
                [DimMap] = demo.MapName,
                [DimViewerSlot] = pair.ViewerSlot,
                [DimViewerName] = PlayerName(demo, pair.ViewerSlot),
                [DimTargetSlot] = pair.TargetSlot,
                [DimTargetName] = PlayerName(demo, pair.TargetSlot)
            };
            if (MatchId is not null)
            {
                dimensions[DimMatchId] = MatchId;
            }

            Dictionary<string, object?> values = new(StringComparer.Ordinal)
            {
                [ValPairExposed] = pair.ExposedSeconds,
                [ValPairCouldSee] = pair.CouldSeeSeconds
            };

            rows.Add(new MetricRow(dimensions, values));
        }

        return new MetricTable(PairsTableName, dimensionColumns, valueColumns, rows);
    }

    private static double Share(double seconds, double sampledSeconds) =>
        sampledSeconds > 0 ? seconds / sampledSeconds : 0;

    private static string PlayerName(ParsedDemo demo, int slot) =>
        demo.Players.TryGetValue(slot, out PlayerInfo? pi) ? pi.Name : $"slot {slot}";
}
