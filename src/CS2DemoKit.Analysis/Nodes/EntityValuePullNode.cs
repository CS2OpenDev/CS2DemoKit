#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A per-player entity-value PULL-node (B6-style — see <see cref="RoundTeamAggregateNode" />) that
///     exposes the SUBJECT slot's current per-player entity-provider value (e.g.
///     <c>player.entity.pawn.health</c>) as a reflectively-read <see cref="Value" />, recomputed live on
///     every read from the shared <see cref="EntityChangeScanner" />'s pre-frame snapshot
///     (<see cref="EntityChangeScanner.GetPreFrameValue" /> — the very accessor the fire-time
///     <c>where:</c>/value-selector entity seam uses). It closes the last entity-read gap: <c>compute:</c>
///     (round-end / live-settle) and <c>flag: when:</c> (flag-eval) are pure node-logic sites with no
///     event frame, so they cannot reach the fire-time <see cref="Building.ExpressionCompiler.CompileEventCondition" />
///     entity seam. Materializing the subject's entity value as this always-active pull-node lets those
///     sites read it as an ordinary graph-node value (a reflective <c>Value</c>), exactly as the B6
///     aggregate nodes are read.
///     <para>
///         TIMING: the read is at SETTLE time — the entity state captured at the most recent frame
///         advance (the round-end state for a round-end <c>compute:</c>, the flag-evaluation state for a
///         <c>when:</c>) — distinct from the AT-FIRE timing a <c>where:</c>/value-selector read correctly
///         uses. Both are valid; the timing follows the site. Because <see cref="EntityChangeScanner" />
///         snapshots every live slot on every frame advance, at round-end this pre-frame value is the
///         round-end value.
///     </para>
///     <para>
///         Excluded from snapshots (<see cref="ISnapshotExcludedNode" />): a derived context value,
///         invisible in output. A not-yet-snapshotted slot (pre-spawn, or the very first frame) degrades
///         to the provider default (0) — never throws at read. A missing scanner is a BUILD-time error
///         (mirrors the <c>where:</c> entity seam's compile-time "requires per-player entity providers"
///         throw), raised where this node is materialized, not here at read.
///     </para>
/// </summary>
public sealed class EntityValuePullNode : StateNode, ISnapshotExcludedNode
{
    private readonly IPerPlayerEntityValueProvider _provider;
    private readonly EntityChangeScanner _scanner;
    private readonly int _slot;

    /// <summary>Creates a per-subject entity pull-node.</summary>
    /// <param name="name">The node's unique name.</param>
    /// <param name="scanner">The scanner whose per-slot pre-frame snapshot backs the read.</param>
    /// <param name="provider">The per-player provider whose value is read for the subject slot.</param>
    /// <param name="slot">The subject player slot this pull-node is relative to.</param>
    /// <param name="subtitle">Optional display subtitle (the player name).</param>
    public EntityValuePullNode(string name, EntityChangeScanner scanner,
        IPerPlayerEntityValueProvider provider, int slot, string? subtitle = null)
    {
        Name = name;
        _scanner = scanner;
        _provider = provider;
        _slot = slot;
        Subtitle = subtitle;
    }

    /// <inheritdoc />
    public override bool IsActive => true;

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <summary>The subject slot's live entity value as a double, read reflectively by the expression compiler.</summary>
    public double Value => Read();

    /// <inheritdoc />
    public override string? GetDisplayValue() => Read().ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override float? GetNumericValue() => (float)Read();

    private double Read()
    {
        // The scanner snapshots every live slot per frame; a slot not yet populated (pre-spawn, or the
        // very first frame) has no snapshot entry and reads the provider default (0). Never throws at
        // read — the provider was gated into the scanner's snapshot set at build time
        // (RuleChainBuilder.UnionV2EntityReads), so GetPreFrameValue's not-registered loud arm is
        // unreachable for a pull-node the planner materialized.
        object? raw = _scanner.GetPreFrameValue(_provider, _slot);
        return raw switch
        {
            int i => i,
            float f => f,
            double d => d,
            long l => l,
            short s => s,
            byte by => by,
            bool b => b ? 1.0 : 0.0,
            _ => 0.0
        };
    }
}
