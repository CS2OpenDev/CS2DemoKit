#region

using System.Text.Json.Serialization;

#endregion

namespace CS2DemoKit.Analysis.GoldenStats;

/// <summary>
///     Canonical per-(demo, provider) stats document. Every stat provider — ours,
///     hand-verified expected, any external reference — gets converted into this
///     shape. Comparison tests then diff one provider's <see cref="GoldenStatsDocument" />
///     against another's, abstracted from provider-specific stat names and
///     numeric quirks (a provider may emit percent-stats as 0–1 ratios, our
///     <c>HLTV</c> column is a string, etc. — those live in the converters).
///     <para>
///         Stat values are always nullable double. Counts (kills, deaths,
///         multi-2k…5k) are stored as exact doubles; provider-specific scaling
///         and tolerance choices live in the comparison test parameters, not
///         in this record. A missing stat is represented as <c>null</c>, which
///         the comparison layer treats as "this provider does not report it"
///         rather than "this provider reports zero."
///     </para>
///     <para>
///         <b>JSON layout</b> uses snake_case for property names and is
///         human-readable on disk (indented). Producer code should use
///         <see cref="GoldenStatsSerializer" /> to read/write — direct
///         <c>JsonSerializer</c> calls bypass the snake-case naming policy.
///     </para>
/// </summary>
public sealed record GoldenStatsDocument(
    [property: JsonPropertyName("schema_version")]
    int SchemaVersion,
    [property: JsonPropertyName("demo")] string DemoFileName,
    [property: JsonPropertyName("demo_sha256")]
    string? DemoSha256,
    [property: JsonPropertyName("provider")]
    string Provider,
    [property: JsonPropertyName("provider_version")]
    string? ProviderVersion,
    [property: JsonPropertyName("generated_at")]
    string? GeneratedAt,
    [property: JsonPropertyName("match")] MatchMetadata Match,
    [property: JsonPropertyName("players")]
    Dictionary<string, PlayerStatsRecord> Players)
{
    /// <summary>JSON schema version emitted by the current build; bump when on-disk shape changes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
///     Match-level metadata. Optional fields are <c>null</c> when the provider
///     doesn't supply them — not every provider reports a tick_count.
/// </summary>
public sealed record MatchMetadata(
    [property: JsonPropertyName("map")] string? Map = null,
    [property: JsonPropertyName("tick_count")]
    int? TickCount = null,
    [property: JsonPropertyName("rounds")] int? Rounds = null,
    [property: JsonPropertyName("round_winners")]
    IReadOnlyList<int>? RoundWinners = null);

/// <summary>
///     Per-player stats record. Keyed by player display name (the
///     m_sSanitizedPlayerName field) in the parent <see cref="GoldenStatsDocument.Players" />
///     map. Display name is sometimes the only cross-provider identifier we
///     have — Steam IDs are not in every provider's output.
/// </summary>
public sealed record PlayerStatsRecord(
    [property: JsonPropertyName("team")] int Team,
    [property: JsonPropertyName("user_id")]
    int? UserId,
    [property: JsonPropertyName("steam_id")]
    ulong? SteamId,
    [property: JsonPropertyName("stats")] Dictionary<string, double?> Stats);
