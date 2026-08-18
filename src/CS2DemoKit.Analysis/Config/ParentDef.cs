namespace CS2DemoKit.Analysis.Config;

/// <summary>Declares how a rule combines its parent rules' satisfaction into its own activation.</summary>
/// <param name="Mode">Conjunction (<c>All</c>) or disjunction (<c>Any</c>) of the parent rules.</param>
/// <param name="Rules">References to the parent rules feeding this combination.</param>
public sealed record ParentsDef(
    ParentMode Mode,
    IReadOnlyList<ParentRef> Rules);

/// <summary>How parent satisfaction is combined: ALL must be satisfied, or ANY one suffices.</summary>
public enum ParentMode
{
    /// <summary>Conjunction — all parents must be satisfied.</summary>
    All,

    /// <summary>Disjunction — any one satisfied parent suffices.</summary>
    Any
}

/// <summary>A parent reference inside a <see cref="ParentsDef" />: target rule id plus an optional predicate expression.</summary>
/// <param name="RuleId">Id of the parent rule.</param>
/// <param name="When">Optional predicate expression evaluated against the parent's stored value.</param>
public sealed record ParentRef(
    string RuleId,
    string? When = null);
