#region

using CS2DemoKit.Analysis.Rules.Lexing;

#endregion

namespace CS2DemoKit.Analysis.Rules.Ast;

/// <summary>
///     A 64-bit signed integer literal. Also the folded form of duration literals after normalization (spec §5 row
///     3).
/// </summary>
public sealed class IntLiteralNode : ExpressionNode
{
    /// <summary>Creates an integer literal node.</summary>
    /// <param name="value">The literal's value.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public IntLiteralNode(long value, SourceSpan span = default) : base(span) => Value = value;

    /// <summary>The literal's value.</summary>
    public long Value { get; }
}

/// <summary>An IEEE-double float literal (<c>digits.digits</c>).</summary>
public sealed class FloatLiteralNode : ExpressionNode
{
    /// <summary>Creates a float literal node.</summary>
    /// <param name="value">The literal's value.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public FloatLiteralNode(double value, SourceSpan span = default) : base(span) => Value = value;

    /// <summary>The literal's value.</summary>
    public double Value { get; }
}

/// <summary>A string literal; the value is the unescaped content.</summary>
public sealed class StringLiteralNode : ExpressionNode
{
    /// <summary>Creates a string literal node.</summary>
    /// <param name="value">The unescaped string content.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public StringLiteralNode(string value, SourceSpan span = default) : base(span)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The unescaped string content.</summary>
    public string Value { get; }
}

/// <summary>A <c>true</c> / <c>false</c> literal.</summary>
public sealed class BoolLiteralNode : ExpressionNode
{
    /// <summary>Creates a bool literal node.</summary>
    /// <param name="value">The literal's value.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public BoolLiteralNode(bool value, SourceSpan span = default) : base(span) => Value = value;

    /// <summary>The literal's value.</summary>
    public bool Value { get; }
}

/// <summary>The <c>null</c> literal — the explicit presence test operand of spec §3.3.</summary>
public sealed class NullLiteralNode : ExpressionNode
{
    /// <summary>Creates a null literal node.</summary>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public NullLiteralNode(SourceSpan span = default) : base(span)
    {
    }
}

/// <summary>
///     A duration literal (<c>10s</c>, <c>0.5s</c>, <c>500ms</c>). Present only between
///     parsing and normalization: the normalizer folds durations to
///     <see cref="IntLiteralNode" /> tick constants before hashing (spec §5 row 3), so
///     <c>5s</c> and <c>320</c> dedup together at 64 ticks/s.
/// </summary>
public sealed class DurationLiteralNode : ExpressionNode
{
    /// <summary>Creates a duration literal node.</summary>
    /// <param name="magnitude">The numeric magnitude as written (e.g. 0.5 for <c>0.5s</c>).</param>
    /// <param name="unit">The unit suffix the literal was written with.</param>
    /// <param name="span">Source position; excluded from canonical identity.</param>
    public DurationLiteralNode(double magnitude, DurationUnit unit, SourceSpan span = default) : base(span)
    {
        Magnitude = magnitude;
        Unit = unit;
    }

    /// <summary>The numeric magnitude as written.</summary>
    public double Magnitude { get; }

    /// <summary>The unit suffix the literal was written with.</summary>
    public DurationUnit Unit { get; }
}
