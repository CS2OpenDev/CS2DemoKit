namespace CS2DemoKit.Analysis.RulesetsV2.Compile;

/// <summary>
///     Planner knobs. The one live knob is <see cref="UseEnvLowering" />: the
///     env-vs-constant duality. Constant lowering (the default) compiles each per-player node's
///     actor binding to a fixed <c>event.&lt;SlotField&gt; == &lt;slot literal&gt;</c> at
///     materialization — one compiled delegate per player, exactly like v1. Env lowering compiles
///     one <c>Func&lt;PlayerEnv, TEvent, …&gt;</c> shared across players and reads the slot from the
///     env at fire time. Both must evaluate identically (the equivalence battery gates the
///     default flip); until that battery passes on the full corpus the fallback is constant
///     lowering, so the pilot runs under it.
/// </summary>
/// <param name="UseEnvLowering">
///     When true, per-player actor bindings lower to a shared env-parameterized delegate;
///     when false (default), to a per-player constant-slot delegate identical to v1.
/// </param>
public sealed record RulesetCompilerOptions(bool UseEnvLowering = false)
{
    /// <summary>The default options — constant lowering (env lowering off until the C7 battery passes).</summary>
    public static RulesetCompilerOptions Default { get; } = new();
}
