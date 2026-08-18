# CS2DemoKit.Analysis.Rules

The zero-dependency semantic core of the Rulesets v2 expression DSL used to author CS2 demo
analysis rules: lexer, parser, canonical AST + normalizer, resolver, typed checker, and
`RuleHasher` (resolved-identity canonical hashing — two expressions that are structurally
identical, modulo naming, hash to the same value). Deliberately BCL-only: no protobuf, no
`CS2DemoKit.Analysis`, no YAML, no engine. Reach for this package when you're building rule
*tooling* — an editor, a linter, an upload-validation service — that must not drag in the
evaluation engine or its dependency graph.

## Scope: what this package validates, and what it doesn't

This package validates and canonically hashes **one expression at a time** against a scope you
supply. It does not know what a "ruleset" or a "highlight" is, and it does not compose multiple
rule documents together. If you need whole-set composition diagnostics — cross-ruleset conflicts,
resolving identifiers against the shipped catalog — that lives one layer up:
`DemoAnalysis.ValidateRulesets(...)` in `CS2DemoKit.Analysis`.

## Example: parse and canonically hash an expression

```csharp
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.Rules.Scopes;

// A scope declares exactly the identifiers an expression is allowed to reference, and how each
// one hashes. "kills" is a *stat* reference (hashes by the node it points at, never by name);
// "player.health" is a plain readable value.
IScopeEnvironment scope = new ScopeEnvironment("where:",
[
    ScopeSymbol.Stat("kills", RulesType.Int),
    ScopeSymbol.Namespace("player", ScopeSymbol.Value("health", RulesType.Int))
]);

// Lex -> parse -> normalize -> resolve -> type-check, in one call.
LanguageResult<CheckedExpression> result =
    ExpressionPipeline.Analyze("kills > 1 and player.health > 0", scope);

if (!result.Success)
{
    foreach (Diagnostic d in result.Diagnostics)
    {
        Console.Error.WriteLine(d); // "(line,col): message [code]"
    }

    return;
}

CheckedExpression expr = result.Require();

// Canonical hashing needs the resolved hash of every STAT this expression references — the
// engine computes those bottom-up (a stat's own node is hashed before anything referencing it),
// and stat-reference cycles are a build error so the recursion always terminates. A tool with no
// engine behind it (a linter, an editor) can supply any consistent source; this one mirrors the
// project's own test fixture.
IStatHashSource statHashes = new FakeStatHashSource();
string hashHex = ExpressionHasher.ComputeHashHex(expr, statHashes);
Console.WriteLine(hashHex);

sealed class FakeStatHashSource : IStatHashSource
{
    public ReadOnlyMemory<byte> GetStatHash(ResolvedReference reference) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"fake-node:{reference.StatPath}"));
}
```

Diagnostics never come back as exceptions — every stage (`ExpressionLexer`, `ExpressionParser`,
`ExpressionNormalizer`, `ExpressionChecker`) reports user-input problems as a `LanguageResult<T>`
carrying a `Diagnostic` list (stable `Code`, position, offending text, and — for unresolved names —
ranked did-you-mean suggestions). `LanguageResult<T>.Require()` throws only for genuine programmer
misuse, such as calling it on a failed result.

The individual pipeline stages (`ExpressionLexer.Tokenize`, `ExpressionParser.Parse`,
`ExpressionNormalizer.Normalize`, `ExpressionChecker.Check`) are public too, for tools that need an
intermediate artifact — an editor that only needs token spans for syntax highlighting has no reason
to run the full pipeline.

## License

MIT.
