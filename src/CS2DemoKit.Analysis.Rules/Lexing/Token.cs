namespace CS2DemoKit.Analysis.Rules.Lexing;

/// <summary>
///     One lexed token. <see cref="Text" /> is always the exact source slice; literal tokens
///     additionally carry their parsed value (<see cref="IntegerValue" />,
///     <see cref="FloatValue" />, <see cref="StringValue" />, or the duration pair
///     <see cref="DurationMagnitude" /> / <see cref="Unit" />).
/// </summary>
/// <param name="Kind">The token's kind.</param>
/// <param name="Text">The exact source text of the token (empty for <see cref="TokenKind.EndOfInput" />).</param>
/// <param name="Span">The token's position within the expression source.</param>
public sealed record Token(TokenKind Kind, string Text, SourceSpan Span)
{
    /// <summary>Parsed value of an <see cref="TokenKind.IntLiteral" />; 0 otherwise.</summary>
    public long IntegerValue { get; init; }

    /// <summary>Parsed value of a <see cref="TokenKind.FloatLiteral" />; 0 otherwise.</summary>
    public double FloatValue { get; init; }

    /// <summary>Unescaped content of a <see cref="TokenKind.StringLiteral" />; null otherwise.</summary>
    public string? StringValue { get; init; }

    /// <summary>Numeric magnitude of a <see cref="TokenKind.DurationLiteral" /> (e.g. 0.5 for <c>0.5s</c>); 0 otherwise.</summary>
    public double DurationMagnitude { get; init; }

    /// <summary>Unit of a <see cref="TokenKind.DurationLiteral" />; <see cref="DurationUnit.None" /> otherwise.</summary>
    public DurationUnit Unit { get; init; }
}
