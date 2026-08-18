#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized per-throw event emitted by <c>EntityChangeScanner</c> when a
///     <c>CMolotovProjectile</c> entity is created. Molotov/incendiary detonation has no usable
///     single-fire game event in GOTV demos — <c>molotov_detonate</c> is never emitted, and
///     <c>inferno_startburn</c> carries no thrower — so the thrower is attributed from the
///     projectile's <c>m_hThrower</c> handle (pawn → controller → slot). Routed to rules exactly
///     like a parsed game event (wrapped in <c>GameEventMessage.ForSynthesizedEvent</c>,
///     dispatched by runtime type), so a YAML rule can trigger <c>on: molotov_thrown</c> and read
///     <c>event.PlayerSlot</c>. <c>PlayerSlot</c> is the only field surfaced to the expression
///     layer (the base <see cref="GameEvent" /> members are excluded by the registry's accessor
///     builder).
/// </summary>
public sealed record MolotovThrownEvent(int FrameNumber, int ServerTick, int GameTick, int PlayerSlot)
    : GameEvent("molotov_thrown", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("PlayerSlot", PlayerSlot)];
}
