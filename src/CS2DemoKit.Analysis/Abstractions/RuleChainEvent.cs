namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Records a single satisfaction of a <see cref="ConjunctionNode" /> — emitted once per rising edge
///     (transition from unsatisfied to satisfied).
/// </summary>
/// <param name="ChainName">The name of the rule chain that was satisfied.</param>
/// <param name="FrameIndex">Zero-based index of the frame in which the chain was satisfied.</param>
/// <param name="Tick">Server tick of the frame in which the chain was satisfied.</param>
/// <param name="PlayerSlot">
///     Slot of the player whose materialized chain instance was satisfied, or <c>null</c> for
///     game-scoped chains (which have no owning player).
/// </param>
/// <param name="PlayerName">
///     Name of the player whose materialized chain instance was satisfied (as resolved at
///     materialization time), or <c>null</c> for game-scoped chains.
/// </param>
public sealed record RuleChainEvent(
    string ChainName,
    int FrameIndex,
    int Tick,
    int? PlayerSlot = null,
    string? PlayerName = null);
