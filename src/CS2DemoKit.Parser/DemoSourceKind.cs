namespace CS2DemoKit.Parser;

/// <summary>
///     The recording source / use-case of a CS2 demo.
///     Identifies which demo-distribution pipeline produced the file, which in
///     turn dictates which game events are emitted (e.g. HLTV demos lack
///     <c>player_blind</c> and <c>round_officially_ended</c>).
/// </summary>
public enum DemoSourceKind
{
    /// <summary>Source could not be identified from the demo header.</summary>
    Unknown,

    /// <summary>Standard Valve matchmaking GOTV recording.</summary>
    GotvMatchmaking,

    /// <summary>Pro/HLTV broadcast recording.</summary>
    HltvPro,

    /// <summary>First-person POV recording.</summary>
    Pov,

    /// <summary>FACEIT match recording.</summary>
    Faceit,

    /// <summary>Custom or third-party recording profile.</summary>
    Custom
}
