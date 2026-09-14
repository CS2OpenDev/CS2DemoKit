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
///     <para>
///         <b>All three clocks carry the frame tick, the same convention as
///         <see cref="EnemySpottedEvent" />, and for the same reason.</b> The scanner that emits
///         this runs off <c>DemoFrame.ServerTick</c>, which is already the frame clock, and it has
///         no <c>ServerStartTick</c> in hand to put the absolute server tick in the slot a parsed
///         event fills with one. Every event the scanner synthesizes therefore carries the frame
///         clock in all three slots, so <c>GameTick</c> is directly comparable with a parsed event's
///         <c>GameTick</c>. The cost is the <c>event.tick</c> alias, which resolves to
///         <c>ServerTick</c>: on this view it is the frame clock, on <c>kill</c> or
///         <c>bomb_planted</c> it is the absolute server clock, and differencing the two adds the
///         demo's <c>ServerStartTick</c> (about 20,000 ticks on the bundled sample) to the answer
///         with no error. Compare <c>GameTick</c> against <c>GameTick</c> instead. No shipped
///         ruleset differences a molotov tick in YAML (the view is used as a counter and a
///         weapon-name filter), but the engine's own clip-start edge reads one: every
///         <c>count:</c> stat gets a first-tick edge that stamps <c>evt.GameTick</c> into the
///         highlight's clip start (<c>RecordFirstEventTickEdge</c>), and <c>ClipWindows</c> takes
///         that stamp as frame clock. The shipped player_stats <c>molotov_used</c> is such a count,
///         so the convention is load-bearing there today.
///     </para>
///     <para>
///         <c>FrameNumber</c> also carries the frame tick, not the frame INDEX the base
///         <see cref="GameEvent" /> documents: the scanner has no index in hand where it emits, and
///         nothing in the graph reads <c>FrameNumber</c> off an event (only a breakpoint transport
///         read reaches it). Same divergence as <see cref="EnemySpottedEvent" />, whose doc also
///         records the measured one-tick jitter between a parsed event's <c>GameTick</c> and the
///         frame that delivered it.
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick the projectile was first seen on (not a frame index).</param>
/// <param name="ServerTick">The same tick (see the clock note above), not the absolute server tick.</param>
/// <param name="GameTick">The same tick. Directly comparable with a parsed event's <c>GameTick</c>.</param>
/// <param name="PlayerSlot">The thrower, resolved from <c>m_hThrower</c>.</param>
public sealed record MolotovThrownEvent(int FrameNumber, int ServerTick, int GameTick, int PlayerSlot)
    : GameEvent("molotov_thrown", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("PlayerSlot", PlayerSlot)];
}
