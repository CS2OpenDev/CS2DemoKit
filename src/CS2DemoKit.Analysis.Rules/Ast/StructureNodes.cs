#region

using System.Collections.Immutable;

#endregion

namespace CS2DemoKit.Analysis.Rules.Ast;

/// <summary>
///     A dotted reference: an identifier head extended by member accesses
///     (<c>event.Attacker</c>, <c>round.bomb.was_planted</c>, <c>myruleset.kills</c>).
///     The parser only builds the chain; resolution against the slot's scope environment is
///     the checker's job (spec §4). Member access on a pure identifier-headed chain collapses
///     into the reference's segments; member access on anything else becomes
///     <see cref="MemberAccessNode" />.
/// </summary>
public sealed class ReferenceNode : ExpressionNode
{
    private string? _path;

    /// <summary>Creates a reference from its segments.</summary>
    /// <param name="segments">The dotted segments, at least one.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public ReferenceNode(ImmutableArray<string> segments, SourceSpan span = default) : base(span)
    {
        if (segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("a reference needs at least one segment", nameof(segments));
        }

        Segments = segments;
    }

    /// <summary>The dotted segments, head first.</summary>
    public ImmutableArray<string> Segments { get; }

    /// <summary>The full dotted path, e.g. <c>round.bomb.was_planted</c>.</summary>
    public string Path => _path ??= string.Join('.', Segments);

    /// <summary>Builds a reference from a dotted path string. Convenience for hooks and tests.</summary>
    /// <param name="dottedPath">The path, e.g. <c>event.weapon</c>.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    /// <returns>The reference node.</returns>
    public static ReferenceNode FromPath(string dottedPath, SourceSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(dottedPath);
        return new ReferenceNode([.. dottedPath.Split('.')], span);
    }

    /// <summary>Returns a new reference with one more member segment.</summary>
    /// <param name="segment">The member name to append.</param>
    /// <param name="span">The combined source span.</param>
    /// <returns>The extended reference.</returns>
    internal ReferenceNode Append(string segment, SourceSpan span) => new(Segments.Add(segment), span);
}

/// <summary>
///     Member access on a non-reference target (e.g. <c>xs[0].count</c>). Plain dotted
///     chains collapse into <see cref="ReferenceNode" /> instead.
/// </summary>
public sealed class MemberAccessNode : ExpressionNode
{
    /// <summary>Creates a member access node.</summary>
    /// <param name="target">The expression whose member is read.</param>
    /// <param name="memberName">The member name after the dot.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public MemberAccessNode(ExpressionNode target, string memberName, SourceSpan span = default) : base(span)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(memberName);
        Target = target;
        MemberName = memberName;
    }

    /// <summary>The expression whose member is read.</summary>
    public ExpressionNode Target { get; }

    /// <summary>The member name after the dot.</summary>
    public string MemberName { get; }
}

/// <summary>
///     Index access <c>target[index]</c>: bounds-checked list element reads (int index,
///     out-of-range → null) and <c>define:</c> map lookups (string key) — spec §2.
/// </summary>
public sealed class IndexAccessNode : ExpressionNode
{
    /// <summary>Creates an index access node.</summary>
    /// <param name="target">The list or map expression being indexed.</param>
    /// <param name="index">The index/key expression.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public IndexAccessNode(ExpressionNode target, ExpressionNode index, SourceSpan span = default) : base(span)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(index);
        Target = target;
        Index = index;
    }

    /// <summary>The list or map expression being indexed.</summary>
    public ExpressionNode Target { get; }

    /// <summary>The index/key expression.</summary>
    public ExpressionNode Index { get; }
}

/// <summary>A unary operation (<c>not x</c>, <c>-x</c>).</summary>
public sealed class UnaryNode : ExpressionNode
{
    /// <summary>Creates a unary node.</summary>
    /// <param name="op">The operator.</param>
    /// <param name="operand">The operand expression.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public UnaryNode(UnaryOperator op, ExpressionNode operand, SourceSpan span = default) : base(span)
    {
        ArgumentNullException.ThrowIfNull(operand);
        Operator = op;
        Operand = operand;
    }

    /// <summary>The operator.</summary>
    public UnaryOperator Operator { get; }

    /// <summary>The operand expression.</summary>
    public ExpressionNode Operand { get; }
}

/// <summary>A binary operation. Parentheses vanish here: the tree shape is the precedence (spec §5 row 2).</summary>
public sealed class BinaryNode : ExpressionNode
{
    /// <summary>Creates a binary node.</summary>
    /// <param name="op">The operator.</param>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public BinaryNode(BinaryOperator op, ExpressionNode left, ExpressionNode right, SourceSpan span = default)
        : base(span)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        Operator = op;
        Left = left;
        Right = right;
    }

    /// <summary>The operator.</summary>
    public BinaryOperator Operator { get; }

    /// <summary>The left operand.</summary>
    public ExpressionNode Left { get; }

    /// <summary>The right operand. For <see cref="BinaryOperator.In" /> this is a reference or list literal.</summary>
    public ExpressionNode Right { get; }
}

/// <summary>
///     A constant list literal — legal only as the right operand of <c>in</c> (spec §2).
///     Elements are scalar literals of one category (numbers, strings, or bools).
/// </summary>
public sealed class ListLiteralNode : ExpressionNode
{
    /// <summary>Creates a list literal node.</summary>
    /// <param name="items">The element literals; may be empty.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public ListLiteralNode(ImmutableArray<ExpressionNode> items, SourceSpan span = default) : base(span)
    {
        if (items.IsDefault)
        {
            throw new ArgumentException("items must be initialized (an empty list is fine)", nameof(items));
        }

        Items = items;
    }

    /// <summary>The element literals; may be empty.</summary>
    public ImmutableArray<ExpressionNode> Items { get; }
}

/// <summary>One <c>key: value</c> entry of a <see cref="MapLiteralNode" />.</summary>
/// <param name="Key">The string key (map keys are always strings — spec §3.4).</param>
/// <param name="Value">The value, a scalar literal (number or string) of the map's uniform value type.</param>
public readonly record struct MapEntry(string Key, ExpressionNode Value);

/// <summary>
///     A constant string-keyed map literal — the inlined body of a map-valued <c>define:</c>
///     (spec §3.4). Values are scalar literals of one uniform category (all numbers or all
///     strings); the map is only ever read through <c>ref[key]</c> subscript
///     (<see cref="IndexAccessNode" />), which yields the mapped value or <c>null</c> on a miss.
///     There is no map-literal surface syntax: a map only arises by inlining a map define, so
///     the node exists purely as the checked/hashed identity of that table.
/// </summary>
public sealed class MapLiteralNode : ExpressionNode
{
    /// <summary>Creates a map literal node.</summary>
    /// <param name="entries">The key/value entries; may be empty.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public MapLiteralNode(ImmutableArray<MapEntry> entries, SourceSpan span = default) : base(span)
    {
        if (entries.IsDefault)
        {
            throw new ArgumentException("entries must be initialized (an empty map is fine)", nameof(entries));
        }

        Entries = entries;
    }

    /// <summary>The key/value entries; may be empty. Author order is preserved; canonical identity sorts by key.</summary>
    public ImmutableArray<MapEntry> Entries { get; }
}

/// <summary>A call to one of the closed five functions (spec §2 / §3.7).</summary>
public sealed class CallNode : ExpressionNode
{
    /// <summary>Creates a call node.</summary>
    /// <param name="function">The function being called.</param>
    /// <param name="arguments">The argument expressions, already arity-checked by the parser.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public CallNode(RuleFunction function, ImmutableArray<ExpressionNode> arguments, SourceSpan span = default)
        : base(span)
    {
        if (arguments.IsDefault)
        {
            throw new ArgumentException("arguments must be initialized", nameof(arguments));
        }

        Function = function;
        Arguments = arguments;
    }

    /// <summary>The function being called.</summary>
    public RuleFunction Function { get; }

    /// <summary>The argument expressions.</summary>
    public ImmutableArray<ExpressionNode> Arguments { get; }
}
