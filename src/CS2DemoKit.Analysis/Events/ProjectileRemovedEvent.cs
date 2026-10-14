#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by <c>EntityChangeScanner</c> once per grenade projectile, on the first frame its
///     entity slot is empty or holds another entity. That is after detonation for an HE grenade, a
///     flashbang or a molotov, and only once the cloud or the sound ends for a smoke or a decoy. The
///     fields are the projectile as last seen, so the position is where it came to rest or burst.
///     Every projectile that was seen is removed, including one whose thrower never resolved.
///     <para>
///         All three clocks carry the frame tick, the convention of every event the scanner
///         synthesizes (see <see cref="MolotovThrownEvent" />).
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick the projectile was first missing on (not a frame index).</param>
/// <param name="ServerTick">The same tick; see the clock note above.</param>
/// <param name="GameTick">The same tick.</param>
/// <param name="PlayerSlot">
///     The thrower's player slot, 0-63, held from its first resolution, or -1 when it never resolved
///     in the projectile's life.
/// </param>
/// <param name="EntityIndex">The projectile's entity index.</param>
/// <param name="Serial">The entity serial; <c>(EntityIndex, Serial)</c> names one projectile for its whole life.</param>
/// <param name="ClassName">The projectile's server class, such as <c>CSmokeGrenadeProjectile</c>.</param>
/// <param name="X">World X on the last frame the projectile was seen. NaN when that read was unreadable.</param>
/// <param name="Y">World Y, as <paramref name="X" />.</param>
/// <param name="Z">World Z, as <paramref name="X" />.</param>
/// <param name="Bounces"><c>m_nBounces</c> as last seen, or 0 when it was never networked.</param>
public sealed record ProjectileRemovedEvent(
    int FrameNumber,
    int ServerTick,
    int GameTick,
    int PlayerSlot,
    int EntityIndex,
    int Serial,
    string ClassName,
    float X,
    float Y,
    float Z,
    int Bounces)
    : GameEvent("projectile_removed", -1, FrameNumber, ServerTick, GameTick)
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
        F("ClassName", ClassName), F("Grenade", Grenade), F("X", X), F("Y", Y), F("Z", Z),
        F("Bounces", Bounces)
    ];
}
