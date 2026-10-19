#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A player's live team (2 = T, 3 = CT, 0 or 1 otherwise), read from
///     <see cref="PlayerContextIndex" /> on every read: the node <c>player.team</c> resolves to where a
///     rule reads it through a node rather than an event condition (<c>while:</c>, <c>when:</c>,
///     <c>compute:</c>, a captured value), and the team an event-table row reports. The index stays the
///     one store; this node only exposes it.
/// </summary>
internal sealed class PlayerTeamNode(string name, PlayerContextIndex index, int slot, string? subtitle = null)
    : StateNode
{
    /// <inheritdoc />
    public override bool IsActive => true;

    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;

    /// <summary>The slot this node reads.</summary>
    public int Slot { get; } = slot;

    /// <summary>The live team, read reflectively by the expression compiler.</summary>
    public int Value => index.GetCurrentTeam(Slot);

    /// <inheritdoc />
    public override string? GetDisplayValue() => Value.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override float? GetNumericValue() => Value;
}
