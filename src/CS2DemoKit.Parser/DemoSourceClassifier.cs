namespace CS2DemoKit.Parser;

/// <summary>
///     Identifies a demo's recording source from its <c>CDemoFileHeader</c>
///     fields, primarily by inspecting the <c>client_name</c> set by the
///     recording proxy ("GOTV&lt;1&gt;", "HLTV", etc.).
/// </summary>
/// <remarks>
///     Heuristics are intentionally conservative — the classifier returns a
///     <see cref="DemoSourceKind.Unknown" /> profile (with a GOTV-leaning
///     feature set) when it can't make a confident call. Callers that know
///     better can pass an explicit profile to
///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile)" />.
/// </remarks>
public static class DemoSourceClassifier
{
    /// <summary>
    ///     Classifies the demo source from <c>CDemoFileHeader</c> fields. Returns the matched
    ///     <see cref="DemoProfile" /> or an <see cref="DemoSourceKind.Unknown" /> profile.
    /// </summary>
    public static DemoProfile Classify(
        string serverName,
        string clientName,
        string gameDirectory,
        int buildNumber)
    {
        // ── Client-name signals ──
        // HLTV proxies advertise "HLTV" in their client name (Pro broadcast).
        // GOTV proxies use "SourceTV Demo" on Valve matchmaking and "GOTV<1>"
        // on legacy/community-server recordings — both are GOTV-class.
        if (!string.IsNullOrEmpty(clientName))
        {
            if (clientName.Contains("HLTV", StringComparison.OrdinalIgnoreCase))
            {
                return DemoProfile.HltvPro(buildNumber, gameDirectory);
            }

            if (clientName.Contains("GOTV", StringComparison.OrdinalIgnoreCase)
                || clientName.Contains("SourceTV", StringComparison.OrdinalIgnoreCase))
            {
                return DemoProfile.GotvMatchmaking(buildNumber, gameDirectory);
            }
        }

        // ── Server-name fallback ──
        // Valve matchmaking servers identify themselves as
        // "Valve Counter-Strike 2 ... Server"; treat as GOTV-class even when
        // the client_name field was unset or non-distinctive.
        if (!string.IsNullOrEmpty(serverName)
            && serverName.Contains("Valve", StringComparison.OrdinalIgnoreCase)
            && serverName.Contains("Counter-Strike", StringComparison.OrdinalIgnoreCase))
        {
            return DemoProfile.GotvMatchmaking(buildNumber, gameDirectory);
        }

        // FACEIT/ESEA/POV demos and unknown sources fall through here. We
        // default to GOTV-style features because matchmaking is the most
        // common recording mode and the broadest event set; rules that
        // require HLTV-only events will be skipped via `requires:` checks
        // in the analysis layer.
        return new DemoProfile(
            DemoSourceKind.Unknown,
            buildNumber,
            gameDirectory,
            DemoFeatureSet.HasPlayerBlind
            | DemoFeatureSet.HasRoundOfficiallyEnded
            | DemoFeatureSet.HasWeaponReload
            | DemoFeatureSet.HasWeaponZoom);
    }
}
