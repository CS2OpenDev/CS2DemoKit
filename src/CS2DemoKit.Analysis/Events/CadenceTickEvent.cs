#region

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Events;

/// <summary>
///     Synthesized by the evaluator for a ruleset's <c>cadences:</c> entry: one fire per period, on the
///     first frame at or after each due tick, after the frame's own messages and post-frame events
///     and after its per-pawn fold, so a <c>player.*</c> read on it is the frame's own state.
///     A stat reaches it as <c>cadence.&lt;id&gt;</c>, which the resolver lowers to the
///     <c>cadence_tick</c> view filtered on <see cref="Cadence" />. It binds to nobody, so an
///     each_player stat fires for every materialized player.
///     <para>
///         <b>All three clocks carry the frame tick</b>, the convention every synthesized event
///         follows (see <see cref="MolotovThrownEvent" />): <c>event.tick</c> is the frame clock.
///     </para>
/// </summary>
/// <param name="FrameNumber">The frame-clock tick of the frame it fired on (not a frame index).</param>
/// <param name="ServerTick">The same tick.</param>
/// <param name="GameTick">The same tick.</param>
/// <param name="Cadence">
///     The cadence's build-wide key, <c>&lt;id&gt;:&lt;ticks&gt;:&lt;anchor&gt;:&lt;until&gt;</c>
///     (<see cref="RulesetsV2.Model.CadenceDef.Key" />).
/// </param>
/// <param name="Step">The step since the anchor, 0 on the anchor's frame.</param>
public sealed record CadenceTickEvent(int FrameNumber, int ServerTick, int GameTick, string Cadence, int Step)
    : GameEvent("cadence_tick", -1, FrameNumber, ServerTick, GameTick)
{
    /// <inheritdoc />
    public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        [F("Cadence", Cadence), F("Step", Step)];
}
