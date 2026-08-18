namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     Stable diagnostic codes the resolver emits, beyond the semantic-core
///     <c>DiagnosticCodes</c> it surfaces verbatim for expression-level errors (type mismatches,
///     unknown roots, …). Codes are part of the contract: tools may key on them, so renames are
///     breaking changes.
/// </summary>
public static class ResolveDiagnosticCodes
{
    /// <summary>An <c>on:</c>/kind-arg trigger source that is not a known view, define, sibling flag, raw, or net message.</summary>
    public const string UnknownTriggerSource = "resolve.unknown-trigger-source";

    /// <summary>A trigger-bodied define referenced where an expression was expected (resolve stage).</summary>
    public const string DefineInExpression = "resolve.define-in-expression";

    /// <summary>A <c>match:</c> key present in both the define trigger and the site — no silent last-wins.</summary>
    public const string DuplicateMatchKey = "resolve.duplicate-match-key";

    /// <summary>A <c>match:</c> key that is not a facet of the resolved view.</summary>
    public const string UnknownFacet = "resolve.unknown-facet";

    /// <summary>A within-ruleset stat-reference cycle (spec §6 cycle rule; the named cycle path is in the message).</summary>
    public const string StatReferenceCycle = "resolve.stat-reference-cycle";

    /// <summary>A stat literally named <c>this</c> — shadows the self-reference (spec §4).</summary>
    public const string ThisShadowed = "resolve.this-shadowed";

    /// <summary>
    ///     A value-selector/where/compute slot whose checked type is wrong for the kind (e.g. a list where a scalar is
    ///     required).
    /// </summary>
    public const string BadSlotType = "resolve.bad-slot-type";

    /// <summary>
    ///     A qualified <c>ruleset.stat</c> read the export graph could not resolve (unknown ruleset/stat, not exported,
    ///     not in use:).
    /// </summary>
    public const string BadCrossReference = "resolve.bad-cross-reference";

    /// <summary>
    ///     A qualified <c>ruleset.stat</c> read naming a ruleset that is not in this document's <c>use:</c> allowlist.
    /// </summary>
    public const string CrossRefNotInUse = "resolve.cross-ref.not-in-use";

    /// <summary>A qualified <c>ruleset.stat</c> read whose ruleset segment names no ruleset in the directory.</summary>
    public const string CrossRefUnknownRuleset = "resolve.cross-ref.unknown-ruleset";

    /// <summary>
    ///     A qualified <c>ruleset.stat</c> read whose stat segment is not a declared stat/highlight of that ruleset.
    /// </summary>
    public const string CrossRefUnknownStat = "resolve.cross-ref.unknown-stat";

    /// <summary>
    ///     A qualified <c>ruleset.stat</c> read of a stat the target ruleset declares but does not <c>exports:</c>.
    /// </summary>
    public const string CrossRefNotExported = "resolve.cross-ref.not-exported";

    /// <summary>
    ///     A match-scoped ruleset reading a per-player ruleset's stat — no player binding exists at match scope
    ///     (the read-scope rule).
    /// </summary>
    public const string CrossRefReadScope = "resolve.cross-ref.read-scope";

    /// <summary>A cross-ruleset stat-reference cycle: a build error naming the cycle path.</summary>
    public const string CrossRefCycle = "resolve.cross-ref.cycle";

    /// <summary>A ruleset with no <c>for:</c> scope reached the resolver (should be caught structurally, defensive).</summary>
    public const string MissingScope = "resolve.missing-scope";

    /// <summary>A highlight <c>kind:</c> value that is not one of <c>highlight</c> | <c>funny</c> | <c>lowlight</c>.</summary>
    public const string BadHighlightKind = "resolve.bad-highlight-kind";

    /// <summary>
    ///     A stat kind the resolver cannot yet lower faithfully — gated loudly rather than emitting a
    ///     config-less node. All eight base kinds now lower (tally/streak/bucket completed the set);
    ///     this code is <b>reserved</b> for the C8 bucket lifts still on the roadmap (composite keys +
    ///     per-bucket reducers), which have no authoring surface yet.
    /// </summary>
    public const string UnsupportedKind = "resolve.unsupported-kind";
}
