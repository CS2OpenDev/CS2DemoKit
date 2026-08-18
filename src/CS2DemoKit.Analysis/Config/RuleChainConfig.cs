namespace CS2DemoKit.Analysis.Config;

/// <summary>
///     One built-in rule chain — the builder's internal node-recipe IR (see
///     <c>Building/BuiltinContexts.cs</c>). Not a user-facing config format: the Rulesets v1 YAML
///     surface was removed; user rules are Rulesets v2 <c>ruleset:</c> documents.
/// </summary>
/// <param name="Id">Unique chain identifier.</param>
/// <param name="Scope">Whether the chain is materialized once per game or once per player.</param>
/// <param name="Rules">Ordered list of rule definitions inside the chain.</param>
public sealed record RuleChainDef(
    string Id,
    ChainScope Scope,
    IReadOnlyList<RuleDef> Rules);

/// <summary>Materialization scope for a rule chain: one instance for the whole demo, or one per player.</summary>
public enum ChainScope
{
    /// <summary>One chain instance for the entire demo.</summary>
    Game,

    /// <summary>One chain instance per discovered player slot.</summary>
    PerPlayer
}
