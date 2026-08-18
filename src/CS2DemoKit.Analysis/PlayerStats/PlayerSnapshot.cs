namespace CS2DemoKit.Analysis.PlayerStats;

/// <summary>
///     A single player's state at a point in time, derived from the live entity tracker.
///     This is the shape the UI consumes — the
///     <c>PlayerSnapshotBuilder.Build</c> entry point produces a list of these
///     ordered CT-first then T-side, with names resolved via the supplied lookups.
/// </summary>
public sealed record PlayerSnapshot(
    int UserId,
    string Name,
    int Team,
    bool IsAlive,
    int Health,
    int Armor,
    bool HasHelmet,
    bool HasDefuser,
    int Money,
    string ActiveWeapon,
    string UtilSummary,
    int Kills,
    int Deaths,
    int Assists);
