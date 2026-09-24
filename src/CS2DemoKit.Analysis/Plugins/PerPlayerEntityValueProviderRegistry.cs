namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Registry of <see cref="IPerPlayerEntityValueProvider" /> instances. Parallel to
///     <see cref="EntityValueProviderRegistry" /> (singleton/push model). Kept as a separate type
///     because the contract — providers are read on demand by edges, not polled into synthesized
///     events — differs.
///     <para>
///         <b>Registration order is the digest's column order, so it is part of the data
///         format.</b> <c>DigestColumnLayout</c>, the pre-frame snapshot and
///         <c>AimVantageScanner</c> all address columns by index, and the per-pawn fold goldens
///         hash that index, so the sequence <see cref="All" /> hands back is an observable
///         contract and not an implementation detail. It is therefore kept in an explicit list:
///         enumerating a dictionary's values happens to give insertion order only while nothing is
///         removed, which is not a guarantee the BCL makes and not one to build a column layout on.
///     </para>
///     <para>
///         <b>A name is claimed once.</b> <see cref="Register" /> refuses a name already taken
///         rather than replacing the provider under it. Silently replacing is the exact failure
///         the scoreboard-label guard was added for one layer up: the column count does not
///         change, so the layout still looks compatible, and every rule reading
///         <c>player.health</c> quietly reads the newcomer's values instead. Names are compared
///         case-insensitively — the same comparison <see cref="Get" /> resolves with, so a
///         provider can never be registered under a spelling that a lookup would then find.
///     </para>
/// </summary>
public sealed class PerPlayerEntityValueProviderRegistry
{
    private readonly Dictionary<string, IPerPlayerEntityValueProvider> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IPerPlayerEntityValueProvider> _inOrder = [];

    /// <summary>All registered per-player providers, in registration order (the digest's column order).</summary>
    public IReadOnlyCollection<IPerPlayerEntityValueProvider> All => _inOrder;

    /// <summary>Creates a registry pre-populated with the framework's built-in per-player providers.</summary>
    public static PerPlayerEntityValueProviderRegistry CreateDefault()
    {
        PerPlayerEntityValueProviderRegistry registry = new();
        registry.Register(new PawnHealthProvider());
        registry.Register(new ActiveWeaponProvider());
        // Baseline economy stats. Each is captured every frame by the pre-frame snapshot today,
        // but rules sample them only at round_freeze_end — prime candidates for the lazy-read
        // refinement. (A movement/speed provider was prototyped but removed: m_vecVelocity is not
        // usably networked on the server pawn in GOTV demos — firing speed came out uniformly 0.)
        registry.Register(new PawnEquipmentValueProvider());
        registry.Register(new PawnArmorProvider());
        // Active-weapon magazine count (Tier C): spec-constructed from day one — the same
        // GenericPerPlayerFieldProvider instance shape ships in BuiltinProviderSpecs.
        // CreateGenericPerPlayerProviders(), at the same (last) position, so the
        // ProviderDigestParityTests gate holds by construction (no hand-written twin).
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnActiveWeaponClip));
        // Nav-mesh place name (Tier C): same spec-constructed-on-both-sides pattern as the clip
        // provider above. BuiltinProviderSpecs.CreateGenericPerPlayerProviders() registers the
        // identical PawnPlace spec at this same position, so digest parity holds by construction.
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnPlace));
        // Aim-rating reads: the per-tick entity state the aim-quality metrics (counter-strafing,
        // spray control, crosshair placement) need and the engine did not previously surface.
        // Spec-constructed on both sides of the parity gate at these same positions, so the two
        // digest streams stay identical by construction. Registered BEFORE the position trio, not
        // appended, because PawnPositionProviderTests pins those three as the last three.
        //
        // Unlike the economy reads above, every one of these changes on nearly every frame for
        // nearly every pawn, so each costs the digest's delta encoding a full column. That is
        // accepted only because gating is by name: a ruleset referencing none of them pays
        // nothing at all.
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnDuckAmount));
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnMaxSpeed));
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnShotsFired));
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnIsScoped));
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnFlashDuration));
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.WeaponRecoilIndex));
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.WeaponAccuracyPenalty));
        // The player's cash, read off the controller through the pawn's controller handle. Changes
        // only on a purchase, a reward or a round's income, so it costs the digest little; gated by
        // name like everything here. Same spec at the same position in
        // BuiltinProviderSpecs.CreateGenericPerPlayerProviders(), so parity holds by construction.
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.ControllerMoney));
        // Eye angle and aim-punch base angle, one provider per component. Both sources are QAngle,
        // which the rules type vocabulary cannot express (no vector type, no member access) and
        // which offers no scalar leaf to name, so these are hand-written classes with no spec
        // form, registered identically on both sides. See PawnAimPunchProvider for why the two
        // punch columns are a raw spring sample and not a resolved punch angle.
        registry.Register(new PawnEyeAngleProvider(PawnAngleAxis.Pitch));
        registry.Register(new PawnEyeAngleProvider(PawnAngleAxis.Yaw));
        registry.Register(new PawnAimPunchProvider(PawnAngleAxis.Pitch));
        registry.Register(new PawnAimPunchProvider(PawnAngleAxis.Yaw));
        // World position, one provider per axis. Computed from CBodyComponent's cell + offset
        // pair rather than a single leaf, so there is no ProviderSpec form; both sides of the
        // parity gate register these same instances, appended last in the same order.
        //
        // These change almost every frame too, costing the digest's delta encoding a full column
        // each. Gating is by name, so a ruleset that reads no axis pays nothing.
        registry.Register(new PawnPositionProvider(PawnPositionAxis.X));
        registry.Register(new PawnPositionProvider(PawnPositionAxis.Y));
        registry.Register(new PawnPositionProvider(PawnPositionAxis.Z));
        return registry;
    }

    /// <summary>Returns the provider registered under the given name, or <c>null</c>.</summary>
    public IPerPlayerEntityValueProvider? Get(string name) =>
        _byName.GetValueOrDefault(name);

    /// <summary>Registers a provider under its <see cref="IPerPlayerEntityValueProvider.Name" />.</summary>
    /// <param name="provider">The provider to register.</param>
    /// <exception cref="ArgumentException">
    ///     The name is already registered, case-insensitively. Reported rather than overwritten:
    ///     see the class summary on why a stolen column is invisible downstream.
    /// </exception>
    public void Register(IPerPlayerEntityValueProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_byName.TryGetValue(provider.Name, out IPerPlayerEntityValueProvider? existing))
        {
            throw new ArgumentException(
                $"a per-player provider is already registered as '{existing.Name}' "
                + $"({existing.GetType().Name}); '{provider.Name}' ({provider.GetType().Name}) would replace it "
                + "and every rule reading that name would silently read the replacement. Provider names are "
                + "compared case-insensitively; pick a name of your own.",
                nameof(provider));
        }

        _byName.Add(provider.Name, provider);
        _inOrder.Add(provider);
    }
}
