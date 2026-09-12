#region

using System.Numerics;
using CS2DemoKit.Analysis.Plugins;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     One player's <see cref="VisibilityAnalyzer.Vantage" /> at one sampled tick, paired with the
///     movement state derived alongside it. Both halves are computed once per (player, tick) and read
///     by every consumer: the transition scanner, and any shot-anchored metric that needs the speed at
///     the shot. Recomputing either per pair is the mistake this type exists to prevent.
/// </summary>
/// <param name="Vantage">Eye, forward ray, feet, team and duck amount, in the shape every visibility primitive takes.</param>
/// <param name="Speed2D">
///     Horizontal speed in units per second, differenced from the PREVIOUS sampled tick's position.
///     Zero when no usable previous sample exists (see <see cref="AimVantageScanner" /> for what
///     "usable" excludes). This is a derivative of position, not a networked value:
///     <c>m_vecVelocity</c> reads uniformly zero on GOTV pawns.
/// </param>
/// <param name="EyePitchDeg">
///     The viewer's crosshair pitch this tick, in degrees, as networked rather than reconstructed.
/// </param>
/// <param name="EyeYawDeg">
///     The yaw half of the same. Carried alongside <paramref name="Vantage" /> even though its
///     forward ray is built from these two, because recovering them from a unit vector costs two
///     inverse trig calls per consumer and reads as derived data when it is in fact the source.
/// </param>
public readonly record struct AimVantage(
    VisibilityAnalyzer.Vantage Vantage,
    float Speed2D,
    float EyePitchDeg,
    float EyeYawDeg);

/// <summary>
///     Builds one <see cref="AimVantage" /> per live pawn per sampled tick out of the per-player
///     DIGEST COLUMNS, without a second entity replay. This is the "future lever" named in
///     <see cref="VisibilityAnalyzer" />'s class doc: the standalone analyzer runs its own sequential
///     replay purely to reconstruct vantage, and once vantage is available inside the existing digest
///     walk that second decode is redundant.
///     <para>
///         <b>How it is fed.</b> <see cref="Observe(int, IReadOnlyList{object?})" /> takes one row of an
///         <c>EntityFrameDigest.PerPawn</c> delta list. Those rows carry only the cells that CHANGED
///         this frame, with unchanged positions left null, so the scanner folds them into a running
///         per-slot record exactly the way the pre-frame snapshot does. That fold is the whole cost:
///         no entity-set walk, no field decode, no per-pair recomputation.
///     </para>
///     <para>
///         <b>A null cell is "unchanged", not "absent".</b> The delta encoding cannot distinguish a
///         cell that held from a cell whose provider newly read null, and the pre-frame snapshot
///         resolves that ambiguity by skipping nulls. This does the same, which means a despawned
///         pawn keeps its last good position rather than disappearing. Liveness is therefore NOT
///         inferred from the columns: the caller's <c>resolveTeam</c> callback decides whether a slot
///         is an eligible viewer or target this tick, and a callback that returns a team for a dead
///         or disconnected player will produce vantages for a corpse.
///     </para>
///     <para>
///         <b>Cost, stated honestly.</b> The six columns this needs (position, eye angles, duck)
///         are the ones that largely defeat the digest's delta encoding; see
///         <see cref="PerPlayerEntityValueProviderRegistry" />. Providers are reference-gated by
///         name, so the cost lands only when an aim ruleset is loaded.
///     </para>
/// </summary>
public sealed class AimVantageScanner
{
    /// <summary>Provider name for the pawn's world X, one of the six columns this scanner requires.</summary>
    public const string PosXProvider = "entity.pawn.pos_x";

    /// <summary>Provider name for the pawn's world Y.</summary>
    public const string PosYProvider = "entity.pawn.pos_y";

    /// <summary>Provider name for the pawn's world Z (feet height).</summary>
    public const string PosZProvider = "entity.pawn.pos_z";

    /// <summary>Provider name for the pawn's eye pitch in degrees (the <c>m_angEyeAngles</c> X component).</summary>
    public const string EyePitchProvider = "entity.pawn.eye_pitch";

    /// <summary>Provider name for the pawn's eye yaw in degrees (the <c>m_angEyeAngles</c> Y component).</summary>
    public const string EyeYawProvider = "entity.pawn.eye_yaw";

    /// <summary>Provider name for the pawn's duck amount (0 standing to 1 fully crouched).</summary>
    public const string DuckAmountProvider = "entity.pawn.duck_amount";

    /// <summary>
    ///     Implied horizontal speed (units per second) above which a position delta is read as a
    ///     TELEPORT rather than movement, and the derived speed is reported as 0. Nothing in CS2
    ///     movement approaches this: the fastest legitimate ground speed is around 250 u/s and even a
    ///     boosted jump stays well under 400. Round restarts and respawns relocate a player between
    ///     two consecutive sampled ticks, and an uncapped derivative turns that into a five-figure
    ///     spike that would silently satisfy every "was moving" gate built on this column.
    /// </summary>
    public const float TeleportSpeedThreshold = 500f;

    /// <summary>
    ///     Default width, in ticks, of the per-slot speed ring <see cref="TryPeakSpeed" /> answers
    ///     from, and the width every production build uses.
    ///     <para>
    ///         This is a HARD FLOOR, not a comfort margin: a caller asking for a window wider than the
    ///         ring gets the peak over only the part that survived the wrap, with no error, so the
    ///         counter-strafe gate would quietly under-admit. It therefore has to stay at least
    ///         <c>AimShotContextEdge.CounterStrafeLookbackSeconds</c> converted at the demo's tick
    ///         rate: 13 ticks at 64 and 26 at 128, which this clears with room for a 256-tick demo.
    ///         <c>CounterStrafeWindowDerivationTests</c> asserts the relation rather than leaving it
    ///         to this remark, which is what it was left to before.
    ///     </para>
    /// </summary>
    public const int DefaultSpeedHistoryTicks = 64;

    /// <summary>
    ///     The six per-player providers this scanner reads, by name. A caller wiring the scanner must
    ///     gate all six into the digest (they are reference-gated, so nothing forces them in on its
    ///     own) and pass the resulting provider order to the constructor.
    /// </summary>
    public static IReadOnlyList<string> RequiredProviders { get; } =
    [
        PosXProvider,
        PosYProvider,
        PosZProvider,
        EyePitchProvider,
        EyeYawProvider,
        DuckAmountProvider
    ];

    private readonly List<AimVantage> _current = new(12);
    private readonly int _duckColumn;
    private readonly int _eyePitchColumn;
    private readonly int _eyeYawColumn;
    private readonly int _maxSpeedSampleGapTicks;
    private readonly int _posXColumn;
    private readonly int _posYColumn;
    private readonly int _posZColumn;
    private readonly Func<int, int> _resolveTeam;
    private readonly Dictionary<int, SlotState> _slots = new();
    private readonly int _speedHistoryTicks;
    private readonly double _tickRate;

    /// <param name="digestProviderNames">
    ///     The per-player provider names in DIGEST COLUMN ORDER, i.e. the same list the digest's
    ///     per-pawn value arrays are indexed by. Resolved to column indices once here; a missing
    ///     provider throws rather than reading a column of nulls that would look like "nobody had a
    ///     position".
    /// </param>
    /// <param name="resolveTeam">
    ///     Slot to live team number, or any value &lt;= 1 for "not an eligible viewer or target this
    ///     tick" (unassigned, spectating, dead, disconnected, or not yet materialized). The &gt; 1
    ///     convention is <see cref="VisibilityAnalyzer.AreEnemies" />'s, reused so a single callback
    ///     carries both the team and the liveness gate the delta columns cannot express.
    /// </param>
    /// <param name="tickRate">Ticks per second, used to convert a sampled tick gap into seconds.</param>
    /// <param name="maxSpeedSampleGapTicks">
    ///     Largest gap between sampled ticks a speed may be differenced across. A wider gap means the
    ///     player was absent from the sample set in between (dead, dormant, or the demo paused), and
    ///     the straight-line distance across that hole is not a speed.
    /// </param>
    /// <param name="speedHistoryTicks">
    ///     Width, in ticks, of the per-slot speed ring <see cref="TryPeakSpeed" /> answers from.
    ///     Defaults to <see cref="DefaultSpeedHistoryTicks" />; see the remark there for the floor it
    ///     has to clear.
    /// </param>
    public AimVantageScanner(
        IReadOnlyList<string> digestProviderNames,
        Func<int, int> resolveTeam,
        double tickRate = 64.0,
        int maxSpeedSampleGapTicks = 8,
        int speedHistoryTicks = DefaultSpeedHistoryTicks)
    {
        ArgumentNullException.ThrowIfNull(digestProviderNames);
        ArgumentNullException.ThrowIfNull(resolveTeam);

        _resolveTeam = resolveTeam;
        _tickRate = tickRate > 0 ? tickRate : 64.0;
        _maxSpeedSampleGapTicks = Math.Max(1, maxSpeedSampleGapTicks);
        _speedHistoryTicks = Math.Max(1, speedHistoryTicks);

        _posXColumn = ColumnOf(digestProviderNames, PosXProvider);
        _posYColumn = ColumnOf(digestProviderNames, PosYProvider);
        _posZColumn = ColumnOf(digestProviderNames, PosZProvider);
        _eyePitchColumn = ColumnOf(digestProviderNames, EyePitchProvider);
        _eyeYawColumn = ColumnOf(digestProviderNames, EyeYawProvider);
        _duckColumn = ColumnOf(digestProviderNames, DuckAmountProvider);
    }

    /// <summary>
    ///     Folds one digest delta row into the running per-slot record. Null entries are left alone
    ///     (see the class doc on why a null reads as "unchanged"). Cheap enough to call for every row
    ///     of every frame, which is what the caller does.
    /// </summary>
    /// <param name="slot">The player slot the row belongs to.</param>
    /// <param name="values">The row's per-provider values, indexed by digest column order.</param>
    public void Observe(int slot, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!_slots.TryGetValue(slot, out SlotState? state))
        {
            state = new SlotState(_speedHistoryTicks);
            _slots[slot] = state;
        }

        Fold(values, _posXColumn, ref state.X, ref state.HasX);
        Fold(values, _posYColumn, ref state.Y, ref state.HasY);
        Fold(values, _posZColumn, ref state.Z, ref state.HasZ);
        Fold(values, _eyePitchColumn, ref state.Pitch, ref state.HasPitch);
        Fold(values, _eyeYawColumn, ref state.Yaw, ref state.HasYaw);

        // Duck needs no "seen" gate: 0 (standing) is the correct assumption for a pawn that has
        // never networked the field, and it is the same fallback VisibilityAnalyzer.TryVantage
        // takes. Gating on it would drop every standing player's vantage.
        bool duckSeen = false;
        Fold(values, _duckColumn, ref state.Duck, ref duckSeen);
    }

    /// <summary>
    ///     The unboxed form of <see cref="Observe(int, IReadOnlyList{object?})" />: folds row
    ///     <paramref name="row" /> of a typed digest, reading the six columns straight out of
    ///     their float storage. This is what the scanner's per-frame consume calls; the boxed form
    ///     stays for callers that hand-build rows.
    /// </summary>
    /// <param name="rows">The frame's per-pawn rows.</param>
    /// <param name="row">The row to fold.</param>
    internal void Observe(PerPawnColumns rows, int row)
    {
        int slot = rows.SlotAt(row);
        if (!_slots.TryGetValue(slot, out SlotState? state))
        {
            state = new SlotState(_speedHistoryTicks);
            _slots[slot] = state;
        }

        Fold(rows, row, _posXColumn, ref state.X, ref state.HasX);
        Fold(rows, row, _posYColumn, ref state.Y, ref state.HasY);
        Fold(rows, row, _posZColumn, ref state.Z, ref state.HasZ);
        Fold(rows, row, _eyePitchColumn, ref state.Pitch, ref state.HasPitch);
        Fold(rows, row, _eyeYawColumn, ref state.Yaw, ref state.HasYaw);

        bool duckSeen = false;
        Fold(rows, row, _duckColumn, ref state.Duck, ref duckSeen);
    }

    /// <summary>
    ///     Builds this tick's vantage set from the folded columns and advances each slot's speed
    ///     derivation. The returned list is REUSED across calls (cleared at entry), so a caller that
    ///     needs it beyond the next <see cref="Sample" /> must copy it.
    /// </summary>
    /// <param name="tick">The tick being sampled; also the clock the speed derivative is taken over.</param>
    public IReadOnlyList<AimVantage> Sample(int tick)
    {
        _current.Clear();
        foreach ((int slot, SlotState state) in _slots)
        {
            if (!state.HasX || !state.HasY || !state.HasZ)
            {
                continue; // no reconstructable origin yet (pre-spawn or dormant)
            }

            // Advance the derivative for every positioned slot, including ones the gates below
            // reject: a slot skipped for one tick must not later difference across the hole as if
            // it were a single sample interval.
            float speed = AdvanceSpeed(state, tick);

            if (!state.HasPitch || !state.HasYaw)
            {
                continue; // no view direction, so nothing that consumes a forward ray can use it
            }

            int team = _resolveTeam(slot);
            if (team <= 1)
            {
                continue; // not an eligible viewer or target this tick
            }

            Vector3 feet = new(state.X, state.Y, state.Z);
            _current.Add(new AimVantage(
                new VisibilityAnalyzer.Vantage(
                    slot,
                    team,
                    feet,
                    PlayerVantage.Eye(feet, state.Duck),
                    PlayerVantage.Forward(state.Pitch, state.Yaw),
                    true,
                    state.Duck),
                speed,
                state.Pitch,
                state.Yaw));
        }

        return _current;
    }

    /// <summary>
    ///     Highest derived 2D speed this slot recorded on a sampled tick inside the inclusive window
    ///     <paramref name="fromTick" />..<paramref name="throughTick" />, or <c>false</c> when the
    ///     window holds no sample at all. Distinguishing "no sample" from "zero speed" is the whole
    ///     point of the bool: they are the same number and opposite facts, and a counter-strafe
    ///     admission gate that reads an unsampled window as "stood perfectly still" would silently
    ///     drop every shot in it out of the denominator.
    ///     <para>
    ///         PEAK rather than the value at one tick because the question it answers is "did this
    ///         player exceed the movement threshold at any point in the window", which one sample
    ///         cannot answer: a counter-strafe is a stop that lands inside a single tick, so the
    ///         moving half of it is often one sample wide.
    ///     </para>
    /// </summary>
    /// <param name="slot">The player slot to query.</param>
    /// <param name="fromTick">Oldest tick to consider, inclusive.</param>
    /// <param name="throughTick">Newest tick to consider, inclusive.</param>
    /// <param name="peak">Receives the highest speed found, or 0 when the window is empty.</param>
    public bool TryPeakSpeed(int slot, int fromTick, int throughTick, out float peak)
    {
        peak = 0f;
        if (!_slots.TryGetValue(slot, out SlotState? state))
        {
            return false;
        }

        bool any = false;
        for (int i = 0; i < state.SpeedRingTick.Length; i++)
        {
            int stamped = state.SpeedRingTick[i];
            if (stamped < fromTick || stamped > throughTick)
            {
                continue; // wrapped-away or outside the window; the stamp is what makes the ring safe
            }

            any = true;
            if (state.SpeedRing[i] > peak)
            {
                peak = state.SpeedRing[i];
            }
        }

        return any;
    }

    private static int ColumnOf(IReadOnlyList<string> names, string provider)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], provider, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"AimVantageScanner: per-player provider '{provider}' is not in the digest's column set. "
            + "Every name in AimVantageScanner.RequiredProviders must be gated into the scanner "
            + "before a vantage can be built (they are reference-gated, so nothing forces them in "
            + "implicitly).");
    }

    private static void Fold(IReadOnlyList<object?> values, int column, ref float target, ref bool seen)
    {
        if (column >= values.Count || values[column] is not { } cell)
        {
            return;
        }

        target = VisibilityAnalyzer.CoerceFloat(cell, target);
        seen = true;
    }

    // The typed twin of the boxed Fold above, same acceptance: a float or int cell is the value,
    // anything else leaves the target alone but still counts as seen.
    private static void Fold(PerPawnColumns rows, int row, int column, ref float target, ref bool seen)
    {
        if (column >= rows.Layout.Count || !rows.IsPresent(row, column))
        {
            return;
        }

        switch (rows.Layout.Kinds[column])
        {
            case PawnCellKind.Float:
                target = rows.GetFloat(row, column);
                break;
            case PawnCellKind.Int:
                target = rows.GetInt(row, column);
                break;
        }

        seen = true;
    }

    /// <summary>
    ///     Differences horizontal position against the previous sampled tick, then records this tick
    ///     as the new anchor. Returns 0 rather than a number for every case where the difference is
    ///     not a speed: no previous sample, a gap wider than the scanner tolerates, a backward tick,
    ///     or an implied speed above <see cref="TeleportSpeedThreshold" />.
    /// </summary>
    private float AdvanceSpeed(SlotState state, int tick)
    {
        // Several demo frames can carry the same server tick, and this is called once per frame.
        // Re-differencing across a zero gap would report the player as stationary on the second
        // frame of every repeated tick, so a re-sample repeats the answer it already gave.
        if (state.HasLastSample && tick == state.LastSampleTick)
        {
            return state.LastSpeed;
        }

        float speed = 0f;
        int gap = tick - state.LastSampleTick;
        if (state.HasLastSample && gap > 0 && gap <= _maxSpeedSampleGapTicks)
        {
            float dx = state.X - state.LastX;
            float dy = state.Y - state.LastY;
            float travelled = MathF.Sqrt(dx * dx + dy * dy);
            float candidate = travelled / (float)(gap / _tickRate);
            speed = candidate <= TeleportSpeedThreshold ? candidate : 0f;
        }

        state.LastX = state.X;
        state.LastY = state.Y;
        state.LastSampleTick = tick;
        state.LastSpeed = speed;
        state.HasLastSample = true;

        // Stamp the ring alongside the anchor, so TryPeakSpeed sees exactly the samples this
        // method produced: the gaps and teleports it zeroes are zeroes in the history too, and a
        // tick nobody sampled leaves the slot it would have occupied stamped with an older tick.
        int index = (int)((uint)tick % (uint)state.SpeedRing.Length);
        state.SpeedRing[index] = speed;
        state.SpeedRingTick[index] = tick;
        return speed;
    }

    // One slot's folded columns plus the anchor its speed derivative is taken from. A class, not a
    // struct: the fold writes through `ref` into the stored instance rather than copying a struct out
    // of the dictionary and back on each of the six columns.
    private sealed class SlotState(int speedHistoryTicks)
    {
        // Tick-stamped ring of derived speeds. Stamps rather than a head index because ticks repeat
        // (several frames per server tick), skip, and occasionally go backwards on a seek, none of
        // which a monotonic cursor survives; a stale stamp simply falls outside every query window.
        public readonly float[] SpeedRing = new float[speedHistoryTicks];

        public readonly int[] SpeedRingTick = CreateUnstampedTicks(speedHistoryTicks);

        public float Duck;
        public bool HasLastSample;
        public bool HasPitch;
        public bool HasX;
        public bool HasY;
        public bool HasYaw;
        public bool HasZ;
        public int LastSampleTick;
        public float LastSpeed;
        public float LastX;
        public float LastY;
        public float Pitch;
        public float X;
        public float Y;
        public float Yaw;
        public float Z;

        // int.MinValue, not 0: tick 0 is a real tick, and a zero-filled stamp array would make every
        // slot claim a sample there. Every query window then rejects the unwritten entries.
        private static int[] CreateUnstampedTicks(int width)
        {
            int[] stamps = new int[width];
            Array.Fill(stamps, int.MinValue);
            return stamps;
        }
    }
}
