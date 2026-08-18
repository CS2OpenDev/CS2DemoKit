#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     Holds the set of available <see cref="DemoSourceProfile" />
///     implementations and resolves a parsed demo's lightweight
///     <see cref="DemoProfile" /> to the most-specific matching engine
///     profile.
/// </summary>
/// <remarks>
///     Internal profiles ship pre-registered. <see cref="Register" /> reserves
///     the public extension point for a future custom-DLL loader; v0.0.2 only
///     uses the built-ins.
///     Selection rules:
///     <list type="number">
///         <item>
///             <description>Filter to profiles whose <see cref="DemoSourceProfile.Kind" /> matches the parsed demo.</description>
///         </item>
///         <item>
///             <description>
///                 Of those, keep only the ones whose <c>[MinBuildNumber, MaxBuildNumber]</c> range contains the
///                 build number.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Pick the one with the tightest version range (smallest range width). Stable secondary order by
///                 registration order.
///             </description>
///         </item>
///         <item>
///             <description>
///                 If nothing matches, fall back to <see cref="DefaultFallback" /> (a vanilla GOTV profile) so
///                 analysis never crashes on an unrecognised demo.
///             </description>
///         </item>
///     </list>
/// </remarks>
public static class DemoSourceProfileRegistry
{
    private static readonly List<DemoSourceProfile> _profiles = new()
    {
        new Cs2GotvProfile(),
        new Cs2HltvProfile(),
        new Cs2FaceitProfile(),
        new Cs2PovProfile()
    };

    /// <summary>All currently-registered profiles in registration order.</summary>
    public static IReadOnlyList<DemoSourceProfile> All => _profiles;

    /// <summary>The fallback profile used when no registered profile matches.</summary>
    public static DemoSourceProfile DefaultFallback { get; } = new Cs2GotvProfile();

    /// <summary>
    ///     Registers an additional profile. Reserved for future custom-DLL
    ///     loader support; not used by the engine itself in v0.0.2.
    /// </summary>
    public static void Register(DemoSourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles.Add(profile);
    }

    /// <summary>
    ///     Resolves the profile for a demo whose actual wire vocabulary is known, which lets a
    ///     source dialect be identified that the header cannot distinguish.
    /// </summary>
    /// <remarks>
    ///     Third-party tournament servers (ESL, BLAST) record through SourceTV and so present
    ///     exactly like Valve matchmaking in the header, but they never emit
    ///     <c>round_officially_ended</c> — <c>cs_pre_restart</c> is their per-round end marker.
    ///     Resolving that off the header is impossible; resolving it off the events the demo
    ///     actually contains is exact. See <see cref="Cs2GotvPreRestartProfile" /> for why this has
    ///     to be a distinct profile rather than an extra event on the GOTV binding.
    /// </remarks>
    /// <param name="demo">The parser-side identification record.</param>
    /// <param name="observedEvents">Names of the game events the demo actually fires.</param>
    public static DemoSourceProfile Resolve(DemoProfile demo, IReadOnlySet<string> observedEvents)
    {
        ArgumentNullException.ThrowIfNull(observedEvents);
        DemoSourceProfile resolved = Resolve(demo);

        // Only the plain GOTV profile has a pre-restart dialect. Cs2FaceitProfile carries the same
        // round-end shape and could plausibly need one too, but no FACEIT demo is on hand to
        // establish its actual vocabulary — left alone deliberately rather than guessed at. An
        // empty event set (a demo we could not decode events for) leaves the header-based answer
        // alone.
        if (resolved is Cs2GotvProfile and not Cs2GotvPreRestartProfile
            && observedEvents.Count > 0
            && !observedEvents.Contains("round_officially_ended")
            && observedEvents.Contains("cs_pre_restart"))
        {
            return new Cs2GotvPreRestartProfile();
        }

        return resolved;
    }

    /// <summary>
    ///     Returns the most-specific profile that matches the given demo
    ///     identification. Never returns null — falls back to
    ///     <see cref="DefaultFallback" /> when no profile applies.
    /// </summary>
    public static DemoSourceProfile Resolve(DemoProfile demo)
    {
        DemoSourceProfile? best = null;
        long bestRangeWidth = long.MaxValue;

        foreach (DemoSourceProfile candidate in _profiles)
        {
            if (candidate.Kind != demo.SourceKind)
            {
                continue;
            }

            if (demo.BuildNumber < candidate.MinBuildNumber)
            {
                continue;
            }

            if (demo.BuildNumber > candidate.MaxBuildNumber)
            {
                continue;
            }

            long rangeWidth =
                (long)candidate.MaxBuildNumber - candidate.MinBuildNumber;

            if (rangeWidth < bestRangeWidth)
            {
                best = candidate;
                bestRangeWidth = rangeWidth;
            }
        }

        return best ?? DefaultFallback;
    }
}
