#region

using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Normalization;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.Rules;

/// <summary>
///     The front-half pipeline in one call: lex → parse → normalize (spec §5) → resolve +
///     type-check (spec §3/§4). This is the shape the three consumers (the v2 loader,
///     <c>rules check</c>, the workbench) use; the individual stages stay public for tools
///     that need intermediate artifacts.
/// </summary>
public static class ExpressionPipeline
{
    /// <summary>Runs the full front half over an expression source string.</summary>
    /// <param name="source">The expression source (a YAML scalar).</param>
    /// <param name="scope">The slot's scope environment.</param>
    /// <param name="options">Normalizer inputs (tick rate, defines, match-binding lowering); null = defaults.</param>
    /// <param name="expectedType">The slot's required result type, when it demands one.</param>
    /// <returns>The checked expression (whose <see cref="CheckedExpression.Root" /> is the hashing form), or diagnostics.</returns>
    public static LanguageResult<CheckedExpression> Analyze(string source, IScopeEnvironment scope,
        NormalizerOptions? options = null, RulesType? expectedType = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);

        LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(source);
        if (!parsed.Success)
        {
            return LanguageResult.Fail<CheckedExpression>(parsed.Diagnostics);
        }

        LanguageResult<ExpressionNode> normalized = ExpressionNormalizer.Normalize(parsed.Require(), options);
        if (!normalized.Success)
        {
            return LanguageResult.Fail<CheckedExpression>(normalized.Diagnostics);
        }

        return ExpressionChecker.Check(normalized.Require(), scope, expectedType);
    }
}
