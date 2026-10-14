#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by <c>EntityChangeScanner</c> once per grenade projectile, one of the five
///     <see cref="Parser.EntityTracking.GrenadeProjectileClasses" />, on the first frame its thrower
///     resolves through <c>m_hThrower</c> (pawn, controller, slot). That is usually the frame the
///     projectile appears; when the handle has not settled there, the event waits for the frame it
///     does, the rule <see cref="MolotovThrownEvent" /> follows. A projectile whose thrower never
///     resolves gets no <c>projectile_created</c>, only a <see cref="ProjectileRemovedEvent" /> with
///     <c>PlayerSlot = -1</c>. A <c>CMolotovProjectile</c> fires this on the same frame and with the
///     same slot as <c>molotov_thrown</c>.
///     <para>
///         All three clocks carry the frame tick, the convention of every event the scanner
///         synthesizes (see <see cref="MolotovThrownEvent" />): <c>event.tick</c> on this event is
///         the frame clock, the value <c>event.frame_tick</c> reads.
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick the event fires on (not a frame index).</param>
/// <param name="ServerTick">The same tick; see the clock note above.</param>
/// <param name="GameTick">The same tick.</param>
/// <param name="PlayerSlot">The thrower's player slot, 0-63. Never -1 on this event.</param>
/// <param name="EntityIndex">The projectile's entity index. Reused by a later entity once the projectile is removed.</param>
/// <param name="Serial">The entity serial; <c>(EntityIndex, Serial)</c> names one projectile for its whole life.</param>
/// <param name="ClassName">The projectile's server class, such as <c>CSmokeGrenadeProjectile</c>.</param>
/// <param name="X">
///     World X on the frame the projectile first appeared, which is the release point to within a
///     tick even when the event itself waited for the thrower. NaN when the position was unreadable.
/// </param>
/// <param name="Y">World Y, as <paramref name="X" />.</param>
/// <param name="Z">World Z, as <paramref name="X" />.</param>
public sealed record ProjectileCreatedEvent(
    int FrameNumber,
    int ServerTick,
    int GameTick,
    int PlayerSlot,
    int EntityIndex,
    int Serial,
    string ClassName,
    float X,
    float Y,
    float Z)
    : GameEvent("projectile_created", -1, FrameNumber, ServerTick, GameTick)
{
    /// <summary>
    ///     The grenade's short name: <c>smoke</c>, <c>molotov</c> (incendiaries included), <c>he</c>,
    ///     <c>flash</c> or <c>decoy</c>. What the views' <c>class</c> facet matches on.
    /// </summary>
    public string Grenade => ProjectileGrenadeName.Of(ClassName);

    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
    [
        F("PlayerSlot", PlayerSlot), F("EntityIndex", EntityIndex), F("Serial", Serial),
        F("ClassName", ClassName), F("Grenade", Grenade), F("X", X), F("Y", Y), F("Z", Z)
    ];
}
