namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Holds the set of <see cref="IEntityValueProvider" /> instances available to the rule
///     engine. Providers read entity state at evaluation time.
/// </summary>
public sealed class EntityValueProviderRegistry
{
    private readonly Dictionary<string, IEntityValueProvider> _byContext =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered entity-value providers, in insertion order.</summary>
    public IReadOnlyCollection<IEntityValueProvider> All => _byContext.Values;

    /// <summary>Creates a registry pre-populated with the framework's built-in providers.</summary>
    public static EntityValueProviderRegistry CreateDefault()
    {
        EntityValueProviderRegistry registry = new();
        registry.Register(new FreezePeriodProvider());
        return registry;
    }

    /// <summary>Returns the provider registered under the given context name, or <c>null</c>.</summary>
    public IEntityValueProvider? Get(string contextName) =>
        _byContext.GetValueOrDefault(contextName);

    /// <summary>Registers a provider under its <see cref="IEntityValueProvider.ContextName" />.</summary>
    public void Register(IEntityValueProvider provider) =>
        _byContext[provider.ContextName] = provider;

    /// <summary>Attempts to look up the provider for a context name. Returns <c>true</c> on hit.</summary>
    public bool TryGet(string contextName, out IEntityValueProvider? provider) =>
        _byContext.TryGetValue(contextName, out provider);
}
