#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Compile;

/// <summary>
///     Lowers a ruleset's <c>show:</c> block onto the existing configured-output
///     machinery.
///     <para>
///         <c>scoreboard:</c> (<see cref="LowerScoreboard" />) maps onto the v1 column
///         projection: each entry resolves to a per-player <see cref="PerPlayerColumnAssignment" />
///         (label + display <c>group</c>) whose board is inferred from the referenced node's scope —
///         a plain stat ref defaults from its <c>per:</c>, a highlight ref surfaces the match-scoped
///         auto <c>&lt;id&gt;.count</c> node and always defaults to the match board. An explicit
///         <c>boards:</c> list overrides the default (and a two-board entry emits one column per
///         board). The projectors split round vs match tables on
///         <see cref="PerPlayerColumnAssignment.IsRoundScoped" />.
///     </para>
///     <para>
///         <c>tables:</c> (<see cref="LowerTables" />) map onto the per-round export (the v1
///         <c>outputs:</c> path): each table becomes an <see cref="OutputDef" /> whose metric refs
///         are the planner's qualified <c>{ruleset}.{stat}</c> node-map keys, so
///         the <c>ConfiguredOutputProjector</c> resolves them against the per-player
///         <c>MaterializedPlayer.NodesByRuleId</c> with no v1 validation. A highlight referenced bare
///         in a table column binds to its per-round fired state (the planner registers the
///         highlight's round-scoped conjunction under <c>{ruleset}.{highlight}</c>); a capture list
///         column binds to the list node, whose display value the serializer flattens.
///     </para>
/// </summary>
public static class ShowLowering
{
    private static readonly string[] _perPlayerRoundDimensions =
        ["match_id", "map", "round_number", "player_slot", "player_name", "team"];

    private static readonly string[] _perPlayerMatchDimensions =
        ["match_id", "map", "player_slot", "player_name", "team"];

    /// <summary>A match-level table has no player dimension — a single row keyed only by match/map.</summary>
    private static readonly string[] _perMatchDimensions =
        ["match_id", "map"];

    /// <summary>
    ///     Lowers a checked ruleset's <c>show: scoreboard:</c> to per-player column
    ///     assignments. Called by the planner inside the per-player template factory once a
    ///     ruleset's stats and highlights are materialized, so every referent already lives in
    ///     <paramref name="nodesByRuleId" /> under its qualified <c>{ruleset}.{stat}</c> key.
    /// </summary>
    /// <param name="ruleset">The checked ruleset (its <see cref="ShowDef" />, stats, highlights, id).</param>
    /// <param name="nodesByRuleId">
    ///     The template's qualified <c>{ruleset}.{stat}</c> → node map (bare-id fallback intentionally
    ///     absent, §6 obligation 8), keyed as the planner keys it (case-insensitive).
    /// </param>
    /// <returns>
    ///     One <see cref="PerPlayerColumnAssignment" /> per board of each scoreboard entry; empty when there is no
    ///     <c>show: scoreboard:</c>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     A scoreboard entry references neither a stat nor a highlight of the ruleset, carries a
    ///     board that is not <c>round</c>/<c>match</c>, or resolved to no compiled node — the planner
    ///     loud-fails rather than emit a silently-empty column (the show block is not reference-checked
    ///     in the resolver).
    /// </exception>
    public static IReadOnlyList<PerPlayerColumnAssignment> LowerScoreboard(
        CheckedRuleset ruleset, IReadOnlyDictionary<string, StateNode> nodesByRuleId)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(nodesByRuleId);
        if (ruleset.Show is not { Scoreboard.Count: > 0 } show)
        {
            return [];
        }

        HashSet<string> highlightIds = new(ruleset.Highlights.Select(h => h.HighlightId), StringComparer.Ordinal);
        Dictionary<string, CheckedStat> statsById =
            ruleset.Stats.ToDictionary(s => s.StatId, StringComparer.Ordinal);
        // A tally: stat produces no node under its own id — its thresholds emit nodes under their
        // `target:` ids (spec §6 tally row). v1 surfaced those targets (2K/3K/4K/5K) as scoreboard
        // columns, so a scoreboard ref to a tally target resolves to its emit node (the board follows
        // the owning tally's scope), exactly as the `tables:` path already references them.
        Dictionary<string, CheckedStat> tallyTargetsById = new(StringComparer.Ordinal);
        foreach (CheckedStat s in ruleset.Stats)
        {
            if (s.TallyThresholds is { } thresholds)
            {
                foreach ((int _, string target) in thresholds)
                {
                    tallyTargetsById[target] = s;
                }
            }
        }

        // Nodes dropped as coverage skips (view unavailable on this profile) are NOT built, so a
        // scoreboard ref to one must silently drop its column, never crash the build — the coverage
        // skip is a legitimate outcome the diagnostic already reported.
        HashSet<string> coverageSkipped = new(ruleset.Coverage.Select(c => c.NodeId), StringComparer.Ordinal);

        List<PerPlayerColumnAssignment> columns = [];
        foreach (ScoreboardEntry entry in show.Scoreboard)
        {
            string baseId = entry.Stat.EndsWith(".count", StringComparison.Ordinal)
                ? entry.Stat[..^".count".Length]
                : entry.Stat;
            if (coverageSkipped.Contains(baseId))
            {
                continue;
            }

            ScoreboardRef reference = ResolveScoreboardRef(ruleset, entry, highlightIds, statsById, tallyTargetsById);
            if (!nodesByRuleId.TryGetValue(reference.NodeKey, out StateNode? node))
            {
                throw new InvalidOperationException(
                    $"show: scoreboard entry '{entry.Stat}' in ruleset '{ruleset.Id.Id}' resolved to no compiled "
                    + $"node (expected node-map key '{reference.NodeKey}').");
            }

            foreach (bool roundScoped in MapBoards(entry, reference.DefaultRoundScoped))
            {
                columns.Add(new PerPlayerColumnAssignment(
                    node, reference.ColumnName, entry.Group, ruleset.Id.JoinKey, roundScoped, entry.As));
            }
        }

        return columns;
    }

    /// <summary>
    ///     Resolves a scoreboard entry to its qualified node-map key, the board its <c>per:</c>
    ///     defaults to, and its column label. A highlight ref (bare <c>&lt;id&gt;</c> or explicit
    ///     <c>&lt;id&gt;.count</c>) surfaces the auto <c>.count</c> node — always match-scoped
    ///     (spec §6 highlight row) — while a plain stat ref defaults from the stat's compound scope
    ///     axis (round-scoped ⇒ round board).
    /// </summary>
    private static ScoreboardRef ResolveScoreboardRef(
        CheckedRuleset ruleset, ScoreboardEntry entry, HashSet<string> highlightIds,
        Dictionary<string, CheckedStat> statsById, Dictionary<string, CheckedStat> tallyTargetsById)
    {
        string statRef = entry.Stat;
        string columnName = entry.Label ?? statRef;

        // Highlight ref: `<id>` or `<id>.count`, both surfacing the match-scoped `.count` node.
        string highlightBase = statRef.EndsWith(".count", StringComparison.Ordinal)
            ? statRef[..^".count".Length]
            : statRef;
        if (highlightIds.Contains(highlightBase))
        {
            return new ScoreboardRef($"{ruleset.Id.Id}.{highlightBase}.count", false, columnName);
        }

        // Plain stat ref: board defaults from the stat's per: (round-scoped ⇒ round board).
        if (statsById.TryGetValue(statRef, out CheckedStat? stat))
        {
            bool roundScoped = stat.Scope is ScopeAxis.Round or ScopeAxis.PlayerRound;
            return new ScoreboardRef($"{ruleset.Id.Id}.{statRef}", roundScoped, columnName);
        }

        // Tally-target ref (2K/3K/4K/5K): the emit node keyed under the target id; board follows the
        // owning tally's scope, just like a plain stat ref.
        if (tallyTargetsById.TryGetValue(statRef, out CheckedStat? tally))
        {
            bool roundScoped = tally.Scope is ScopeAxis.Round or ScopeAxis.PlayerRound;
            return new ScoreboardRef($"{ruleset.Id.Id}.{statRef}", roundScoped, columnName);
        }

        throw new InvalidOperationException(
            $"show: scoreboard entry '{statRef}' in ruleset '{ruleset.Id.Id}' references neither a stat, a "
            + "highlight, nor a tally target defined in the ruleset.");
    }

    /// <summary>
    ///     Maps a scoreboard entry's <c>boards:</c> override to the projector's round/match flag, one
    ///     per board (a two-board entry lands its column in both tables); an absent override uses the
    ///     per:-inferred default.
    /// </summary>
    private static List<bool> MapBoards(ScoreboardEntry entry, bool defaultRoundScoped)
    {
        if (entry.Boards is not { Count: > 0 } boards)
        {
            return [defaultRoundScoped];
        }

        List<bool> flags = new(boards.Count);
        foreach (string board in boards)
        {
            flags.Add(board switch
            {
                "round" => true,
                "match" => false,
                _ => throw new InvalidOperationException(
                    $"show: scoreboard boards entry '{board}' is not a valid board (round | match).")
            });
        }

        return flags;
    }

    /// <summary>Lowers a checked ruleset's <c>tables:</c> to configured-output defs.</summary>
    /// <param name="ruleset">The checked ruleset (its <see cref="ShowDef" /> and id).</param>
    /// <returns>One <see cref="OutputDef" /> per declared table; empty when there is no <c>show: tables:</c>.</returns>
    public static IReadOnlyList<OutputDef> LowerTables(CheckedRuleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        if (ruleset.Show is not { Tables.Count: > 0 } show)
        {
            return [];
        }

        List<OutputDef> outputs = new(show.Tables.Count);
        foreach (TableDef table in show.Tables)
        {
            (OutputScope scope, IReadOnlyList<string> dimensions) = MapPer(table.Per);
            List<MetricRef> metrics = new(table.Columns.Count);
            foreach (TableColumn column in table.Columns)
            {
                // The qualified {ruleset}.{stat} key: stats register there, a highlight registers
                // its per-round conjunction there, so a column ref resolves uniformly (obligation 8).
                metrics.Add(new MetricRef($"{ruleset.Id.Id}.{column.Stat}", column.Label ?? column.Stat, column.As));
            }

            outputs.Add(new OutputDef(table.Name, scope, metrics, dimensions));
        }

        return outputs;
    }

    private static (OutputScope Scope, IReadOnlyList<string> Dimensions) MapPer(string? per) =>
        per switch
        {
            "player_round" => (OutputScope.PerPlayerPerRound, _perPlayerRoundDimensions),
            "player_match" => (OutputScope.PerPlayerPerGame, _perPlayerMatchDimensions),
            // A game-scoped (for: match) ruleset's table: one match-level row, metrics resolved against
            // the build's game node map. No player dimension.
            "match" => (OutputScope.PerMatch, _perMatchDimensions),
            _ => throw new InvalidOperationException(
                $"show: table dimension 'per: {per ?? "<null>"}' is not a supported v2.0 table dimension "
                + "(player_round | player_match | match).")
        };

    /// <summary>A resolved scoreboard reference: its node-map key, per:-inferred board, and column label.</summary>
    /// <param name="NodeKey">The qualified <c>{ruleset}.{stat}</c> node-map key of the referent.</param>
    /// <param name="DefaultRoundScoped">The board the referent's scope defaults to (round ⇒ true).</param>
    /// <param name="ColumnName">The column header label (the entry's <c>label:</c>, else the stat ref).</param>
    private readonly record struct ScoreboardRef(string NodeKey, bool DefaultRoundScoped, string ColumnName);
}
