namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Marks a node that must never enter tracked snapshot vectors. The evaluator's snapshot
///     bookkeeping skips these nodes when appending newly materialized per-player nodes, so they
///     never receive a snapshot column — consumers read them <em>live</em> after evaluation instead.
///     <para>
///         Two families implement this: transient nodes (<see cref="ITransientNode" /> — reset
///         before every dispatch, so a per-message row would only ever show defaults) and
///         keyed-counter nodes (dictionary-valued buckets that don't fit the scalar
///         <see cref="NodeSnapshot" /> model; they sample per-game only, read live at
///         end of eval).
///     </para>
/// </summary>
public interface ISnapshotExcludedNode;
