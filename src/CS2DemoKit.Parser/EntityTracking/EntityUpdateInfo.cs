namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Describes a single entity update record decoded from a <c>svc_PacketEntities</c>
///     entity_data bit stream. Used for display purposes — does not mutate live entity state.
/// </summary>
public sealed class EntityUpdateInfo
{
    /// <summary>Kind of entity update encoded in a <c>svc_PacketEntities</c> record.</summary>
    public enum UpdateType
    {
        /// <summary>Entity entering the PVS — baseline + initial field state.</summary>
        Enter,

        /// <summary>Entity leaving the PVS — no fields included.</summary>
        Leave,

        /// <summary>Delta update on an existing entity — only changed fields.</summary>
        Delta
    }

    /// <summary>Schema class name of the updated entity.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>Entity index inside the demo's entity table.</summary>
    public int EntityIndex { get; init; }

    /// <summary>
    ///     Fields decoded in this update.
    ///     For Enter: baseline + initial fields.
    ///     For Delta: only the changed fields.
    ///     For Leave: empty.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>The kind of update (Enter / Leave / Delta).</summary>
    public UpdateType Kind { get; init; }

    /// <summary>Serial number from the entity's wire encoding — uniquely identifies entity lifetime.</summary>
    public int Serial { get; init; }
}
