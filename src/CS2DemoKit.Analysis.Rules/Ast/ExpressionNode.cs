namespace CS2DemoKit.Analysis.Rules.Ast;

/// <summary>
///     Base of the canonical AST — the dedup and hashing unit of the v2 rules language
///     (spec §5). Nodes are immutable and compare by <em>structural value</em>: two nodes are
///     equal exactly when their deterministic canonical serializations
///     (<see cref="CanonicalText" />) are identical. <see cref="Span" /> is diagnostic
///     payload only and never participates in equality or hashing — two spellings of the
///     same expression at different positions are equal and hash together.
/// </summary>
public abstract class ExpressionNode : IEquatable<ExpressionNode>
{
    private string? _canonicalText;

    protected private ExpressionNode(SourceSpan span) => Span = span;

    /// <summary>Source position of the node. Excluded from canonical identity.</summary>
    public SourceSpan Span { get; }

    /// <summary>
    ///     The node's deterministic canonical serialization (a parenthesized prefix form,
    ///     e.g. <c>(gt (ref kills) (int 1))</c>). Whitespace, word-form operators, and
    ///     redundant parentheses have already vanished by construction (spec §5 rows 1–2).
    ///     This text drives structural equality; the resolved-identity hash additionally
    ///     replaces stat references with their referenced node hashes (spec §6 row 6).
    /// </summary>
    public string CanonicalText => _canonicalText ??= CanonicalWriter.Write(this);

    /// <summary>Structural value equality via canonical serialization; spans are ignored.</summary>
    /// <param name="other">The node to compare with.</param>
    /// <returns>True when both nodes serialize identically.</returns>
    public bool Equals(ExpressionNode? other) =>
        other is not null
        && (ReferenceEquals(this, other) || string.Equals(CanonicalText, other.CanonicalText, StringComparison.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ExpressionNode);

    /// <inheritdoc />
    public override int GetHashCode() => CanonicalText.GetHashCode(StringComparison.Ordinal);

    /// <summary>Returns <see cref="CanonicalText" />.</summary>
    /// <returns>The canonical serialization.</returns>
    public override string ToString() => CanonicalText;
}
