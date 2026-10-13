# Migrating to 0.14.0

This page is the order in which a consumer meets the 0.14.0 changes. The per-break reference, one
entry per surface that moved, is the 0.14.0 block under
[Compatibility notes in `releasing.md`](releasing.md#compatibility-notes-worth-carrying-into-a-release).
A consumer still on 0.12.0 goes through [`migrating-to-0.13.md`](migrating-to-0.13.md) first.

## 1. Pin the family together

```xml
<PackageVersion Include="CS2DemoKit.Parser" Version="0.14.0"/>
<PackageVersion Include="CS2DemoKit.Analysis" Version="0.14.0"/>
<PackageVersion Include="CS2DemoKit.Analysis.Rules" Version="0.14.0"/>
```

Intra-family pins are exact, so a mixed set fails to restore rather than half-working.

## 2. What stays put

`DemoAnalysis.Run`, `Build`, `Evaluate` and `ValidateRulesets`, `BuildResult`'s constructor, the
rules syntax and every output projection keep their shape. The shipped rulesets build and count as
before.

## 3. Compile errors, in the order they show up

None so far.

## 4. Behaviour that moved without a compile error

**A ruleset that cannot be lowered is excluded, not thrown.** Through 0.13.0 a ruleset that resolved
but could not be lowered threw: a `for: each_player` one from the first player's materialization,
which aborted `Evaluate`, and a `for: match` or `for: each_team` one from `Build`. It now lands on
`BuildResult.ExcludedRulesets` with a `build.lowering-failed` diagnostic, and its siblings build and
evaluate. A host that caught the exception to quarantine a ruleset reads the list instead:

```csharp
BuildResult build = DemoAnalysis.Build(demo, docs);
foreach (ExcludedRuleset excluded in build.ExcludedRulesets)
{
    // composition drops first, then lowering drops; each carries its code and position
}
```

`RulesetDiagnostics` still carries only composition diagnostics, so a host that reads only that
list misses lowering failures. A ruleset that reads an excluded one through `use:` is excluded with
it.

**`ValidateRulesets` rejects three shapes it used to pass.** A net trigger on a kind that has no
net path (`resolve.trigger.unsupported-for-kind`), a `while:` gate the planner cannot carry
(`resolve.while.unsupported`), and a non-int `capture keep: list`
(`resolve.keep-list.element-type`). Each failed at build or evaluation before, so a directory that
validated with one of them never produced its numbers. The releasing note lists the exact shapes.

## 5. New things worth adopting

`BuildDiagnosticCodes.LoweringFailed` (`build.lowering-failed`) and the three new
`ResolveDiagnosticCodes` constants are stable codes to key on.
