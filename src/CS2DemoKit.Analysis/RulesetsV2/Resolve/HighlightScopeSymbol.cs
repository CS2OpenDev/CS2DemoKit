#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The sibling-scope symbol for a highlight: a <see cref="ScopeSymbolKind.Stat" /> whose bare
///     read is the per-round fired bool and which exposes a real <c>count</c> member (the automatic
///     match-scoped <c>&lt;id&gt;.count</c> node). The base <see cref="ScopeSymbol.Stat" />
///     factory carries no members and the checker's pseudo-<c>.count</c> only fires on lists, so a
///     highlight's <c>.count</c> needs this dedicated symbol to resolve as a stat reference — which
///     is what lets the stat-reference cycle pre-pass walk highlight <c>.count</c> reads.
/// </summary>
public sealed class HighlightScopeSymbol : IScopeSymbol
{
    private readonly IScopeSymbol _count;

    /// <summary>Creates a highlight scope symbol.</summary>
    /// <param name="id">The highlight id (the bare reference name).</param>
    public HighlightScopeSymbol(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Name = id;
        _count = ScopeSymbol.Stat("count", RulesType.Int);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ScopeSymbolKind Kind => ScopeSymbolKind.Stat;

    /// <summary>The bare highlight read: the per-round fired bool (spec §7 table-column semantics).</summary>
    public RulesType? ValueType => RulesType.Bool;

    /// <inheritdoc />
    public bool SupportsSetTest => false;

    /// <inheritdoc />
    public bool TryGetMember(string name, [NotNullWhen(true)] out IScopeSymbol? member)
    {
        if (string.Equals(name, "count", StringComparison.Ordinal))
        {
            member = _count;
            return true;
        }

        member = null;
        return false;
    }

    /// <inheritdoc />
    public IEnumerable<string> MemberNames => ["count"];
}
