#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Rules.Ast;

#endregion

namespace CS2DemoKit.Analysis.Rules.Normalization;

/// <summary>
///     Environment inputs of the normalizer (spec §5 rows 3–5). The semantic core is a leaf
///     library: the demo tick rate, the <c>define:</c> table, and the view's <c>match:</c>
///     key catalog all live upstream (the v2 loader / <c>rules check</c>), so they arrive
///     through this options object.
/// </summary>
public sealed class NormalizerOptions
{
    /// <summary>The shared default: 64 ticks/s, no defines, no match-binding lowering.</summary>
    public static NormalizerOptions Default { get; } = new();

    /// <summary>
    ///     Tick rate used to fold duration literals to int tick constants (spec §5 row 3).
    ///     Defaults to the parser's own 64/s assumption used by the demo-less
    ///     <c>rules check</c> path; loaders supply <c>ParsedDemo.TickRate</c> when a demo is
    ///     present. Folding rounds with <see cref="MidpointRounding.AwayFromZero" />.
    /// </summary>
    public double TicksPerSecond { get; init; } = 64.0;

    /// <summary>
    ///     Resolves a <c>define:</c> name to its expression body for inlining at use sites
    ///     before hashing (spec §5 row 4). Return null for names that are not defines — they
    ///     then resolve normally against the scope environment. Bodies are AST-substituted
    ///     and recursively normalized; cycles are reported as diagnostics.
    /// </summary>
    public Func<string, ExpressionNode?>? DefineLookup { get; init; }

    /// <summary>
    ///     Lowers structured <c>match:</c> bindings to their <c>where:</c>-equivalent
    ///     comparisons in the view's fixed catalog key order (spec §5 row 5). Required by
    ///     <see cref="ExpressionNormalizer.NormalizeMatchBindings" /> when bindings are present.
    /// </summary>
    public IMatchBindingLowering? MatchBindingLowering { get; init; }
}

/// <summary>One structured <c>match:</c> binding (e.g. <c>weapon: "ak47"</c>) awaiting lowering.</summary>
/// <param name="Key">The binding key as written in the ruleset (e.g. <c>weapon</c>).</param>
/// <param name="Value">The bound value expression (typically a literal).</param>
public sealed record MatchBinding(string Key, ExpressionNode Value);

/// <summary>
///     The loader-supplied hook that maps one <c>match:</c> binding onto its
///     <c>where:</c>-equivalent comparison AST plus the key's position in the view's fixed
///     catalog key order, so structured and free-form spellings of the same constraint hash
///     identically (spec §5 row 5).
/// </summary>
public interface IMatchBindingLowering
{
    /// <summary>Attempts to lower a binding.</summary>
    /// <param name="binding">The binding to lower.</param>
    /// <param name="lowered">The <c>where:</c>-equivalent comparison AST; null when the key is unknown.</param>
    /// <param name="keyOrder">The key's index in the view's catalog key order; 0 when unknown.</param>
    /// <returns>True when the key is a known <c>match:</c> key of the view.</returns>
    bool TryLower(MatchBinding binding, [NotNullWhen(true)] out ExpressionNode? lowered, out int keyOrder);
}
