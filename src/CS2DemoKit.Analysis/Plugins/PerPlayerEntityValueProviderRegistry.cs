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
        // provider above — BuiltinProviderSpecs.CreateGenericPerPlayerProviders() appends the
        // identical PawnPlace spec last, so digest parity holds by construction.
        registry.Register(new GenericPerPlayerFieldProvider(BuiltinProviderSpecs.PawnPlace));
        return registry;
    }

    /// <summary>Returns the provider registered under the given name, or <c>null</c>.</summary>
    public IPerPlayerEntityValueProvider? Get(string name) =>
        _byName.GetValueOrDefault(name);

    /// <summary>Registers a provider under its <see cref="IPerPlayerEntityValueProvider.Name" />.</summary>
    public void Register(IPerPlayerEntityValueProvider provider) =>
        _byName[provider.Name] = provider;
}
