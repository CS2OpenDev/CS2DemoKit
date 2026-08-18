#region

using CS2DemoKit.Analysis.Rules.Checking;

#endregion

namespace CS2DemoKit.Analysis.Rules.Hashing;

/// <summary>Preimage row 1 — the stat/rule node kind (spec §6).</summary>
public enum RuleNodeKind
{
    /// <summary>Unset. Not a hashable kind.</summary>
    None = 0,

    /// <summary>A boolean flag node.</summary>
    Flag,

    /// <summary>An event-triggered counter.</summary>
    Count,

    /// <summary>An event-triggered sum.</summary>
    Sum,

    /// <summary>A value capture (first / last / list).</summary>
    Capture,

    /// <summary>A keyed bucket lift (C8; key-part list and reducer join row 8 when it ships).</summary>
    Bucket,

    /// <summary>A computed expression node.</summary>
    Compute,

    /// <summary>A highlight emitter.</summary>
    Highlight,

    /// <summary>
    ///     A threshold tally (v1 <c>ThresholdTally</c>, now hashable — plan decision 4). Its
    ///     bucket thresholds join the keep-spec row (row 8).
    /// </summary>
    Tally,

    /// <summary>
    ///     A windowed streak (v1 <c>WindowedStreak</c>, now hashable — plan decision 4). Its
    ///     window and minimum-streak length join the keep-spec row (row 8).
    /// </summary>
    Streak,

    /// <summary>
    ///     A per-key ratio over two same-keyed bucket nodes (G3, <c>rate:</c>). Identity is carried
    ///     entirely on row 5+6: the resolver synthesizes an <c>of / per</c> division expression over the
    ///     two bucket references into the <see cref="RuleNodeDescriptor.Expression" /> slot, so two rates
    ///     over different bucket pairs hash apart via the embedded referenced-node hashes (no new preimage row).
    /// </summary>
    Rate,

    /// <summary>
    ///     A windowed multi-kill PULSE (<c>burst:</c>) — bool-valued. Like <see cref="Streak" /> its
    ///     window and minimum count join the keep-spec row (row 8), but it lowers to a bool pulse node
    ///     rather than an int counter.
    /// </summary>
    Burst
}

/// <summary>
///     Preimage row 3 — the compound <c>(For × Per)</c> scope axis a node lives on (spec §6
///     row 3, plan decision 5). The four-value product replaces the old collapsed
///     <c>match | round | player</c>: a per-player <c>per: round</c> stat and its
///     <c>per: match</c> twin differ <b>only</b> here, so a single per-player value would
///     false-dedup them (a corruption class).
/// </summary>
public enum ScopeAxis
{
    /// <summary>Unset. Not a hashable axis.</summary>
    None = 0,

    /// <summary>Match-scoped (one value for the whole match).</summary>
    Match,

    /// <summary>Round-scoped (reset each round).</summary>
    Round,

    /// <summary>Per-player, match-scoped (one value per player for the whole match).</summary>
    PlayerMatch,

    /// <summary>Per-player, round-scoped (one value per player, reset each round).</summary>
    PlayerRound
}

/// <summary>Preimage row 8 — a capture's keep policy (spec §6).</summary>
public enum KeepKind
{
    /// <summary>Not a capture.</summary>
    None = 0,

    /// <summary>Keep the first captured value.</summary>
    First,

    /// <summary>Keep the last captured value.</summary>
    Last,

    /// <summary>Keep every captured value as a list.</summary>
    List,

    /// <summary>Keep the running minimum of a numeric captured value (first value on an unseen window).</summary>
    Min,

    /// <summary>Keep the running maximum of a numeric captured value (first value on an unseen window).</summary>
    Max
}

/// <summary>
///     Everything that participates in a v2 node's resolved-identity hash — the spec §6
///     preimage, one property per row (rows 5 and 6 travel together inside
///     <see cref="Expression" />: the canonical AST is serialized with embedded
///     referenced-stat hashes). Anything NOT in this record — display names, descriptions,
///     output destinations, positions — is deliberately outside node identity.
/// </summary>
/// <param name="StatId">
///     The node's own id. Hashed only as row 9's id-salt when the node has no inputs
///     (no trigger events, no expression, no gate), so two empty counters stay distinct;
///     nodes with inputs dedup regardless of their ids.
/// </param>
/// <param name="Kind">Row 1: flag / count / sum / capture / bucket / compute / highlight / tally / streak.</param>
/// <param name="ValueType">Row 2: the §3.1 value type, including <c>list&lt;T&gt;</c> element type.</param>
/// <param name="Per">Row 3: the compound <c>(For × Per)</c> scope axis (match / round / player_match / player_round).</param>
/// <param name="ConcreteEvents">
///     Row 4: the trigger's expansion to concrete event types. Order-insensitive — the hasher
///     sorts and dedups; empty for untriggered stats.
/// </param>
/// <param name="Expression">
///     Row 5 (trigger condition) + row 6: the checked (normalized) trigger-condition expression,
///     or null when the node has none. For a <c>sum:</c>/<c>capture:</c> this is the
///     <c>where:</c>-style trigger predicate, distinct from <see cref="ValueSelector" />.
/// </param>
/// <param name="ValueSelector">
///     Row 5 (value selector): the checked (normalized) value-selector expression a
///     <c>sum:</c>/<c>capture:</c> carries alongside its trigger. When present the row is
///     packed as the two-slot form <c>(cond … | value …)</c>, so a capture and a sum sharing
///     a trigger but differing in their value selector do not dedup. Null for kinds with a
///     single AST (or none).
/// </param>
/// <param name="GateHash">
///     Row 7: the resolved hash of the <c>while:</c> gate node, when gated (distinct from the row-5
///     trigger).
/// </param>
/// <param name="Keep">Row 8: the capture keep policy.</param>
/// <param name="BucketKeyParts">Row 8 (C8 bucket lifts): the bucket key-part list, when the node is a bucket.</param>
/// <param name="BucketReducer">Row 8 (C8 bucket lifts): the per-bucket reducer name, when the node is a bucket.</param>
/// <param name="TallyThresholds">
///     Row 8 (tally kind-args): the tally's bucket thresholds as <c>(Min, Target)</c> pairs
///     (spec §6 row 8). Both components are identity-bearing: <c>Min</c> is the boundary count
///     and <c>Target</c> is the emit-node id that boundary writes to — different targets write
///     to different counter nodes, so v1's own hasher hashes both. Order-insensitive (the
///     hasher sorts by <c>(Min, Target)</c> and dedups). Two tallies differing only in a
///     threshold's min OR its target hash apart.
/// </param>
/// <param name="StreakWindow">
///     Row 8 (streak kind-args): the streak's window in ticks (max gap between events that extends
///     the streak).
/// </param>
/// <param name="StreakMinStreak">Row 8 (streak kind-args): the streak's minimum length that counts as a completed streak.</param>
/// <param name="ActorBinding">
///     Row 10 (view actor-role binding): a canonical token for the view's implicit per-player actor
///     role, or <c>null</c> for nodes with no view (raw / net / expression / compute). When null the
///     row is <b>absent</b> from the preimage, so every v1 caller (which always passes null) hashes
///     byte-identically to before this row existed — the same additive pattern as the tally / bucket
///     rows. When non-null it discriminates otherwise-identical stats whose view binds a different
///     actor slot: a <c>count: kill</c> (actor = killer) and a <c>count: assist</c> (actor =
///     assister) share rows 1–9 (same kind, type, scope, concrete events, baked trigger) yet write
///     different per-player values, so this row keeps them apart. The role-equality itself is applied
///     by the planner at edge-build time per slot (it is not in the row-5 trigger AST, §4.2); this
///     row makes that per-slot difference visible to node identity. Same-view stats carry the same
///     token and still dedup.
/// </param>
/// <param name="Live">
///     Row 8 (compute cadence): <c>true</c> when a <c>compute:</c> declared
///     <c>live: true</c> — an opt-in that re-evaluates the compute live as its reads change rather than
///     once at round end. Emitted onto row 8 ONLY when <c>true</c>, so a non-live compute and every v1
///     caller (which never sets it) hash byte-identically to the earlier preimage — the same additive
///     discipline as the tally / bucket / streak kind-args. When <c>true</c> it discriminates an
///     otherwise-identical live and non-live compute: their cadence differs, so they are NOT
///     behaviorally interchangeable and must not share a node.
/// </param>
public sealed record RuleNodeDescriptor(
    string StatId,
    RuleNodeKind Kind,
    RulesType ValueType,
    ScopeAxis Per,
    IReadOnlyList<string> ConcreteEvents,
    CheckedExpression? Expression = null,
    CheckedExpression? ValueSelector = null,
    ReadOnlyMemory<byte>? GateHash = null,
    KeepKind Keep = KeepKind.None,
    IReadOnlyList<string>? BucketKeyParts = null,
    string? BucketReducer = null,
    IReadOnlyList<(int Min, string Target)>? TallyThresholds = null,
    int? StreakWindow = null,
    int? StreakMinStreak = null,
    string? ActorBinding = null,
    bool Live = false);
