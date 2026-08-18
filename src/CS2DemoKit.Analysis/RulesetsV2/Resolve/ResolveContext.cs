namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The inputs that distinguish the two runs of the resolve → canonicalize → check pipeline
///     across the load-vs-build boundary.
///     <list type="bullet">
///         <item>
///             <b>Load (demo-less):</b> <see cref="Draft" /> — 64 ticks/s, no profile, params stay
///             symbolic (exposed as a <c>params.*</c> namespace, never bound to literals, never
///             hashed). Produces the diagnostics-only <see cref="CheckedRulesetDraft" />.
///         </item>
///         <item>
///             <b>Build (demo in hand):</b> <see cref="Build" /> — the demo's real
///             <c>ParsedDemo.TickRate</c>, the active source profile (drives concrete-event
///             resolution and coverage skips), and the install's param values bound to literals
///             before hashing (decision 2). Produces the planner's <see cref="CheckedRuleset" />.
///         </item>
///     </list>
/// </summary>
public sealed class ResolveContext
{
    private ResolveContext(bool isBuild, double ticksPerSecond, string? profileId,
        IReadOnlyDictionary<string, object?>? paramValues)
    {
        IsBuild = isBuild;
        TicksPerSecond = ticksPerSecond;
        ProfileId = profileId;
        ParamValues = paramValues;
    }

    /// <summary>True for a build-time re-pass (params bound to literals, per-profile view binding, coverage skips).</summary>
    public bool IsBuild { get; }

    /// <summary>The tick rate used to fold duration literals/params (spec §5 row 3). 64 at demo-less load.</summary>
    public double TicksPerSecond { get; }

    /// <summary>
    ///     The active demo-source profile id (e.g. <c>Cs2GotvProfile</c>), used to resolve views to
    ///     their concrete wire events and decide coverage skips. Null at demo-less load.
    /// </summary>
    public string? ProfileId { get; }

    /// <summary>
    ///     The install's param values, keyed by param name (a subset — unbound params fall back to
    ///     their declared default). Null at demo-less load, where params stay symbolic.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ParamValues { get; }

    /// <summary>The demo-less load context: 64 ticks/s, symbolic params, no profile.</summary>
    public static ResolveContext Draft { get; } = new(false, 64.0, null, null);

    /// <summary>Builds a demo-less draft context at a non-default tick rate (rare; tests only).</summary>
    /// <param name="ticksPerSecond">The tick rate.</param>
    /// <returns>The draft context.</returns>
    public static ResolveContext DraftAt(double ticksPerSecond) => new(false, ticksPerSecond, null, null);

    /// <summary>Builds a build-time context.</summary>
    /// <param name="ticksPerSecond">The demo's real tick rate.</param>
    /// <param name="profileId">The active demo-source profile id.</param>
    /// <param name="paramValues">The install's param values (null = all defaults).</param>
    /// <returns>The build context.</returns>
    public static ResolveContext Build(double ticksPerSecond, string profileId,
        IReadOnlyDictionary<string, object?>? paramValues = null)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return new ResolveContext(true, ticksPerSecond, profileId, paramValues);
    }
}
