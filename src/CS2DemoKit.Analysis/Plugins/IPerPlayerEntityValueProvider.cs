#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Reads a single networked field off a per-player entity (typically <c>CCSPlayerPawn</c>),
///     parameterised by player slot. Distinct from <see cref="IEntityValueProvider" /> because
///     the consumer pattern differs fundamentally:
///     <list type="bullet">
///         <item>
///             <see cref="IEntityValueProvider" /> targets a singleton entity (e.g. CCSGameRules).
///             The scanner POLLS it every frame and SYNTHESIZES change events on transitions.
///         </item>
///         <item>
///             <see cref="IPerPlayerEntityValueProvider" /> targets per-player entities. Consumers
///             READ the value at the moment of a player-scoped event (e.g. <c>player_hurt</c>),
///             using the event's <c>VictimSlot</c>/<c>AttackerSlot</c> to address the right pawn.
///             No synthesized events — pull model, not push.
///         </item>
///     </list>
///     <para>
///         The scanner maintains a <b>pre-frame snapshot</b> of each registered provider's value
///         per active player slot. Consumers read the snapshot via
///         <c>EntityChangeScanner.GetPreFrameValue</c>; the snapshot reflects the PREVIOUS frame's
///         state, which is the right value for "pre-event" reads — entity-state at the event's own
///         tick has already been updated by the wire-co-located PacketEntities.
///     </para>
/// </summary>
public interface IPerPlayerEntityValueProvider
{
    /// <summary>The CS2 entity class name to read from (e.g. <c>"CCSPlayerPawn"</c>).</summary>
    string EntityClass { get; }

    /// <summary>Networked field path on the entity (e.g. <c>SchemaNames.CBaseEntity.Health</c>).</summary>
    string FieldName { get; }

    /// <summary>
    ///     Stable name used for diagnostics and snapshot keying. Not exposed in YAML
    ///     (no per-player expression syntax yet); kept symmetric with <see cref="IEntityValueProvider" />
    ///     so future YAML extensions can resolve providers by name.
    /// </summary>
    string Name { get; }

    /// <summary>Runtime C# type of the value (e.g. <c>typeof(int)</c>).</summary>
    Type ValueType { get; }

    /// <summary>
    ///     Single-pass batch capture: invoke <paramref name="emit" /> once per live (slot, value)
    ///     pair discovered by walking the layer's entity table. Used by
    ///     <c>EntityValueCache</c> (breakpoint pre-warm) to populate a per-frame snapshot
    ///     in O(entities) rather than O(slots × entities). Implementations should skip slots
    ///     with no resolvable entity (don't emit nulls).
    ///     <para>
    ///         Each implementation walks the entity set itself via <see cref="PawnLookup.ForEachLivePawn" />.
    ///         On the load-time hot path (<c>EntityChangeScanner.CapturePreFrameSnapshot</c>) the
    ///         scanner walks the set <b>once</b> and dispatches to <see cref="ReadForPawn" /> for every
    ///         provider instead, so the set is not re-swept per provider.
    ///     </para>
    /// </summary>
    void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit);

    /// <summary>
    ///     Reads this provider's value off an already-resolved pawn wrapper, returning the value
    ///     to snapshot or <c>null</c> to emit nothing for this (provider, slot). This is the
    ///     per-pawn unit of work shared between <see cref="CaptureAllSlots" /> and the scanner's
    ///     single combined sweep, letting one <see cref="CSPlayerPawn" /> wrapper (the SDK-emitted
    ///     type since the SDK cutover, bound via <c>SdkEntityWorlds.Wrap</c>) and one entity-set
    ///     walk serve all per-player providers.
    ///     <para>
    ///         <b>Emit gate is part of the contract:</b> a provider that treats <c>0</c> (or any
    ///         sentinel) as "absent" must return <c>null</c> here exactly as its old
    ///         <see cref="CaptureAllSlots" /> body did — the snapshot semantics are unchanged.
    ///         <paramref name="tracker" /> is supplied for providers that need a second hop
    ///         (e.g. resolving an active-weapon handle to a weapon entity).
    ///     </para>
    /// </summary>
    object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn);

    /// <summary>
    ///     Reads the value for a player slot at the layer's current tick. Returns <c>null</c>
    ///     when the entity for that slot does not exist (e.g. before spawn, after disconnect).
    ///     <para>
    ///         <b>Timing:</b> the layer is expected to be advanced to the requested tick before
    ///         this method is called. Reading <c>m_iHealth</c> AT a <c>player_hurt</c> tick yields
    ///         POST-damage HP (the entity update arrives in the same packet as the event). Use
    ///         <c>EntityChangeScanner.GetPreFrameValue</c> for PRE-event reads.
    ///     </para>
    /// </summary>
    object? Read(EntityStateLayer layer, int playerSlot);
}
