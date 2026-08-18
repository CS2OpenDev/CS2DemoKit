namespace CS2DemoKit.Parser;

/// <summary>
///     Lightweight identification of a demo's recording source and capabilities.
///     Populated automatically by <see cref="DemoSourceClassifier" /> from the
///     <c>CDemoFileHeader</c>; can be supplied explicitly to
///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile)" /> via the
///     <c>profileOverride</c> argument when callers know better than the
///     auto-classifier (testing, mislabelled headers, dev tooling).
/// </summary>
/// <remarks>
///     This is the *parser-side* profile — a coarse identification record useful
///     to UI / output tooling. The analysis engine consumes a richer
///     <c>DemoSourceProfile</c> derived from this record at evaluator-build
///     time; that piece lives in the analysis library.
/// </remarks>
public sealed record DemoProfile(
    DemoSourceKind SourceKind,
    int BuildNumber,
    string GameDirectory,
    DemoFeatureSet Features)
{
    private const DemoFeatureSet GotvFeatures =
        DemoFeatureSet.HasPlayerBlind
        | DemoFeatureSet.HasRoundOfficiallyEnded
        | DemoFeatureSet.HasWeaponReload
        | DemoFeatureSet.HasWeaponZoom;

    private const DemoFeatureSet HltvFeatures =
        DemoFeatureSet.HasGrenadeThrown
        | DemoFeatureSet.HasHltvCameraEvents
        | DemoFeatureSet.HasEntityKilled
        | DemoFeatureSet.HasPlayerSound
        | DemoFeatureSet.HasCsPreRestart;

    /// <summary>
    ///     An empty profile used when no header information is available.
    /// </summary>
    public static DemoProfile Unknown { get; } =
        new(DemoSourceKind.Unknown, 0, string.Empty, DemoFeatureSet.None);

    /// <summary>
    ///     Standard CS2 GOTV matchmaking profile.
    /// </summary>
    public static DemoProfile GotvMatchmaking(int buildNumber = 0, string gameDirectory = "csgo") =>
        new(DemoSourceKind.GotvMatchmaking, buildNumber, gameDirectory, GotvFeatures);

    /// <summary>
    ///     HLTV / pro broadcast profile.
    /// </summary>
    public static DemoProfile HltvPro(int buildNumber = 0, string gameDirectory = "csgo") =>
        new(DemoSourceKind.HltvPro, buildNumber, gameDirectory, HltvFeatures);
}
