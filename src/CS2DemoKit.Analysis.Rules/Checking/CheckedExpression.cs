#region

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.Rules.Checking;

/// <summary>
///     One resolved reference inside a checked expression: the reference node, its full
///     dotted path, its read type, and — for stat references — the stat portion of the path
///     so the resolved-identity hasher can substitute the referenced node's own hash for the
///     name (spec §6 row 6).
/// </summary>
public sealed class ResolvedReference
{
    internal ResolvedReference(ReferenceNode node, string path, RulesType type, IScopeSymbol symbol,
        bool isStatReference, string? statPath, ImmutableArray<string> tailSegments)
    {
        Node = node;
        Path = path;
        Type = type;
        Symbol = symbol;
        IsStatReference = isStatReference;
        StatPath = statPath;
        TailSegments = tailSegments;
    }

    /// <summary>The reference node this resolution belongs to.</summary>
    public ReferenceNode Node { get; }

    /// <summary>The full dotted path as written (e.g. <c>round.bomb.was_planted</c>).</summary>
    public string Path { get; }

    /// <summary>The reference's read type, after any pseudo-member (<c>.count</c> → int, <c>.set</c> → bool).</summary>
    public RulesType Type { get; }

    /// <summary>
    ///     The deepest named symbol of the walk: the stat symbol for stat references,
    ///     otherwise the terminal value/param symbol.
    /// </summary>
    public IScopeSymbol Symbol { get; }

    /// <summary>True when the reference reads a stat node — those hash by node identity, not name.</summary>
    public bool IsStatReference { get; }

    /// <summary>
    ///     The dotted path of the stat portion (e.g. <c>otherruleset.kills</c> of
    ///     <c>otherruleset.kills.count</c>); null for non-stat references. This is the key the
    ///     <c>IStatHashSource</c> callback resolves to the referenced node's own hash bytes.
    /// </summary>
    public string? StatPath { get; }

    /// <summary>Segments after the stat portion (pseudo-members like <c>count</c>); empty for non-stat references.</summary>
    public ImmutableArray<string> TailSegments { get; }
}

/// <summary>
///     A successfully resolved and type-checked expression: the (normalized) AST, its result
///     type, the per-node resolution table, and the statically enumerable read set of
///     spec §3.6 — every reference is resolved at compile time, which feeds lazy scanner
///     activation and <c>DeclaredReads</c> edge ordering downstream.
/// </summary>
public sealed class CheckedExpression
{
    private readonly Dictionary<ReferenceNode, ResolvedReference> _byNode;

    internal CheckedExpression(ExpressionNode root, RulesType resultType,
        IReadOnlyList<ResolvedReference> references, Dictionary<ReferenceNode, ResolvedReference> byNode)
    {
        Root = root;
        ResultType = resultType;
        References = references;
        _byNode = byNode;
    }

    /// <summary>The checked AST root (check after normalization: this is the hashing form).</summary>
    public ExpressionNode Root { get; }

    /// <summary>The expression's language-level result type.</summary>
    public RulesType ResultType { get; }

    /// <summary>
    ///     The expression's read set: every distinct reference path, in first-occurrence
    ///     source order (spec §3.6 — statically enumerable by construction).
    /// </summary>
    public IReadOnlyList<ResolvedReference> References { get; }

    /// <summary>Looks up the resolution recorded for a specific reference node instance of <see cref="Root" />.</summary>
    /// <param name="node">The reference node (compared by object identity, not value).</param>
    /// <param name="resolution">The recorded resolution when present.</param>
    /// <returns>True when the node belongs to this expression.</returns>
    public bool TryGetResolution(ReferenceNode node, [NotNullWhen(true)] out ResolvedReference? resolution)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _byNode.TryGetValue(node, out resolution);
    }
}
