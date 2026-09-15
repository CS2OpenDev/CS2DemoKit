#region

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     The storage kind of one per-pawn digest column. The digest stores every provider's value
///     unboxed in a typed column, so a provider's <see cref="IPerPlayerEntityValueProvider.ValueType" />
///     must be one of the four CLR types these name; anything else is rejected when the column
///     layout is built rather than silently dropped per frame.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "The kinds name the CLR storage type each column uses, which is the whole point of the enum.")]
public enum PawnCellKind : byte
{
    /// <summary>A <c>System.Int32</c> column.</summary>
    Int,

    /// <summary>A <c>System.Boolean</c> column, stored as 0 or 1 in the int storage.</summary>
    Bool,

    /// <summary>A <c>System.Single</c> column.</summary>
    Float,

    /// <summary>A <c>System.String</c> column; null is "no value", never a stored string.</summary>
    String
}

/// <summary>
///     Typed read of an <c>int</c> or <c>bool</c> column. A provider whose value type is
///     <c>int</c> or <c>bool</c> implements this so the digest can read it without a box; one that
///     does not is read through <see cref="IPerPlayerEntityValueProvider.ReadForPawn" /> and unboxed,
///     which is correct but costs the allocation the typed path exists to avoid.
///     <para>
///         The contract is the boxed one restated: returning <c>false</c> is exactly the cases the
///         boxed read returns <c>null</c>, and the value returned with <c>true</c> is exactly the
///         value the boxed read would box. <c>PawnCellReaderParityTests</c> holds every shipped
///         provider to that on a real demo.
///     </para>
/// </summary>
public interface IPawnIntCellReader
{
    /// <summary>
    ///     Reads the cell for the pawn bound to <paramref name="context" />. For a <c>bool</c>
    ///     provider the value is 0 or 1.
    /// </summary>
    /// <param name="context">The pawn being read, plus per-pawn caches shared by every reader.</param>
    /// <param name="value">The cell value, or 0 when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> when the provider has a value for this pawn; <c>false</c> for "no value".</returns>
    bool TryReadInt(PawnReadContext context, out int value);
}

/// <summary>Typed read of a <c>float</c> column. See <see cref="IPawnIntCellReader" /> for the contract.</summary>
public interface IPawnFloatCellReader
{
    /// <summary>Reads the cell for the pawn bound to <paramref name="context" />.</summary>
    /// <param name="context">The pawn being read, plus per-pawn caches shared by every reader.</param>
    /// <param name="value">The cell value, or 0 when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> when the provider has a value for this pawn; <c>false</c> for "no value".</returns>
    bool TryReadFloat(PawnReadContext context, out float value);
}

/// <summary>Typed read of a <c>string</c> column. See <see cref="IPawnIntCellReader" /> for the contract.</summary>
public interface IPawnStringCellReader
{
    /// <summary>Reads the cell for the pawn bound to <paramref name="context" />.</summary>
    /// <param name="context">The pawn being read, plus per-pawn caches shared by every reader.</param>
    /// <param name="value">The cell value, or null when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> when the provider has a value for this pawn; <c>false</c> for "no value".</returns>
    bool TryReadString(PawnReadContext context, [NotNullWhen(true)] out string? value);
}

/// <summary>
///     The pawn a typed cell reader is reading, plus the reads several providers share on the same
///     pawn-frame, resolved once and cached until the next <see cref="Bind" />: the reconstructed
///     origin that three position columns split, the eye angle that two angle columns split, the
///     aim-punch base angle that two punch columns split, and the SDK wrapper, which is resolved
///     only if a reader asks for it.
///     <para>
///         One instance per digest stream, never shared across threads: the parallel producer's
///         workers each own one through their <c>PerPawnDeltaState</c>.
///     </para>
/// </summary>
public sealed class PawnReadContext
{
    private readonly LaneCursor _eyeAngleCursor = new();
    private Vector3? _eyeAngles;
    private bool _eyeAnglesResolved;
    private Vector3? _origin;
    private bool _originResolved;
    private Vector3 _punchAngle;
    private bool _punchHasValue;
    private AimPunchLayout _punchLayout;
    private bool _punchResolved;
    private CSPlayerPawn? _wrapper;

    /// <summary>The tracker the bound pawn lives in.</summary>
    public EntityTracker Tracker { get; private set; } = null!;

    /// <summary>The bound pawn's raw entity state.</summary>
    public EntityState Pawn { get; private set; } = null!;

    /// <summary>
    ///     The bound pawn's SDK wrapper, resolved on first use. Throws rather than returning null
    ///     when the pawn's class has no wrapper binding, so an unbound pawn class is loud instead of
    ///     reading as a column of nulls.
    /// </summary>
    public CSPlayerPawn Wrapper =>
        _wrapper ??= SdkEntityWorlds.Wrap<CSPlayerPawn>(Tracker, Pawn)
                     ?? throw new InvalidOperationException(
                         $"pawn class '{Pawn.ClassName}' has no CSPlayerPawn wrapper binding, so its "
                         + "wrapper-reading providers cannot be served");

    /// <summary>
    ///     The pawn's world position from <see cref="PositionUtil.CellToWorld" />, or null when the
    ///     six CBodyComponent leaves have not all been networked. Resolved once per pawn-frame.
    /// </summary>
    public Vector3? Origin
    {
        get
        {
            if (!_originResolved)
            {
                _origin = PositionUtil.CellToWorld(Pawn);
                _originResolved = true;
            }

            return _origin;
        }
    }

    /// <summary>
    ///     The pawn's networked eye angle (pitch, yaw, roll in degrees), or null when it has never
    ///     been networked. Same read as <c>EntityState.TryGet&lt;Vector3&gt;</c> on
    ///     <c>m_angEyeAngles</c>, with the path resolved once per class shape. Resolved once per
    ///     pawn-frame.
    /// </summary>
    public Vector3? EyeAngles
    {
        get
        {
            if (!_eyeAnglesResolved)
            {
                _eyeAngles = ReadEyeAngles();
                _eyeAnglesResolved = true;
            }

            return _eyeAngles;
        }
    }

    /// <summary>
    ///     The pawn's aim-punch base angle under <paramref name="layout" />, exactly as
    ///     <see cref="AimPunchSchema.TryReadBaseAngle" /> reads it, cached for the pawn-frame so the
    ///     pitch and yaw columns share one read.
    /// </summary>
    /// <param name="layout">The resolved field family for this demo.</param>
    /// <param name="angle">The base angle, or default when the pawn has never networked a sample.</param>
    /// <returns><c>true</c> when the pawn carries a spring sample.</returns>
    public bool TryPunchBaseAngle(AimPunchLayout layout, out Vector3 angle)
    {
        if (!_punchResolved || _punchLayout != layout)
        {
            _punchHasValue = AimPunchSchema.TryReadBaseAngle(Pawn, layout, out _punchAngle);
            _punchLayout = layout;
            _punchResolved = true;
        }

        angle = _punchAngle;
        return _punchHasValue;
    }

    /// <summary>Points the context at a pawn and forgets everything cached for the previous one.</summary>
    internal void Bind(EntityTracker tracker, EntityState pawn)
    {
        Tracker = tracker;
        Pawn = pawn;
        _wrapper = null;
        _originResolved = false;
        _eyeAnglesResolved = false;
        _punchResolved = false;
    }

    private Vector3? ReadEyeAngles()
    {
        // Only the object-lane hit is served from the slot. Every other case (int or float lane,
        // unmapped path, no shape) is handed to TryGet so its exact fall-through rules apply.
        SlotAddr addr = _eyeAngleCursor.Resolve(Pawn, SchemaNames.CCSPlayerPawn.EyeAngles);
        if (addr.Lane == LaneKind.Vector)
        {
            return Pawn.TryGetVectorSlot(addr.Slot, out Vector3 typed) ? typed : null;
        }

        if (addr.Lane == LaneKind.Object)
        {
            return Pawn.TryGetObjectSlot(addr.Slot, out object? boxed) ? (Vector3?)boxed : null;
        }

        return Pawn.TryGet<Vector3>(SchemaNames.CCSPlayerPawn.EyeAngles);
    }
}

/// <summary>
///     Remembers where one dotted path lives on the last <see cref="ClassShape" /> it was resolved
///     against, so a provider that reads the same leaf on every pawn of a class pays the
///     string-keyed probe once per shape instead of once per pawn-frame. Different trackers build
///     different shape instances, so a worker's provider clone re-resolves on its own tracker.
///     The cached pair is one immutable object, so a reader can never observe a torn (shape, slot).
/// </summary>
internal sealed class LaneCursor
{
    private Resolved? _last;

    /// <summary>The lane address of <paramref name="path" /> on the entity's bound shape, or <see cref="SlotAddr.Fallback" />.</summary>
    public SlotAddr Resolve(EntityState entity, string path)
    {
        ClassShape? shape = entity.Shape;
        Resolved? last = _last;
        if (last is not null && ReferenceEquals(last.Shape, shape))
        {
            return last.Addr;
        }

        SlotAddr addr = shape is not null && shape.PathToSlot.TryGetValue(path, out SlotAddr found)
            ? found
            : SlotAddr.Fallback;
        _last = new Resolved(shape, addr);
        return addr;
    }

    private sealed record Resolved(ClassShape? Shape, SlotAddr Addr);
}

/// <summary>What a lane probe found for a path: a seen typed slot, a seen object slot, or nothing on a lane.</summary>
internal enum LaneHit : byte
{
    /// <summary>The path is not lane-mapped, or its slot has not been written; read the path boxed.</summary>
    Miss,

    /// <summary>A seen int-lane slot.</summary>
    Int,

    /// <summary>A seen float-lane slot.</summary>
    Float,

    /// <summary>A seen object-lane slot.</summary>
    Object
}

/// <summary>
///     The wire-type coercions the generic provider applies, as typed functions. The boxed
///     <see cref="GenericPerPlayerFieldProvider" /> read and its typed cell readers both go through
///     these, so the COERCIONS cannot drift. The gates (PositiveOnly, UnseenAsDefault, and the
///     hop-failed short circuit ahead of both) are restated per path, and only
///     <c>PawnCellReaderParityTests</c> holds those together, arm by arm, on a real demo.
/// </summary>
internal static class PawnCellCoercion
{
    /// <summary>
    ///     Reads the seen lane slot for <paramref name="path" />, if there is one. A hit is exactly
    ///     the value <c>entity[path]</c> would box; a miss means the caller must take the boxed read,
    ///     which owns the fallback dictionary and any alias walk.
    /// </summary>
    public static LaneHit Probe(
        EntityState entity, string path, LaneCursor cursor, out int intValue, out float floatValue, out object? objectValue)
    {
        intValue = 0;
        floatValue = 0f;
        objectValue = null;
        SlotAddr addr = cursor.Resolve(entity, path);
        switch (addr.Lane)
        {
            case LaneKind.Int:
                return entity.TryGetIntSlot(addr.Slot, out intValue) ? LaneHit.Int : LaneHit.Miss;
            case LaneKind.Float:
                return entity.TryGetFloatSlot(addr.Slot, out floatValue) ? LaneHit.Float : LaneHit.Miss;
            case LaneKind.Object:
                return entity.TryGetObjectSlot(addr.Slot, out objectValue) ? LaneHit.Object : LaneHit.Miss;
            case LaneKind.Vector:
                // Served boxed, as the object lane served it; nothing on this path reads a vector
                // per frame.
                if (entity.TryGetVectorSlot(addr.Slot, out Vector3 vec))
                {
                    objectValue = vec;
                    return LaneHit.Object;
                }

                return LaneHit.Miss;
            default:
                return LaneHit.Miss;
        }
    }

    /// <summary>CS2 networks ints as varints with wire-type variance; every integral width coerces.</summary>
    public static bool TryCoerceInt(object? raw, out int value)
    {
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case uint u:
                value = (int)u;
                return true;
            case long l:
                value = (int)l;
                return true;
            case ulong ul:
                value = (int)ul;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Bools arrive as bool, or as an int 0/1 on the int lane.</summary>
    public static bool TryCoerceBool(object? raw, out bool value)
    {
        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case int i:
                value = i != 0;
                return true;
            case uint u:
                value = u != 0;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>Floats arrive as float, or as double from a wider decoder.</summary>
    public static bool TryCoerceFloat(object? raw, out float value)
    {
        switch (raw)
        {
            case float f:
                value = f;
                return true;
            case double d:
                value = (float)d;
                return true;
            default:
                value = 0f;
                return false;
        }
    }
}
