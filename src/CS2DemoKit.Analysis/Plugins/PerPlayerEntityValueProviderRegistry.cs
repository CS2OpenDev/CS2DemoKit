namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Registry of <see cref="IPerPlayerEntityValueProvider" /> instances. Parallel to
///     <see cref="EntityValueProviderRegistry" /> (singleton/push model). Kept as a separate type
///     because the contract — providers are read on demand by edges, not polled into synthesized
///     events — differs.
/// </summary>
public sealed class PerPlayerEntityValueProviderRegistry
{
    private readonly Dictionary<string, IPerPlayerEntityValueProvider> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered per-player providers, in insertion order.</summary>
    public IReadOnlyCollection<IPerPlayerEntityValueProvider> All => _byName.Values;

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
        // World position, one provider per axis. Computed from CBodyComponent's cell + offset
        // pair rather than a single leaf, so there is no ProviderSpec form; both sides of the
        // parity gate register these same instances, appended last in the same order.
        //
        // These are the only shipped providers whose value changes almost every frame, which
        // costs the digest's delta encoding a full column each. Gating is by name, so a ruleset
        // that reads no axis pays nothing.
        registry.Register(new PawnPositionProvider(PawnPositionAxis.X));
        registry.Register(new PawnPositionProvider(PawnPositionAxis.Y));
        registry.Register(new PawnPositionProvider(PawnPositionAxis.Z));
        return registry;
    }

    /// <summary>Returns the provider registered under the given name, or <c>null</c>.</summary>
    public IPerPlayerEntityValueProvider? Get(string name) =>
        _byName.GetValueOrDefault(name);

    /// <summary>Registers a provider under its <see cref="IPerPlayerEntityValueProvider.Name" />.</summary>
    public void Register(IPerPlayerEntityValueProvider provider) =>
        _byName[provider.Name] = provider;
}
