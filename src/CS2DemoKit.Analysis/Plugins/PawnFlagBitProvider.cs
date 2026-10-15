#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     One bit of the pawn's <c>m_fFlags</c> as a bool column, so a rule tests
///     <c>player.on_ground</c> instead of a mask the rules language has no operator for. No value
///     when <c>m_fFlags</c> is unseen, the same as <see cref="BuiltinProviderSpecs.PawnFlags" />.
/// </summary>
public sealed class PawnFlagBitProvider
    : IPerPlayerEntityValueProvider, IWorkerCloneable<IPerPlayerEntityValueProvider>, IPawnIntCellReader,
        IDerivedPawnValue
{
    /// <summary><c>FL_ONGROUND</c>.</summary>
    public const int OnGroundBit = 1 << 0;

    /// <summary><c>FL_DUCKING</c>.</summary>
    public const int DuckingBit = 1 << 1;

    // Holds lane cursors, so one per worker.
    private readonly GenericPerPlayerFieldProvider _flags = new(BuiltinProviderSpecs.PawnFlags);

    /// <summary>Creates the column <paramref name="name" /> over the bits in <paramref name="mask" />.</summary>
    /// <param name="name">The provider name.</param>
    /// <param name="mask">True when any of these bits is set.</param>
    public PawnFlagBitProvider(string name, int mask)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Mask = mask;
    }

    /// <summary><c>entity.pawn.on_ground</c>.</summary>
    public static PawnFlagBitProvider OnGround() => new("entity.pawn.on_ground", OnGroundBit);

    /// <summary><c>entity.pawn.ducking</c>.</summary>
    public static PawnFlagBitProvider Ducking() => new("entity.pawn.ducking", DuckingBit);

    /// <summary>The bits tested.</summary>
    public int Mask { get; }

    /// <inheritdoc />
    public string EntityClass => _flags.EntityClass;

    /// <inheritdoc />
    public string FieldName => _flags.FieldName;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Type ValueType => typeof(bool);

    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(emit);
        EntityTracker tracker = layer.Tracker;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            if (ReadForPawn(tracker, SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!) is { } value)
            {
                emit(slot, value);
            }
        });
    }

    /// <inheritdoc />
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) =>
        _flags.ReadForPawn(tracker, pawn) is int flags ? (flags & Mask) != 0 : null;

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        ArgumentNullException.ThrowIfNull(layer);
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        return pawn is null ? null : ReadForPawn(layer.Tracker, SdkEntityWorlds.Wrap<CSPlayerPawn>(layer.Tracker, pawn)!);
    }

    /// <inheritdoc />
    public bool TryReadInt(PawnReadContext context, out int value)
    {
        bool has = _flags.TryReadInt(context, out int flags);
        value = has && (flags & Mask) != 0 ? 1 : 0;
        return has;
    }

    /// <inheritdoc />
    public IPerPlayerEntityValueProvider CloneForWorker() => new PawnFlagBitProvider(Name, Mask);
}
