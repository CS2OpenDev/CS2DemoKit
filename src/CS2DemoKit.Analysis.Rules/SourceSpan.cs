namespace CS2DemoKit.Analysis.Rules;

/// <summary>
///     A half-open character range inside a single expression source string, with the 1-based
///     line/column of its first character. Positions are relative to the expression scalar
///     itself — the YAML loader that owns the enclosing document adds the mapping position of
///     the scalar to produce the spec §8 <c>file(line,col)</c> prefix. Spans are diagnostic
///     payload only: they never participate in canonical-AST identity (two spellings of the
///     same expression at different positions must hash equal).
/// </summary>
/// <param name="Offset">0-based character offset of the first character within the expression source.</param>
/// <param name="Length">Number of characters covered; zero for end-of-input positions.</param>
/// <param name="Line">1-based line number of the first character (expressions may be multi-line YAML scalars).</param>
/// <param name="Column">1-based column number of the first character within its line.</param>
public readonly record struct SourceSpan(int Offset, int Length, int Line, int Column)
{
    /// <summary>
    ///     Merges two spans into the smallest span covering both. Line/column come from the
    ///     earlier span. Used when a parent AST node covers its children.
    /// </summary>
    /// <param name="left">The first span (typically the leftmost token of a construct).</param>
    /// <param name="right">The second span (typically the rightmost token of a construct).</param>
    /// <returns>A span covering both inputs.</returns>
    public static SourceSpan Cover(SourceSpan left, SourceSpan right)
    {
        SourceSpan first = left.Offset <= right.Offset ? left : right;
        int end = Math.Max(left.Offset + left.Length, right.Offset + right.Length);
        return new SourceSpan(first.Offset, end - first.Offset, first.Line, first.Column);
    }
}
