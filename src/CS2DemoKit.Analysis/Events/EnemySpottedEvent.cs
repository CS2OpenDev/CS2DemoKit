#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized per-transition event emitted by <c>VisibilityTransitionScanner</c> the tick a
///     directed enemy pair crosses from not-visible to visible: "this enemy just came into
///     <paramref name="ViewerSlot" />'s view". CS2 has no wire event for this. The demo's own
///     <c>spotted</c> bit is team radar, not what the player can see, and
///     <c>VisibilityAnalyzer.Analyze</c> computes the per-tick booleans only to throw them away and
///     accumulate seconds, so nothing in its report carries a tick.
///     <para>
///         Visibility here is <b>could-see</b>, not merely exposed: a clear line of sight to some body
///         anchor AND that anchor inside the viewer's frustum AND the sightline not through active
///         smoke. Geometric line of fire without the viewer looking at it is not a spot.
///     </para>
///     <para>
///         Routed to rules exactly like a parsed game event (wrapped in
///         <c>GameEventMessage.ForSynthesizedEvent</c>, dispatched by runtime type), so a YAML rule can
///         trigger on the <c>enemy_spotted</c> view and read the two slots and the angle.
///     </para>
///     <para>
///         <b>All three clocks carry the same value, and that is correct here.</b> The scanner is
///         driven off <c>DemoFrame.ServerTick</c>, which is ALREADY the frame clock: a parsed
///         event's own <c>ServerTick</c> counts from the server's boot and is reduced by
///         <c>ServerStartTick</c> to reach the same frame clock (measured on the bundled sample:
///         event tick 30,176 against frame tick 9,720). So a spot's tick is directly comparable
///         with a parsed event's <c>GameTick</c> as-is. Subtracting <c>ServerStartTick</c> here
///         would double-apply it and push early spots negative, which is not a visible failure: it
///         silently fails every <c>LastSpotTick &gt;= 0</c> guard and empties the spot-to-shot
///         population instead of erroring.
///     </para>
///     <para>
///         The two clocks agree to the tick, not exactly. Measured on the same sample
///         (<c>ServerStartTick</c> 20,457): of 3,006 parsed events, 2,194 have a <c>GameTick</c>
///         equal to the header tick of the frame that delivered them and 812 sit one tick below it
///         (<c>weapon_fire</c> 195 of 206, <c>player_death</c> 21 of 22, <c>player_hurt</c> 55 of
///         76), because the server stamps an event during its simulation and the frame that carries
///         it can be the next one. That is event timing, not an offset in <c>ServerStartTick</c>: a
///         synthesized event has no such stamp and lands exactly on its frame. A rule comparing a
///         spot tick against a shot tick therefore sees the shot's <c>GameTick</c> up to one tick
///         early, which the <c>ticks_since_*</c> enrichments absorb (a difference of -1 rounds to
///         "this tick", never to a sentinel).
///     </para>
///     <para>
///         <c>FrameNumber</c> also carries the frame tick, not the frame INDEX the base
///         <see cref="GameEvent" /> documents: the scanner has no index in hand where it emits, and
///         nothing in the graph reads <c>FrameNumber</c> off an event (only a breakpoint transport
///         read reaches it).
///     </para>
/// </summary>
/// <param name="FrameNumber">The sampled frame-clock tick (see the clock note above; not a frame index).</param>
/// <param name="ServerTick">The same tick.</param>
/// <param name="GameTick">
///     The same tick. Directly comparable with a parsed event's <c>GameTick</c>.
/// </param>
/// <param name="ViewerSlot">The player who can now see the target. The view binds its actor slot to this.</param>
/// <param name="TargetSlot">The enemy who became visible.</param>
/// <param name="AngleToChestDeg">
///     Degrees between the viewer's eye ray and the ray from that eye to the target's CHEST anchor
///     (48 units above the target's feet, scaled by their duck amount). Chest is
///     <c>PlayerVantage.BuildAnchors</c>' first anchor and the one most stable across crouch
///     transitions, so it is the anchor a preaim number can be compared against between demos. This is
///     the instantaneous crosshair error at first contact: the preaim metric reads it directly.
/// </param>
/// <param name="ViewerPitchDeg">
///     Where the viewer's crosshair was pointing at the instant of contact, pitch in degrees.
/// </param>
/// <param name="ViewerYawDeg">
///     The yaw half of the same instant.
///     <para>
///         Carried ON THE EVENT rather than left for a rule to capture, because the pairing a
///         crosshair-travel metric needs is "this spot and the shot that answered it", and a YAML
///         capture cannot express that: <c>keep: first</c> pins the round's first contact and
///         <c>keep: last</c> settles to its last, so neither is the spot immediately preceding a
///         given shot. With the angles on the event the engine can latch them per viewer and the
///         travel becomes a property of the shot.
///     </para>
/// </param>
public sealed record EnemySpottedEvent(
    int FrameNumber,
    int ServerTick,
    int GameTick,
    int ViewerSlot,
    int TargetSlot,
    float AngleToChestDeg,
    float ViewerPitchDeg,
    float ViewerYawDeg)
    : GameEvent("enemy_spotted", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
    [
        F("ViewerSlot", ViewerSlot), F("TargetSlot", TargetSlot),
        F("AngleToChestDeg", AngleToChestDeg),
        F("ViewerPitchDeg", ViewerPitchDeg), F("ViewerYawDeg", ViewerYawDeg)
    ];
}
