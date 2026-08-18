namespace CS2DemoKit.Analysis.Config;

/// <summary>
///     One rule definition inside a <see cref="RuleChainDef" /> — the builder's internal recipe for
///     a node + edges. Used by the built-in context rules (<c>Building/BuiltinContexts.cs</c>) and
///     the Rulesets v2 per-player context bridge; not a user-facing config format (the Rulesets v1
///     YAML surface was removed).
/// </summary>
/// <param name="Id">Chain-unique identifier; the rule's materialized node is registered under this name.</param>
/// <param name="Type">Rule kind (Bool, Counter, Value).</param>
/// <param name="Name">Optional display name; defaults to <paramref name="Id" />.</param>
/// <param name="ValueType">For Value rules: CLR type name of the stored value (e.g. <c>int</c>, <c>string</c>).</param>
/// <param name="Default">For Value/Counter rules: initial value on materialization or round reset.</param>
/// <param name="ResetOnRound">If true, the produced node is registered as round-scoped.</param>
/// <param name="Parents">Optional parent linkage; defines inputs for logic rules and dependency for ordering.</param>
/// <param name="Triggers">Event-driven actions that set or activate the rule's node.</param>
/// <param name="Requires">List of capability flags the active profile must satisfy for this rule to build.</param>
public sealed record RuleDef(
    string Id,
    RuleType Type = RuleType.Bool,
    string? Name = null,
    string? ValueType = null,
    object? Default = null,
    bool ResetOnRound = false,
    ParentsDef? Parents = null,
    IReadOnlyList<TriggerDef>? Triggers = null,
    IReadOnlyList<string>? Requires = null);

/// <summary>The supported built-in rule kinds; selects which node + edges the builder materializes.</summary>
public enum RuleType
{
    /// <summary>Boolean state node (BoolNode / ConjunctionNode / DisjunctionNode based on parents).</summary>
    Bool,

    /// <summary>Integer counter incremented by triggers.</summary>
    Counter,

    /// <summary>Typed value node set by triggers (uses ValueType + Default).</summary>
    Value
}
