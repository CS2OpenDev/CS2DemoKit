#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.GoldenStats;

/// <summary>
///     Per-tick per-slot per-field snapshot of our parser's entity state, used
///     as the load-bearing fixture for <c>EntityFieldSnapshotTests</c>. The
///     snapshot is canonical truth for our parser's output at a pinned demo
///     commit; tests re-run the parser at the same ticks and assert the
///     fields haven't drifted.
///     <para>
///         <b>Workflow:</b>
///         <list type="number">
///             <item>
///                 EntityFieldDiff tool runs against a reference demo, comparing
///                 our parser to demofile-net. Mismatches are surfaced in the
///                 tool's console output for the developer to investigate.
///             </item>
///             <item>
///                 When the developer is satisfied with our parser's output —
///                 either it agrees with demofile-net or any divergences are
///                 documented expected differences — the tool writes this
///                 snapshot via <see cref="Capture" />.
///             </item>
///             <item>
///                 The committed snapshot becomes the test fixture. Future
///                 parser changes that drift from these values fail
///                 <c>EntityFieldSnapshotTests</c> at test time. demofile-net
///                 is no longer in the loop unless someone deliberately
///                 re-runs the tool to refresh the fixture.
///             </item>
///         </list>
///     </para>
///     <para>
///         An earlier design proposed running demofile-net at every test
///         invocation. This shape keeps the standing constraint — demofile-net
///         is a comparison oracle only, never a project dependency — by moving
///         the live oracle out of the test path.
///     </para>
/// </summary>
public sealed record EntityFieldsSnapshot(
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
    [property: JsonPropertyName("ticks")] Dictionary<string, List<EntityFieldRow>> Ticks)
{
    /// <summary>JSON schema version emitted by the current build; bump when on-disk shape changes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     Captures the snapshot for a parsed demo at the given ticks.
    ///     <paramref name="ticks" /> are demo ServerTicks; the method does one
    ///     <see cref="EntityTracker.ReplayTo" /> per tick (no cross-tick
    ///     replay sharing). Slot ordering within each tick is ascending.
    /// </summary>
    /// <remarks>
    ///     The field selection mirrors the EntityFieldDiff tool — the same
    ///     six classes × small set of fields we already cross-check against
    ///     demofile-net. Keeping the field set narrow makes the fixture
    ///     human-readable on diff.
    /// </remarks>
    public static EntityFieldsSnapshot Capture(
        ParsedDemo demo,
        string demoFileName,
        IReadOnlyList<int> ticks,
        string? demoSha256 = null,
        string? providerVersion = null)
    {
        Dictionary<string, List<EntityFieldRow>> ticksOut = new(StringComparer.Ordinal);
        foreach (int tick in ticks)
        {
            EntityTracker tracker = new();
            tracker.ReplayTo(tick, demo.Frames);

            List<EntityFieldRow> rows = new();
            foreach ((int slot, EntityState ent) in tracker.CurrentEntities.AllIndexed())
            {
                if (!ent.IsInPvs)
                {
                    continue;
                }

                EntityFieldRow? row = CaptureRow(slot, ent);
                if (row is not null)
                {
                    rows.Add(row);
                }
            }

            rows.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            ticksOut[tick.ToString(CultureInfo.InvariantCulture)] = rows;
        }

        return new EntityFieldsSnapshot(
            CurrentSchemaVersion,
            demoFileName,
            demoSha256,
            "ours",
            providerVersion,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ticksOut);
    }

    private static void Add(Dictionary<string, object?> fields, EntityState ent,
        string rawPath, string outName)
    {
        object? v = ent[rawPath];
        if (v is null)
        {
            return;
        }

        // Normalize to JSON-friendly primitives. UInt64 handles fit in a JSON
        // number natively; ulong serialises lossy if >= 2^53, but we use
        // ToString in that range to preserve full precision.
        fields[outName] = NormalizeForJson(v);
    }

    /// <summary>
    ///     Extracts the small set of fields we care about from a single entity.
    ///     Returns <c>null</c> when the entity's class isn't in our tracked set
    ///     (most non-player entities, projectiles, props, etc.). Keeps the
    ///     fixture compact and focused on the gameplay-relevant subset.
    /// </summary>
    private static EntityFieldRow? CaptureRow(int slot, EntityState ent)
    {
        Dictionary<string, object?> fields = new(StringComparer.Ordinal);
        switch (ent.ClassName)
        {
            case "CCSPlayerController":
                Add(fields, ent, "m_iszPlayerName", "PlayerName");
                Add(fields, ent, "m_steamID", "SteamID");
                Add(fields, ent, "m_iConnected", "Connected");
                Add(fields, ent, "m_iTeamNum", "TeamNum");
                Add(fields, ent, "m_iPendingTeamNum", "PendingTeamNum");
                Add(fields, ent, "m_pInGameMoneyServices.m_iAccount", "Account");
                break;
            case "CCSPlayerPawn":
                Add(fields, ent, "m_iHealth", "Health");
                Add(fields, ent, "m_iMaxHealth", "MaxHealth");
                Add(fields, ent, "m_lifeState", "LifeState");
                Add(fields, ent, "m_iTeamNum", "TeamNum");
                Add(fields, ent, "m_ArmorValue", "ArmorValue");
                break;
            case "CCSGameRulesProxy":
                Add(fields, ent, "m_pGameRules.m_totalRoundsPlayed", "TotalRoundsPlayed");
                Add(fields, ent, "m_pGameRules.m_gamePhase", "GamePhase");
                Add(fields, ent, "m_pGameRules.m_bWarmupPeriod", "WarmupPeriod");
                break;
            default:
                return null;
        }

        if (fields.Count == 0)
        {
            return null;
        }

        return new EntityFieldRow(slot, ent.ClassName, fields);
    }

    /// <summary>
    ///     Coerces a wire-decoded value into a System.Text.Json-friendly type.
    ///     Big ulong handles (m_steamID is 17 digits) become strings to avoid
    ///     JSON double-precision loss. Everything else round-trips via the
    ///     default System.Text.Json primitive serialisation.
    /// </summary>
    private static object? NormalizeForJson(object? v) => v switch
    {
        null => null,
        bool => v,
        string => v,
        sbyte s => (long)s,
        byte b => (long)b,
        short s => (long)s,
        ushort u => (long)u,
        int i => (long)i,
        uint u => (long)u,
        long => v,
        ulong u when u <= long.MaxValue => (long)u,
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        float f => (double)f,
        double => v,
        decimal m => (double)m,
        _ => v.ToString()
    };
}

/// <summary>
///     One entity's captured fields at a single tick. Slot is the entity index
///     in <c>tracker.CurrentEntities</c>; class is the schema class name.
/// </summary>
public sealed record EntityFieldRow(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("class")] string ClassName,
    [property: JsonPropertyName("fields")] Dictionary<string, object?> Fields);

/// <summary>
///     JSON read/write for <see cref="EntityFieldsSnapshot" />. Same options as
///     <see cref="GoldenStatsSerializer" /> (snake_case property naming,
///     skip-null on write, indented output).
/// </summary>
public static class EntityFieldsSnapshotSerializer
{
    /// <summary>Deserializes an <see cref="EntityFieldsSnapshot" /> from a JSON string.</summary>
    public static EntityFieldsSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<EntityFieldsSnapshot>(json, GoldenStatsSerializer.Options)
        ?? throw new InvalidOperationException("EntityFieldsSnapshot deserialized to null.");

    /// <summary>Reads and deserializes a snapshot from a file on disk.</summary>
    public static EntityFieldsSnapshot ReadFromFile(string path) =>
        Deserialize(File.ReadAllText(path));

    /// <summary>Serializes an <see cref="EntityFieldsSnapshot" /> to a JSON string.</summary>
    public static string Serialize(EntityFieldsSnapshot snap) =>
        JsonSerializer.Serialize(snap, GoldenStatsSerializer.Options);

    /// <summary>Serializes the snapshot and writes the JSON to a file on disk.</summary>
    public static void WriteToFile(EntityFieldsSnapshot snap, string path) =>
        File.WriteAllText(path, Serialize(snap));
}
