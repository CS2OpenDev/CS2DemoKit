namespace CS2DemoKit.Analysis.GoldenStats;

/// <summary>
///     Canonical stat keys used inside <see cref="GoldenStatsDocument.Players" /> stat
///     maps. Every provider's converter maps its own naming convention onto
///     these constants; comparison tests and reporting code reference them
///     too. Single source of truth: rename a stat here and every callsite
///     that uses the constant updates with it.
///     <para>
///         Naming is snake_case to match the JSON-on-disk format.
///         <see cref="GoldenStatsSerializer" /> emits property names as-is —
///         no naming-policy transformation runs on these keys.
///     </para>
/// </summary>
public static class CanonicalStatNames
{
    // ── Floats / ratios ───────────────────────────────────────────────────────
    /// <summary>Canonical key for the <c>adr</c> stat.</summary>
    public const string Adr = "adr";

    /// <summary>Canonical key for the <c>assists</c> stat.</summary>
    public const string Assists = "assists";

    /// <summary>Canonical key for the <c>ct_rounds_won</c> stat.</summary>
    public const string CtRoundsWon = "ct_rounds_won";

    /// <summary>Canonical key for the <c>deaths</c> stat.</summary>
    public const string Deaths = "deaths";

    /// <summary>Canonical key for the <c>enemy_damage</c> stat.</summary>
    public const string EnemyDamage = "enemy_damage";

    /// <summary>Canonical key for the <c>hltv_rating</c> stat.</summary>
    public const string HltvRating = "hltv_rating";

    /// <summary>Canonical key for the <c>hs_pct</c> stat.</summary>
    public const string HsPct = "hs_pct";

    // ── Percentages (stored 0–100, not 0–1) ───────────────────────────────────
    /// <summary>Canonical key for the <c>kast_pct</c> stat.</summary>
    public const string KastPct = "kast_pct";

    /// <summary>Canonical key for the <c>kd</c> stat.</summary>
    public const string Kd = "kd";

    // ── Counts ────────────────────────────────────────────────────────────────
    /// <summary>Canonical key for the <c>kills</c> stat.</summary>
    public const string Kills = "kills";

    /// <summary>Canonical key for the <c>multi_2k</c> stat.</summary>
    public const string Multi2K = "multi_2k";

    /// <summary>Canonical key for the <c>multi_3k</c> stat.</summary>
    public const string Multi3K = "multi_3k";

    /// <summary>Canonical key for the <c>multi_4k</c> stat.</summary>
    public const string Multi4K = "multi_4k";

    /// <summary>Canonical key for the <c>multi_5k</c> stat.</summary>
    public const string Multi5K = "multi_5k";

    /// <summary>Canonical key for the <c>rounds_survived</c> stat.</summary>
    public const string RoundsSurvived = "rounds_survived";

    /// <summary>Canonical key for the <c>shots_fired</c> stat.</summary>
    public const string ShotsFired = "shots_fired";

    /// <summary>Canonical key for the <c>shots_hit_foe</c> stat.</summary>
    public const string ShotsHitFoe = "shots_hit_foe";

    /// <summary>Canonical key for the <c>t_rounds_won</c> stat.</summary>
    public const string RoundsWon = "t_rounds_won";

    /// <summary>Canonical key for the <c>trade_kills</c> stat.</summary>
    public const string TradeKills = "trade_kills";
}
