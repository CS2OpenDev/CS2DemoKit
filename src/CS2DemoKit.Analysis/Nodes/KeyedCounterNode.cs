#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     The per-key bucket counter node (per-weapon
///     dimensions): a per-player accumulator holding one <see cref="double" /> bucket per observed
///     string key (e.g. weapon name). Trigger edges call <see cref="Add" /> with the evaluated
///     <c>key:</c> expression and a delta (+1 for <c>increment</c>, the <c>value:</c> expression
///     for <c>add</c>).
///     <para>
///         <b>Snapshot participation: none</b> (<see cref="ISnapshotExcludedNode" />). Dictionary
///         values don't fit the scalar <see cref="NodeSnapshot" /> model, so keyed counters sample
///         <b>per-game only</b>: <see cref="Output.KeyedStatsProjector" /> reads the live node after
///         evaluation. Consequently <c>reset: round</c> is rejected at load/build time — a per-round
///         keyed sample would silently read end-of-game state.
///     </para>
/// </summary>
public sealed class KeyedCounterNode(
    string ruleId,
    string name,
    string? subtitle = null,
    KeyedReduceMode reduceMode = KeyedReduceMode.Add)
    : StateNode, ISnapshotExcludedNode
{
    private readonly Dictionary<string, double> _buckets = new(StringComparer.Ordinal);

    /// <summary>
    ///     The per-key reduction this node applies (C8 named reducers). <see cref="KeyedReduceMode.Add" />
    ///     (the default) covers both <c>sum</c> and <c>count</c> — the v1 accumulate behavior — so a node
    ///     built without a mode is byte-identical to before C8.
    /// </summary>
    public KeyedReduceMode ReduceMode { get; } = reduceMode;

    /// <summary>
    ///     The rule id this node was materialized from. <see cref="StateNode.Name" /> is the
    ///     <em>display</em> name (<c>name:</c> falls back to the id) — projectors key output tables
    ///     off this id so table names stay stable when a display name is added later.
    /// </summary>
    public string RuleId { get; } = ruleId;

    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;

    /// <summary>Per-key accumulated totals, in insertion (first-observed) order.</summary>
    public IReadOnlyDictionary<string, double> Buckets => _buckets;

    /// <summary>Sum across all buckets — the value an unkeyed counter with the same triggers would hold.</summary>
    public double Total { get; private set; }

    /// <inheritdoc />
    /// <remarks>Active once any bucket exists (the first trigger fire).</remarks>
    public override bool IsActive => _buckets.Count > 0;

    /// <summary>Adds <paramref name="delta" /> to <paramref name="key" />'s bucket, creating it at 0 first.</summary>
    public void Add(string key, double delta)
    {
        _buckets.TryGetValue(key, out double current);
        _buckets[key] = current + delta;
        Total += delta;
    }

    /// <summary>
    ///     Drops every bucket and zeroes <see cref="Total" /> — back to the freshly-materialized
    ///     state (a keyed counter is always born empty; there is no seeded default to restore).
    ///     Called by the evaluator's match-restart reset.
    /// </summary>
    public void ResetForMatchRestart()
    {
        _buckets.Clear();
        Total = 0;
    }

    /// <summary>
    ///     Applies this node's <see cref="ReduceMode" /> to fold <paramref name="value" /> into
    ///     <paramref name="key" />'s bucket (C8 named reducers). This is the single writer the trigger
    ///     edges call:
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="KeyedReduceMode.Add" /> — accumulate (sum / count): delegates to
    ///             <see cref="Add" /> verbatim, so an <c>Add</c>-mode node is byte-identical to the v1 path.
    ///         </item>
    ///         <item>
    ///             <see cref="KeyedReduceMode.Min" /> / <see cref="KeyedReduceMode.Max" /> — keep the
    ///             smaller / larger; an <b>unseen</b> key takes the first value (min/max against a phantom 0
    ///             would corrupt an all-positive or all-negative series).
    ///         </item>
    ///         <item><see cref="KeyedReduceMode.Last" /> — overwrite with the newest value.</item>
    ///         <item><see cref="KeyedReduceMode.First" /> — write only when the key is unseen.</item>
    ///     </list>
    ///     <see cref="Total" /> tracks the running sum of the stored per-key values (its old invariant —
    ///     the value a like-triggered unkeyed counter would hold under <c>Add</c>), maintained by the
    ///     delta between the bucket's old and new value for every mode.
    /// </summary>
    public void Combine(string key, double value)
    {
        if (ReduceMode == KeyedReduceMode.Add)
        {
            Add(key, value);
            return;
        }

        bool seen = _buckets.TryGetValue(key, out double current);
        double next = ReduceMode switch
        {
            KeyedReduceMode.Min => seen ? Math.Min(current, value) : value,
            KeyedReduceMode.Max => seen ? Math.Max(current, value) : value,
            KeyedReduceMode.Last => value,
            KeyedReduceMode.First => seen ? current : value,
            _ => throw new InvalidOperationException($"unhandled keyed reduce mode: {ReduceMode}")
        };

        if (seen && next.Equals(current))
        {
            return; // no change (First on a seen key, or Min/Max that didn't move)
        }

        _buckets[key] = next;
        Total += next - current; // current is 0 for an unseen key (TryGetValue default)
    }

    /// <inheritdoc />
    /// <remarks>The cross-bucket total, invariant-culture <c>"0.##"</c> (per-key detail is not displayed).</remarks>
    public override string? GetDisplayValue() =>
        IsActive ? Total.ToString("0.##", CultureInfo.InvariantCulture) : null;

    /// <inheritdoc />
    public override float? GetNumericValue() => IsActive ? (float)Total : null;
}

/// <summary>
///     The per-key reduction a <see cref="KeyedCounterNode" /> applies (C8 named reducers,
///     <c>reduce:</c>). <see cref="Add" /> is the default and covers both the <c>sum</c> and
///     <c>count</c> author reducers (they differ only in the delta the edge supplies — a
///     <c>value:</c> amount vs. +1); the v1 <c>keyed_counter</c> path always uses it, so an
///     <c>Add</c>-mode node is byte-identical to before this enum existed.
/// </summary>
public enum KeyedReduceMode
{
    /// <summary>Accumulate the delta (the <c>sum</c> and <c>count</c> reducers). The default, v1-compatible.</summary>
    Add = 0,

    /// <summary>Keep the smallest value per key (the first value on an unseen key, never min-against-0).</summary>
    Min,

    /// <summary>Keep the largest value per key (the first value on an unseen key, never max-against-0).</summary>
    Max,

    /// <summary>Keep the most recent value per key (overwrite).</summary>
    Last,

    /// <summary>Keep the first value per key (write only when the key is unseen).</summary>
    First
}
