#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Analysis.Rules.Scopes;

/// <summary>
///     Dictionary-backed <see cref="IScopeSymbol" /> with factory methods per kind. The
///     Catalog-backed loader and tests build namespace trees from these; custom
///     implementations of the interface work equally.
/// </summary>
public sealed class ScopeSymbol : IScopeSymbol
{
    private readonly Dictionary<string, IScopeSymbol> _members;

    private ScopeSymbol(string name, ScopeSymbolKind kind, RulesType? valueType, bool supportsSetTest,
        IEnumerable<IScopeSymbol> members)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Kind = kind;
        ValueType = valueType;
        SupportsSetTest = supportsSetTest;
        _members = members.ToDictionary(m => m.Name, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ScopeSymbolKind Kind { get; }

    /// <inheritdoc />
    public RulesType? ValueType { get; }

    /// <inheritdoc />
    public bool SupportsSetTest { get; }

    /// <inheritdoc />
    public bool TryGetMember(string name, [NotNullWhen(true)] out IScopeSymbol? member) =>
        _members.TryGetValue(name, out member);

    /// <inheritdoc />
    public IEnumerable<string> MemberNames => _members.Keys;

    /// <summary>Creates a readable value symbol (event field, context, provider read, define list/map).</summary>
    /// <param name="name">The symbol's name.</param>
    /// <param name="type">The value's language-level type.</param>
    /// <returns>The symbol.</returns>
    public static ScopeSymbol Value(string name, RulesType type) =>
        new(name, ScopeSymbolKind.Value, type, false, []);

    /// <summary>Creates a stat reference symbol (hashes by referenced node identity, spec §6 row 6).</summary>
    /// <param name="name">The stat id segment.</param>
    /// <param name="type">The stat's value type.</param>
    /// <param name="supportsSetTest">True for scalar capture stats, which allow the <c>.set</c> presence test.</param>
    /// <returns>The symbol.</returns>
    public static ScopeSymbol Stat(string name, RulesType type, bool supportsSetTest = false) =>
        new(name, ScopeSymbolKind.Stat, type, supportsSetTest, []);

    /// <summary>Creates a <c>params:</c> binding symbol (a bound compile-time constant).</summary>
    /// <param name="name">The param's name.</param>
    /// <param name="type">The bound value's type.</param>
    /// <returns>The symbol.</returns>
    public static ScopeSymbol Param(string name, RulesType type) =>
        new(name, ScopeSymbolKind.Param, type, false, []);

    /// <summary>Creates a pure namespace symbol with members (e.g. <c>event</c>, <c>round.bomb</c>).</summary>
    /// <param name="name">The namespace segment name.</param>
    /// <param name="members">Its member symbols.</param>
    /// <returns>The symbol.</returns>
    public static ScopeSymbol Namespace(string name, params IScopeSymbol[] members) =>
        new(name, ScopeSymbolKind.Namespace, null, false, members);
}

/// <summary>
///     Dictionary-backed <see cref="IScopeEnvironment" />: a slot name plus its root symbols.
/// </summary>
public sealed class ScopeEnvironment : IScopeEnvironment
{
    private readonly Dictionary<string, IScopeSymbol> _roots;

    /// <summary>Creates a per-slot environment.</summary>
    /// <param name="slotName">The slot's diagnostic display name (e.g. <c>where:</c>).</param>
    /// <param name="roots">The slot's visible roots.</param>
    public ScopeEnvironment(string slotName, IEnumerable<IScopeSymbol> roots)
    {
        ArgumentNullException.ThrowIfNull(slotName);
        ArgumentNullException.ThrowIfNull(roots);
        SlotName = slotName;
        _roots = roots.ToDictionary(r => r.Name, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string SlotName { get; }

    /// <inheritdoc />
    public bool TryGetRoot(string name, [NotNullWhen(true)] out IScopeSymbol? root) =>
        _roots.TryGetValue(name, out root);

    /// <inheritdoc />
    public IEnumerable<string> RootNames => _roots.Keys;
}
