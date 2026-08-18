#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Analysis.Rules.Scopes;

/// <summary>Classification of a name in a scope environment (spec §4 namespace tree).</summary>
public enum ScopeSymbolKind
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>
    ///     A pure namespace segment (<c>event</c>, <c>round.bomb</c>) — has members but is not
    ///     itself readable as a value.
    /// </summary>
    Namespace,

    /// <summary>A readable value: event fields, contexts, providers, role-handle members, define lists/maps.</summary>
    Value,

    /// <summary>
    ///     A stat node reference (bare same-ruleset id, or the stat segment of a qualified
    ///     <c>ruleset.stat</c> read). Stat references hash by the referenced node's own
    ///     resolved structural hash, never by name (spec §6 row 6).
    /// </summary>
    Stat,

    /// <summary>A <c>params:</c> binding — a compile-time constant bound before hashing.</summary>
    Param
}

/// <summary>
///     One resolvable name in the namespace tree: its kind, its language-level type (null for
///     pure namespaces), and its member table. Implementations come from the Catalog-backed
///     loader; the semantic core only walks the tree.
/// </summary>
public interface IScopeSymbol
{
    /// <summary>The symbol's own name (one segment, e.g. <c>bomb</c>).</summary>
    string Name { get; }

    /// <summary>The symbol's classification.</summary>
    ScopeSymbolKind Kind { get; }

    /// <summary>The value's language-level type; null when the symbol is a pure namespace.</summary>
    RulesType? ValueType { get; }

    /// <summary>
    ///     True when the symbol supports the <c>.set</c> presence test as a scalar capture
    ///     stat (spec §3.5). List-typed symbols support <c>.set</c> regardless (sugar for
    ///     <c>count &gt; 0</c>).
    /// </summary>
    bool SupportsSetTest { get; }

    /// <summary>All direct member names, for diagnostics and did-you-mean candidates (spec §8).</summary>
    IEnumerable<string> MemberNames { get; }

    /// <summary>Looks up a direct member.</summary>
    /// <param name="name">The member segment.</param>
    /// <param name="member">The member symbol when found.</param>
    /// <returns>True when the member exists.</returns>
    bool TryGetMember(string name, [NotNullWhen(true)] out IScopeSymbol? member);
}

/// <summary>
///     The per-slot scope environment references resolve against (spec §4): which roots are
///     visible depends on the slot (an event-triggered <c>where:</c> sees <c>event.*</c> and
///     role handles; a <c>when:</c> flag sees stats and contexts but no <c>event.*</c>).
///     Supplied by the Catalog-backed loader; faked in tests.
/// </summary>
public interface IScopeEnvironment
{
    /// <summary>
    ///     The slot's display name for diagnostics (e.g. <c>where:</c>). Rendered verbatim in
    ///     the out-of-scope root error, which also lists <see cref="RootNames" />.
    /// </summary>
    string SlotName { get; }

    /// <summary>All in-scope root names — the slot's allowed roots, named in resolution errors (spec §4/§8).</summary>
    IEnumerable<string> RootNames { get; }

    /// <summary>Looks up a root name (the first segment of a reference).</summary>
    /// <param name="name">The reference head.</param>
    /// <param name="root">The root symbol when found.</param>
    /// <returns>True when the root is in scope for this slot.</returns>
    bool TryGetRoot(string name, [NotNullWhen(true)] out IScopeSymbol? root);
}
