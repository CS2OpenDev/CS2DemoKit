namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     One <c>stats:</c> entry. The kind is a discriminator key
///     (<c>flag:</c> / <c>count:</c> / …), whose primary argument is carried as
///     <see cref="KindArg" /> (raw text at this stage); the trigger comes from the <c>on:</c> key.
///     A stat carrying a <c>for_each:</c> axis is multiplied by stage-1 Expand before hashing and
///     before duplicate-id checking.
/// </summary>
/// <param name="Id">The stat's id, unique within the ruleset's shared id namespace (post-expansion).</param>
/// <param name="Kind">The kind discriminator; exactly one of the eight.</param>
/// <param name="KindArg">
///     The raw text of the kind key's value (the flag condition, count/sum/capture/compute
///     expression, or tally/streak/bucket primary argument). <c>null</c> only when the kind value
///     was structurally absent.
/// </param>
/// <param name="Per">The reset scope (<c>per: round | match</c>).</param>
/// <param name="Keep">The capture retention mode; legal only under <c>capture:</c> (validated).</param>
/// <param name="Trigger">The trigger from the <c>on:</c> key, when present.</param>
/// <param name="OffTrigger">The deactivator from the <c>off:</c> key, when present (flags only).</param>
/// <param name="ForEach">The <c>for_each:</c> axes, when present; <c>null</c> after expansion.</param>
/// <param name="Label">The optional display label.</param>
/// <param name="Position">The document-absolute position of the stat.</param>
/// <param name="Thresholds">
///     The <c>tally:</c> stat's <c>thresholds:</c> list — ordered <c>(min, target)</c> bucket
///     boundaries. <c>null</c> for non-tally kinds; the resolver requires a non-empty list for a
///     <c>tally:</c>.
/// </param>
/// <param name="StreakWindow">
///     The <c>streak:</c> stat's <c>window:</c> — the maximum gap between consecutive events for the
///     streak to extend, as raw text (an integer tick count or a duration literal folded at the
///     context tick rate). <c>null</c> for non-streaks or when unspecified (the resolver defaults it).
/// </param>
/// <param name="StreakMinStreak">
///     The <c>streak:</c> stat's <c>min_streak:</c> — the minimum streak length that counts as a
///     completed streak. <c>null</c> for non-streaks or when unspecified (the resolver defaults it).
/// </param>
/// <param name="BucketKey">
///     The <c>bucket:</c> stat's <c>key:</c> when authored as a single scalar expression that selects
///     the per-event bucket (e.g. <c>event.Weapon</c>). <c>null</c> for non-buckets, or when the key was
///     authored as a <em>list</em> (see <see cref="BucketKeys" />). The resolver requires exactly one of
///     <see cref="BucketKey" /> / <see cref="BucketKeys" /> for a <c>bucket:</c>.
/// </param>
/// <param name="BucketKeys">
///     The <c>bucket:</c> stat's <c>key:</c> when authored as a YAML <em>list</em> of expressions — a
///     composite/tuple key. The parts are ordered and order-bearing for identity
///     (<c>[a, b]</c> ≠ <c>[b, a]</c>). <c>null</c> when the key was a scalar (see <see cref="BucketKey" />)
///     or absent.
/// </param>
/// <param name="BucketValue">
///     The <c>bucket:</c> stat's optional <c>value:</c> — the per-event numeric amount reduced into the
///     key. <c>null</c> ⇒ no value slot (a plain count bucket unless <see cref="BucketReduce" /> is set to
///     a value-requiring reducer, which the resolver rejects). Non-null ⇒ the resolver checks it as a
///     numeric event-scope expression; the reducer defaults to <c>sum</c> unless <see cref="BucketReduce" />
///     names another.
/// </param>
/// <param name="BucketReduce">
///     The <c>bucket:</c> stat's optional <c>reduce:</c> — the named per-key reducer
///     (<c>sum | count | min | max | last | first</c>). <c>null</c> ⇒ the implicit default
///     (<c>sum</c> when <see cref="BucketValue" /> is present, else <c>count</c>) — so every pre-C8 bucket is
///     unchanged. <c>min | max | last | first</c> reduce a <c>value:</c> and so require one; <c>count</c>
///     forbids a <c>value:</c> (nothing to reduce). Validated by the resolver.
/// </param>
/// <param name="Live">
///     The <c>compute:</c> stat's opt-in <c>live:</c> cadence flag. <c>false</c> (the
///     default, and the only legal value for a scalar <c>compute:</c> or any non-compute kind) ⇒ the
///     compute evaluates ONCE at round end via a <c>ComputeOnRoundEndEdge</c> — byte-identical to
///     pre-A3a. <c>true</c> (authored as <c>compute: { value: "…", live: true }</c>) ⇒ the compute
///     re-evaluates LIVE whenever its declared reads go dirty during evaluation, so downstream reads
///     and the per-message snapshot timeline see the current value rather than only the round-end one.
///     Cadence is identity-bearing (a live and a non-live compute over the same formula hash apart).
/// </param>
/// <param name="RateOf">
///     The <c>rate:</c> stat's <c>of:</c> — the id of the sibling <c>bucket:</c> stat that supplies the
///     per-key <b>numerator</b> (G3, per-key ratios). <c>null</c> for non-rate kinds. The resolver
///     type-checks it (a numeric sibling bucket keying on the same <c>key:</c> as <see cref="RatePer" />).
/// </param>
/// <param name="RatePer">
///     The <c>rate:</c> stat's <c>per:</c> — the id of the sibling <c>bucket:</c> stat that supplies the
///     per-key <b>denominator</b> (G3, per-key ratios). <c>null</c> for non-rate kinds. Its key set is the
///     output population base: a rate row exists for every denominator key whose value is non-zero.
///     <b>Note:</b> this is the rate's numerator/denominator <c>per:</c> sub-key nested under
///     <c>rate:</c>, distinct from the stat-level <see cref="Per" /> reset scope.
/// </param>
/// <param name="Format">
///     The <c>compute:</c> stat's optional <c>format:</c> — a .NET numeric format string (e.g. <c>F2</c>,
///     <c>F0</c>) the projector applies when rendering the computed value's display string. <c>null</c> ⇒
///     the planner's default (<c>F1</c>). This is a pure <b>display</b> attribute (like <see cref="Label" />):
///     it is <b>excluded from the resolved-identity preimage</b>, so two computes differing only in
///     <c>format:</c> hash identically and dedup (first-wins, exactly like the display label).
/// </param>
public sealed record StatDef(
    string Id,
    StatKind Kind,
    string? KindArg,
    PerScope Per,
    KeepMode? Keep,
    TriggerDef? Trigger,
    TriggerDef? OffTrigger,
    IReadOnlyList<ForEachAxis>? ForEach,
    string? Label,
    SourcePosition Position,
    IReadOnlyList<TallyThreshold>? Thresholds = null,
    string? StreakWindow = null,
    int? StreakMinStreak = null,
    string? BucketKey = null,
    string? BucketValue = null,
    IReadOnlyList<string>? BucketKeys = null,
    string? BucketReduce = null,
    bool Live = false,
    string? RateOf = null,
    string? RatePer = null,
    string? Format = null);

/// <summary>
///     One <c>tally:</c> threshold bucket (spec §6 row 8): the inclusive minimum
///     source value that activates the bucket and the counter-node id the bucket increments. Mirrors
///     v1's <c>ThresholdDef</c> under the v2 <c>tally:</c> surface.
/// </summary>
/// <param name="Min">
///     The inclusive minimum source value that activates this bucket — either an int literal
///     (<see cref="TallyMinLiteral" />) or a <c>params.&lt;name&gt;</c> reference
///     (<see cref="TallyMinParam" />) the resolver binds to its literal int value before hashing.
/// </param>
/// <param name="Target">The id of the counter node this bucket increments when it wins.</param>
/// <param name="Position">The document-absolute position of the threshold entry.</param>
public sealed record TallyThreshold(TallyMin Min, string Target, SourcePosition Position);

/// <summary>
///     A <c>tally:</c> threshold's inclusive-min bound (spec §6 row 8): either an int literal or a
///     <c>params.&lt;name&gt;</c> reference the resolver resolves to its literal int value <b>before</b>
///     hashing — so <c>min: params.x</c> with <c>x = 3</c> dedups with a literal <c>min: 3</c>.
/// </summary>
public abstract record TallyMin;

/// <summary>An int-literal tally threshold min (the authored-integer form).</summary>
/// <param name="Value">The inclusive minimum source value.</param>
public sealed record TallyMinLiteral(int Value) : TallyMin;

/// <summary>
///     A <c>params.&lt;name&gt;</c> tally threshold min: the raw scalar text as authored (e.g.
///     <c>params.multi_threshold</c> or a bare declared-param name). The resolver extracts the param
///     name, validates it is a bound int param, and binds it to its literal int value pre-hash.
/// </summary>
/// <param name="RawText">The raw scalar text of the <c>min:</c> value.</param>
public sealed record TallyMinParam(string RawText) : TallyMin;

/// <summary>The v2 stat kinds. Each is exactly one graph node.</summary>
public enum StatKind
{
    /// <summary>Unset. Never produced by the mapper.</summary>
    None = 0,

    /// <summary><c>flag:</c> — a boolean node (<c>true</c> + <c>on:</c>, or <c>when:</c> over siblings).</summary>
    Flag,

    /// <summary><c>count:</c> — +1 per match or per rising edge of a flag.</summary>
    Count,

    /// <summary><c>sum:</c> — accumulate a value per match.</summary>
    Sum,

    /// <summary><c>capture:</c> — record value(s) at matches (<c>keep: first | last | list</c>).</summary>
    Capture,

    /// <summary><c>compute:</c> — a value derived at round end.</summary>
    Compute,

    /// <summary><c>tally:</c> — threshold tally over a source value.</summary>
    Tally,

    /// <summary><c>streak:</c> — windowed streak over events.</summary>
    Streak,

    /// <summary><c>bucket:</c> — per-key bucket counter.</summary>
    Bucket,

    /// <summary>
    ///     <c>rate:</c> — a per-key ratio (G3) over two same-keyed sibling <c>bucket:</c> stats
    ///     (<c>of:</c> numerator / <c>per:</c> denominator), evaluated per denominator key.
    /// </summary>
    Rate,

    /// <summary>
    ///     <c>burst:</c> — a boolean PULSE over events: goes true for one dispatch when
    ///     <c>min_streak</c> matches land within a rolling <c>window</c> (a true sliding window). Reuses
    ///     the <c>window:</c>/<c>min_streak:</c> args, but is bool-valued (unlike the int <c>streak:</c>
    ///     counter). Authored as the trigger of a windowed multi-kill highlight's <c>when:</c>.
    /// </summary>
    Burst
}

/// <summary>The reset scope shared by <c>stats:</c> and <c>highlights:</c> <c>per:</c> keys.</summary>
public enum PerScope
{
    /// <summary>Unset. Never produced by the mapper.</summary>
    None = 0,

    /// <summary><c>per: round</c> — reset every round.</summary>
    Round,

    /// <summary><c>per: match</c> — never reset within the match.</summary>
    Match
}

/// <summary>Capture retention mode (<c>keep:</c>); legal only under <c>capture:</c>.</summary>
public enum KeepMode
{
    /// <summary>Unset. Never produced by the mapper.</summary>
    None = 0,

    /// <summary><c>keep: first</c> — write-once, the first matched value.</summary>
    First,

    /// <summary><c>keep: last</c> — the most recent matched value (the default <c>value</c> semantics).</summary>
    Last,

    /// <summary><c>keep: list</c> — one ordered collection-valued node of every matched value.</summary>
    List,

    /// <summary>
    ///     <c>keep: min</c> — the running minimum of a numeric matched value over the aggregation
    ///     window. An <b>unseen</b> window takes the first value (never min against the node's phantom
    ///     0). Requires a numeric <c>capture:</c> value (validated).
    /// </summary>
    Min,

    /// <summary>
    ///     <c>keep: max</c> — the running maximum of a numeric matched value over the aggregation
    ///     window. An <b>unseen</b> window takes the first value (never max against the node's phantom
    ///     0). Requires a numeric <c>capture:</c> value (validated).
    /// </summary>
    Max
}

/// <summary>One <c>for_each:</c> axis — a substitution key and its ordered list of literal values.</summary>
/// <param name="Key">The <c>{key}</c> token substituted into ids, labels, titles, and expression texts.</param>
/// <param name="Values">The literal values, in source order; the carrying entry is multiplied once per value.</param>
/// <param name="Position">The document-absolute position of the axis key.</param>
public sealed record ForEachAxis(string Key, IReadOnlyList<string> Values, SourcePosition Position);
