#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     One resolved-and-checked v2 stat — the planner's input for a single graph
///     node. It carries everything the planner needs to build the node and the everything the
///     resolved-identity hasher needs to key it (the <see cref="RuleNodeDescriptor" /> preimage
///     fields), but performs <b>no hashing and builds no graph</b> itself (that is the planner). The
///     three canonical ASTs are kept <b>separate</b>: a
///     <see cref="TriggerCondition" /> (row 5 trigger predicate), a <see cref="ValueSelector" />
///     (row 5 value slot for <c>sum:</c>/<c>capture:</c>), and a <see cref="WhileGate" /> (row 7,
///     lowered by the planner to a parent-as-edge-source). The implicit per-player actor binding
///     is deliberately <b>absent</b> from all three: it is a planner-side
///     edge/source check, not node identity.
/// </summary>
/// <param name="Ruleset">The owning ruleset's compiler-internal id.</param>
/// <param name="StatId">The stat's id (unique in the ruleset's shared id namespace).</param>
/// <param name="Kind">The resolved-identity node kind (preimage row 1).</param>
/// <param name="ValueType">The stat's value type, including <c>list&lt;T&gt;</c> element kind (preimage row 2).</param>
/// <param name="Scope">The compound <c>(For × Per)</c> scope axis (preimage row 3).</param>
/// <param name="ConcreteEvents">
///     The trigger's expansion to concrete wire event names (preimage row 4). Populated per the
///     active demo-source profile at build; the logical event name at demo-less load.
/// </param>
/// <param name="TriggerCondition">
///     The checked (normalized) trigger-condition AST — the §4.2 composed conjunction (merged
///     <c>match:</c> keys ∧ view <c>baked:</c> ∧ define <c>where:</c> ∧ site <c>where:</c>), or
///     <c>null</c> for an untriggered/unconditioned node. Bool-typed.
/// </param>
/// <param name="ValueSelector">
///     The checked (normalized) value-selector AST a <c>sum:</c>/<c>capture:</c> carries alongside
///     its trigger (the <c>compute:</c> formula rides here too); <c>null</c> for kinds with none.
/// </param>
/// <param name="WhileGate">The checked (normalized) <c>while:</c> gate AST (bool), or <c>null</c> when ungated.</param>
/// <param name="Keep">The capture keep policy (preimage row 8); <see cref="KeepKind.None" /> for non-captures.</param>
/// <param name="TallyThresholds">
///     Tally kind-args as <c>(Min, Target)</c> pairs (preimage row 8); <c>null</c> for
///     non-tallies.
/// </param>
/// <param name="StreakWindow">Streak window in ticks (preimage row 8); <c>null</c> for non-streaks.</param>
/// <param name="StreakMinStreak">Streak minimum length (preimage row 8); <c>null</c> for non-streaks.</param>
/// <param name="BucketKeyParts">Bucket key-part list (preimage row 8, C8); <c>null</c> for non-buckets.</param>
/// <param name="BucketReducer">Bucket reducer name (preimage row 8, C8); <c>null</c> for non-buckets.</param>
/// <param name="DeclaredReads">
///     The node's statically enumerable read set (spec §3.6, A1 <c>DeclaredReads</c>): every
///     distinct reference path across all three ASTs, in first-occurrence order. Feeds edge
///     ordering and lazy scanner activation.
/// </param>
/// <param name="EntityReads">
///     The subset of reads that lower to entity-provider pre-frame reads (<c>player.*</c> and B5
///     role handles) — the reads the planner unions into scanner gating.
/// </param>
/// <param name="ResolvedView">
///     The view the trigger resolved to (<c>kill</c>, <c>bomb_planted</c>, …), or <c>null</c> for
///     raw/net/expression-only nodes. The planner reads it to lower facet reads and the actor
///     binding.
/// </param>
/// <param name="SuppressActorBinding">
///     True when <c>match: { actor: any }</c> was set — the view's implicit per-player actor binding
///     is suppressed. This is a planner-side edge/source concern (the binding is not in the hashed
///     AST); the flag is carried here for the planner, not for node identity.
/// </param>
/// <param name="Label">The optional display label (outside node identity).</param>
/// <param name="Position">The document-absolute position of the stat.</param>
/// <param name="Live">
///     The <c>compute:</c> stat's opt-in <c>live:</c> cadence. <c>false</c> ⇒ the
///     planner emits today's round-end <c>ComputeOnRoundEndEdge</c> (byte-identical to before live existed);
///     <c>true</c> ⇒ the planner wires a live recompute that fires as the compute's declared reads go
///     dirty. Identity-bearing (row 8): a live and a non-live compute over the same formula hash apart.
///     Always <c>false</c> for non-compute kinds.
/// </param>
/// <param name="RateOf">
///     A <c>rate:</c> stat's numerator bucket id (G3, per-key ratios): the sibling <c>bucket:</c> whose
///     per-key values are the ratio numerators. <c>null</c> for non-rate kinds. The planner pulls this
///     node from the local lookup to build the <c>KeyedRatioNode</c>. Identity is NOT carried here — it
///     rides row 5 via the synthesized <c>of / per</c> division on <see cref="TriggerCondition" /> — so
///     this field is a pure planner input (outside the hashed preimage).
/// </param>
/// <param name="RatePer">
///     A <c>rate:</c> stat's denominator bucket id (G3, per-key ratios): the sibling <c>bucket:</c>
///     whose key set defines the output rows (a rate is defined only where the population base exists).
///     <c>null</c> for non-rate kinds. Planner input only (see <see cref="RateOf" />).
/// </param>
/// <param name="Format">
///     A <c>compute:</c> stat's optional <c>format:</c> display string (a .NET numeric format, e.g.
///     <c>F2</c>), carried from <see cref="Model.StatDef.Format" /> for the planner to stamp on the
///     <c>ComputedStatNode</c> (<c>null</c> ⇒ the planner's <c>F1</c> default). <b>Presentation only</b>:
///     it is deliberately <b>absent from the hashed preimage</b> (<see cref="Compile.V2StatHasher" /> never
///     reads it), so two computes differing only in <c>format:</c> are behaviorally interchangeable and
///     dedup — exactly like <see cref="Label" />.
/// </param>
public sealed record CheckedStat(
    RulesetId Ruleset,
    string StatId,
    RuleNodeKind Kind,
    RulesType ValueType,
    ScopeAxis Scope,
    IReadOnlyList<string> ConcreteEvents,
    CheckedExpression? TriggerCondition,
    CheckedExpression? ValueSelector,
    CheckedExpression? WhileGate,
    KeepKind Keep,
    IReadOnlyList<(int Min, string Target)>? TallyThresholds,
    int? StreakWindow,
    int? StreakMinStreak,
    IReadOnlyList<string>? BucketKeyParts,
    string? BucketReducer,
    IReadOnlyList<string> DeclaredReads,
    IReadOnlyList<EntityProviderReference> EntityReads,
    string? ResolvedView,
    bool SuppressActorBinding,
    string? Label,
    SourcePosition Position,
    bool Live = false,
    string? RateOf = null,
    string? RatePer = null,
    string? Format = null);
