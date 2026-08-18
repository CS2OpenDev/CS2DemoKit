#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The compiler-internal identity of a v2 ruleset. Typed ruleset
///     identity is deliberately deferred; until then every string-keyed surface of the shared
///     <c>BuildResult</c> (the evaluator, the timeline's <c>RuleChainEvent.ChainName</c>, the
///     output projector's <c>_chain_</c> prefix reader, per-player column assignment, the
///     fire-count badge layer) keys on the canonical string projection <see cref="JoinKey" />, so
///     v2 rulesets run with zero evaluator/Abstractions changes.
/// </summary>
/// <param name="Id">The ruleset id (the <c>ruleset:</c> key).</param>
/// <param name="Scope">The ruleset's materialization scope (<c>for: match | each_player</c>).</param>
public readonly record struct RulesetId(string Id, RulesetScope Scope)
{
    /// <summary>
    ///     The canonical string projection stamped into the shared <c>BuildResult</c> — the v1
    ///     chain-naming convention (<c>_chain_&lt;id&gt;</c>) so the existing string consumers
    ///     resolve v2 nodes unchanged. Only the id participates; the
    ///     scope is compiler-internal.
    /// </summary>
    public string JoinKey => $"_chain_{Id}";

    /// <summary>Formats as the join key for logs and test output.</summary>
    /// <returns>The <see cref="JoinKey" />.</returns>
    public override string ToString() => JoinKey;
}
