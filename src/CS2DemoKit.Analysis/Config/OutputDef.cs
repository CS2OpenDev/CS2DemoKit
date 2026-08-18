#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.Config;

/// <summary>
///     One declared output table (the YAML <c>outputs:</c> schema): which rule values to sample, at
///     which scope, with which dimension columns. Configured outputs are <b>additive</b> — the three
///     built-in tables (<c>player_round_stats</c>, <c>player_game_stats</c>, <c>rule_chain_events</c>)
///     always emit; declared outputs append.
/// </summary>
/// <param name="Id">
///     Unique output identifier — becomes the emitted <c>MetricTable.Name</c>. Shares overlay
///     semantics with chains (same-id user output replaces shipped; duplicate ids within a tier error)
///     but is a separate id namespace from chain ids.
/// </param>
/// <param name="Scope">Sampling strategy: per (player, round), per (player, game), or per chain event.</param>
/// <param name="Metrics">
///     Rule references whose values become the table's value columns, in declared order. Empty for
///     <see cref="OutputScope.PerEvent" /> (event rows are dimension-only).
/// </param>
/// <param name="Dimensions">
///     Dimension columns to emit, in declared order (fixed per-scope dimension registry).
/// </param>
/// <param name="Chains">
///     For <see cref="OutputScope.PerEvent" /> only: the chain ids whose satisfactions to log.
///     Required there; an error on any other scope.
/// </param>
/// <param name="Enabled">
///     <c>enabled: false</c> lets a user-tier file turn off a shipped output without redefining it
///     (same overlay semantics as chains). Disabled outputs are dropped after tier merging.
/// </param>
public sealed record OutputDef(
    string Id,
    OutputScope Scope,
    IReadOnlyList<MetricRef> Metrics,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string>? Chains = null,
    bool Enabled = true);

/// <summary>One value column of a configured output: a rule reference plus its display label.</summary>
/// <param name="RuleRef">
///     A bare rule id (<c>kills</c> — must resolve unambiguously across all chains) or a
///     chain-qualified reference (<c>kast.kast_pct</c> — the escape hatch when two chains declare
///     structurally different rules under one id). Bare ids, loud ambiguity error,
///     qualified escape.
/// </param>
/// <param name="Label">Column header text; defaults to <paramref name="RuleRef" /> when omitted.</param>
/// <param name="Format">
///     The column's <c>as:</c> display formatting for a tick-valued value (v2 <c>show:</c> table
///     <c>as:</c>). <see cref="ColumnValueFormat.None" /> (the default, and every v1 output) leaves
///     the projected value byte-identical; the projector applies non-<c>None</c> formats at the
///     demo's tick rate when reading the cell.
/// </param>
public sealed record MetricRef(string RuleRef, string Label, ColumnValueFormat Format = ColumnValueFormat.None);

/// <summary>Sampling scope of a configured output table.</summary>
public enum OutputScope
{
    /// <summary>One row per (player, live round) — sampled at the last snapshot of each round.</summary>
    PerPlayerPerRound,

    /// <summary>One row per player — sampled at the final snapshot (end-of-match scoreboard shape).</summary>
    PerPlayerPerGame,

    /// <summary>One row per chain satisfaction (rising edge) of the declared <c>chains:</c>.</summary>
    PerEvent,

    /// <summary>
    ///     A single match-level row — sampled at the final snapshot. The metric refs resolve against the
    ///     build's game-scoped node map (a <c>for: match</c> ruleset's game nodes), so the row carries
    ///     match totals with no player dimension. The output shape a game-scoped <c>show: tables</c>
    ///     (<c>per: match</c>) lowers to.
    /// </summary>
    PerMatch
}
