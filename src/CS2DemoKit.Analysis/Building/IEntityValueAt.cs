namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     A positioned-per-fire-frame entity read for EDGE breakpoint conditions. The caller positions the
///     accessor at a fire frame's PRE-FRAME entity state before invoking the compiled predicate, then
///     <see cref="GetValue" /> returns a per-player provider's value for a slot at that frame.
///     <para>
///         The compiled predicate for a condition like <c>VictimSlot.entity.pawn.health &lt; 20</c> calls
///         <see cref="GetValue" /> with the provider name (<c>entity.pawn.health</c>) and a slot resolved
///         from the event payload (the victim's slot). <c>null</c> means the entity is absent at that
///         frame (pre-spawn, disconnected, or — for a dead pawn read at its own death frame — filtered
///         out); the compiler coalesces it to the provider's default before the comparison, exactly as
///         the per-player-chain <c>player.entity.*</c> path does.
///     </para>
/// </summary>
public interface IEntityValueAt
{
    /// <summary>The provider's boxed value for <paramref name="slot" /> at the positioned frame, or null.</summary>
    object? GetValue(string providerName, int slot);
}
