#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Typed read of an <c>int</c> column off a slot's <c>CCSPlayerController</c> rather than its
///     pawn. The digest reads such a column for every slot with a controller, including a slot with
///     no player pawn bound, which a pawn-rooted column never sees.
/// </summary>
public interface IControllerIntCellReader
{
    /// <summary>Reads the cell off <paramref name="controller" />, the slot's identity-checked controller.</summary>
    /// <param name="controller">The <c>CCSPlayerController</c> at entity index slot + 1.</param>
    /// <param name="value">The cell value, or 0 when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> when the controller has a value; <c>false</c> for "no value".</returns>
    bool TryReadInt(EntityState controller, out int value);
}

/// <summary>
///     A per-player <c>int</c> read of one field on the slot's <c>CCSPlayerController</c>, found by
///     entity index (slot + 1) and identity-checked, never through a pawn's <c>m_hController</c>.
///     A slot whose controller has no <c>CCSPlayerPawn</c> (a coach, a spectator) still reads.
/// </summary>
public sealed class ControllerFieldProvider
    : IPerPlayerEntityValueProvider, IPawnStateReader, IPawnIntCellReader, IControllerIntCellReader,
        IWorkerCloneable<IPerPlayerEntityValueProvider>
{
    /// <summary>The controller class every read is identity-checked against.</summary>
    public const string ControllerClass = "CCSPlayerController";

    private const int MaxSlots = 64;

    /// <param name="spec">A direct <c>int</c> read on <see cref="ControllerClass" />, with no handle hop.</param>
    /// <exception cref="ArgumentException">The spec is not a direct int read on the controller.</exception>
    public ControllerFieldProvider(ProviderSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.EntityClass != ControllerClass || spec.ValueType != typeof(int)
                                                || spec.ViaHandleToClassName is not null || spec.ViaHandleToField is not null
                                                || spec.PositiveOnly || spec.UnseenAsDefault)
        {
            throw new ArgumentException(
                $"provider spec '{spec.Name}': a controller read is a direct int field on {ControllerClass}, "
                + "with no handle hop and no emit knobs.", nameof(spec));
        }

        Spec = spec;
    }

    /// <summary>The spec this provider reads.</summary>
    public ProviderSpec Spec { get; }

    /// <inheritdoc />
    public string EntityClass => ControllerClass;

    /// <inheritdoc />
    public string FieldName => Spec.Path;

    /// <inheritdoc />
    public string Name => Spec.Name;

    /// <inheritdoc />
    public Type ValueType => typeof(int);

    /// <summary>The slot's controller at entity index slot + 1, or null when that index holds no controller.</summary>
    public static EntityState? ResolveController(EntityTracker tracker, int playerSlot)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        return playerSlot is >= 0 and < MaxSlots
               && tracker.CurrentEntities[playerSlot + 1] is { ClassName: ControllerClass } controller
            ? controller
            : null;
    }

    /// <inheritdoc />
    public bool TryReadInt(EntityState controller, out int value)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (controller.TryGet<int>(Spec.Path) is int read)
        {
            value = read;
            return true;
        }

        value = 0;
        return false;
    }

    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(emit);
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            if (Read(layer.Tracker, slot) is int value)
            {
                emit(slot, value);
            }
        }
    }

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return Read(layer.Tracker, playerSlot);
    }

    /// <inheritdoc />
    public object? ReadForPawnState(EntityTracker tracker, EntityState pawn) =>
        Read(tracker, SlotOf(pawn));

    /// <inheritdoc />
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn)
    {
        ArgumentNullException.ThrowIfNull(pawn);
        int controllerIndex = PawnLookup.IndexOf(PawnLookup.TryUnboxHandle(pawn["m_hController"]));
        return Read(tracker, controllerIndex - 1);
    }

    /// <inheritdoc />
    public bool TryReadInt(PawnReadContext context, out int value)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ResolveController(context.Tracker, SlotOf(context.Pawn)) is { } controller)
        {
            return TryReadInt(controller, out value);
        }

        value = 0;
        return false;
    }

    /// <inheritdoc />
    public IPerPlayerEntityValueProvider CloneForWorker() => new ControllerFieldProvider(Spec);

    private int? Read(EntityTracker tracker, int playerSlot) =>
        ResolveController(tracker, playerSlot) is { } controller && TryReadInt(controller, out int value)
            ? value
            : null;

    private static int SlotOf(EntityState pawn) =>
        PawnLookup.TryReadHandle(pawn, "m_hController", out uint handle) ? PawnLookup.IndexOf(handle) - 1 : -1;
}
