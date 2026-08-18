using System.Globalization;

namespace CS2DemoKit.Parser.GameEvents;

/// <summary>
///     A decoded CS2 game event: the per-fire transport context, plus the typed payload record
///     the SDK materialised for it.
/// </summary>
/// <remarks>
///     <para>
///         This used to be an abstract base with 272 generated subtypes, one per event. The
///         payload records now come from <c>CS2OpenDev.Sdk.GameEvents</c>
///         (<c>CS2OpenSchema.Events</c>), which models exactly what the schema declares and
///         nothing else — so the transport fields that are properties of the <em>fire</em> rather
///         than of the event live out here instead.
///     </para>
///     <para>
///         The SDK's own wrapper for this is <c>GameEventEnvelope&lt;T&gt;</c>, which is generic.
///         A demo's event stream is heterogeneous, so it needs a single non-generic type to sit in
///         one list; this is that type, with <see cref="Payload" /> typed as <see cref="object" />.
///         Pattern-match it to get at a specific event:
///     </para>
///     <code>
///         if (evt.Payload is PlayerDeathEvent death) { … }
///     </code>
///     <para>
///         Not sealed: the Analysis layer derives synthesized events (entity-derived fires that
///         never appeared on the wire, e.g. <c>molotov_thrown</c>) which carry no SDK payload and
///         supply their own <see cref="GetDecodedFields" />.
///     </para>
/// </remarks>
/// <param name="Name">Native event name, e.g. <c>player_death</c>.</param>
/// <param name="EventId">Wire <c>eventid</c> of the originating fire. Not stable across demos.</param>
/// <param name="FrameNumber">Demo frame index the event fired in.</param>
/// <param name="ServerTick">Absolute server tick.</param>
/// <param name="GameTick">Frame-clock tick (<c>ServerTick - ServerStartTick</c>).</param>
/// <param name="Payload">
///     The SDK record for this event, or <see langword="null" /> for a synthesized event or one
///     the SDK build predates.
/// </param>
public record GameEvent(
    string Name,
    int EventId,
    int FrameNumber,
    int ServerTick,
    int GameTick,
    object? Payload = null)
{
    private IReadOnlyList<(string, string, string)>? _decoded;

    /// <summary>
    ///     Field name / formatted value / wire type, for display and for the rules layer's
    ///     <c>event.&lt;Field&gt;</c> resolution.
    /// </summary>
    /// <remarks>
    ///     Projected from <see cref="Payload" /> by reflection, cached per instance. Names are the
    ///     SDK's property names — <c>Userid</c>, <c>Attacker</c> — not the semantic role names
    ///     (<c>VictimSlot</c>, <c>KillerSlot</c>) the retired generator used to emit. The wire type
    ///     comes from the SDK's <c>[GameEventFieldType]</c> attribute, which carries the original
    ///     KV1 tag.
    /// </remarks>
    public virtual IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
        _decoded ??= GameEventFieldProjector.Project(Payload);

    /// <summary>
    ///     The semantic meaning of each enrichable field — which ints are controller slots, which
    ///     are entity indices.
    /// </summary>
    public virtual IReadOnlyList<(string Field, FieldSemantic Kind)> GetFieldSemantics() =>
        GameEventSemantics.Derive(this);

    // Field-tuple shorthand for subclasses that hand-list their fields instead of inheriting the
    // projector. That is every subclass with no SDK payload: synthesized events, and the handful of
    // wire events missing from the upstream schema (see GameEventSupplementary). They cannot fall
    // through to GetDecodedFields() above, and neither obvious shortcut works —
    // Project(Payload) sees null and silently returns an empty field list, while projecting `this`
    // would sweep Name/EventId/FrameNumber/ServerTick/GameTick in alongside the real fields.
    //
    // Only the overloads those subclasses actually use exist; add one when a subclass needs it.
    protected static (string, string, string) F(string n, int v) =>
        (n, v.ToString(CultureInfo.InvariantCulture), "int");

    protected static (string, string, string) F(string n, string v) =>
        (n, $"\"{v}\"", "string");
}
