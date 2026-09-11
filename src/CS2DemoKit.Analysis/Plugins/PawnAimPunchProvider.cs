#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Reads one component of a pawn's aim-punch BASE angle, exposed as
///     <c>entity.pawn.punch_pitch</c> / <c>entity.pawn.punch_yaw</c>. The value is a
///     <c>QAngle</c> that the parser decodes to a <see cref="Vector3" /> of (pitch, yaw, roll)
///     degrees.
///     <para>
///         <b>Version independent by construction.</b> CS2 networks aim punch two different ways
///         depending on when the demo was recorded, and both are in circulation:
///         <c>m_pAimPunchServices.m_predictableBaseAngle</c> from build 22894272, and the flat
///         <c>m_aimPunchAngle</c> before it. <see cref="AimPunchSchema" /> owns that difference
///         and hands back one <see cref="AimPunchState" />, so nothing downstream of this provider
///         branches on schema version. Naming a single path here instead would abort the entire
///         analysis at prime time on every demo of the other vintage, and the vintage it would
///         reject is the ordinary matchmaking demo most users have.
///     </para>
///     <para>
///         <b>WARNING: this is a spring state, not a resolved aim punch.</b> What it emits is the
///         raw sample <see cref="AimPunchState" /> describes: correct only AT its base tick, and wrong
///         in a way that still looks plausible anywhere else. The decay integration is deliberately NOT
///         applied yet. Treat these two columns as the raw spring sample they are, and do not build an
///         effective-aim (viewangle plus recoil scale times punch) metric on them until the
///         integration lands.
///     </para>
///     <para>
///         <b>The oracle for that follow-up</b> is <c>bullet_damage.AimPunchX/Y/Z</c>, the
///         server's own resolved punch at the damaging shot. Any integration added here has to
///         agree with it on a demo that carries the event; the bundled pro GOTV sample does not,
///         so the check needs a Valve matchmaking demo. <c>m_aimPunchCache</c>, which used to
///         carry per-shot history and would have made this trivial, has been gone since build
///         21529689.
///     </para>
///     <para>
///         Split into scalar components and read through <see cref="IPawnStateReader" /> for the
///         same two reasons as <see cref="PawnEyeAngleProvider" />: the rules type vocabulary has
///         no vector type and no member access, and the SDK wrapper's typed accessors resolve
///         only through the Lens lane.
///     </para>
/// </summary>
public sealed class PawnAimPunchProvider(PawnAngleAxis axis)
    : IPerPlayerEntityValueProvider, IWorkerCloneable<IPerPlayerEntityValueProvider>, IPawnStateReader,
        IMultiSchemaFieldProvider, IPawnFloatCellReader
{
    // Latched on first successful probe. A worker owns one tracker for its whole chunk, so this
    // resolves once per worker rather than once per pawn-frame. Not shared across workers: each
    // gets its own instance through CloneForWorker.
    private AimPunchLayout _layout = AimPunchLayout.None;
    private bool _resolved;

    /// <summary>The angle component this instance reads.</summary>
    public PawnAngleAxis Axis => axis;

    /// <inheritdoc />
    public string EntityClass => AimPunchSchema.PawnClass;

    /// <inheritdoc />
    // The preferred (current-build) spelling. Validation judges CandidateFieldNames instead, so
    // this being absent on an older demo is not a fault; it is the older demo's normal shape.
    public string FieldName => AimPunchSchema.ServicesAngle;

    /// <inheritdoc />
    public IReadOnlyList<string> CandidateFieldNames => AimPunchSchema.CandidateAnglePaths;

    /// <inheritdoc />
    public string Name => axis == PawnAngleAxis.Pitch ? "entity.pawn.punch_pitch" : "entity.pawn.punch_yaw";

    /// <inheritdoc />
    public Type ValueType => typeof(float);

    /// <inheritdoc />
    public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
    {
        EntityTracker tracker = layer.Tracker;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            if (ReadForPawnState(tracker, pawn) is { } value)
            {
                emit(slot, value);
            }
        });
    }

    /// <inheritdoc />
    // Null (slot skipped) when the base angle has never been networked: a pawn that has not
    // fired since spawning has no spring sample, which is not the same fact as "zero punch".
    public object? ReadForPawnState(EntityTracker tracker, EntityState pawn)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        if (!_resolved)
        {
            _layout = AimPunchSchema.Resolve(tracker);
            _resolved = true;
        }

        return AimPunchSchema.Read(pawn, _layout) is { } state ? Select(state.BaseAngle) : null;
    }

    /// <inheritdoc />
    // The same latch and gate as ReadForPawnState, reading only the base angle (the velocity,
    // tick and fraction Read also fetches were discarded here) and sharing it with the other
    // component through the context.
    public bool TryReadFloat(PawnReadContext context, out float value)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_resolved)
        {
            _layout = AimPunchSchema.Resolve(context.Tracker);
            _resolved = true;
        }

        if (context.TryPunchBaseAngle(_layout, out Vector3 angle))
        {
            value = Select(angle);
            return true;
        }

        value = 0f;
        return false;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. Read through <see cref="IPawnStateReader" />.</exception>
    // The SDK wrapper reaches the services sub-entity only through the Lens lane and comes back
    // null on trackers where the state indexer still holds the decoded QAngle, so honouring this
    // signature would mean emitting nothing for every pawn on those trackers. Failing loudly
    // beats a column of nulls that reads as "nobody's aim was ever punched".
    public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) =>
        throw new NotSupportedException(
            $"provider '{Name}' reads the raw entity state; call ReadForPawnState "
            + "(IPawnStateReader) instead of ReadForPawn.");

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer, int playerSlot)
    {
        EntityState? pawn = PawnLookup.ResolvePawn(layer.Tracker, playerSlot);
        return pawn is null ? null : ReadForPawnState(layer.Tracker, pawn);
    }

    /// <inheritdoc />
    public IPerPlayerEntityValueProvider CloneForWorker() => new PawnAimPunchProvider(axis);

    private float Select(Vector3 angle) => axis == PawnAngleAxis.Pitch ? angle.X : angle.Y;
}
