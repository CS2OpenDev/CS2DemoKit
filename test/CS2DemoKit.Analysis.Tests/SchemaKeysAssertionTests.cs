#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Asserts that a hand-selected set of critical entity field paths exist
///     on the right entity classes at a known-good demo midpoint. Catches
///     schema drift that would otherwise manifest as silent <c>null</c> reads
///     at runtime — the failure mode that hid the
///     <c>m_hActiveWeapon</c> bug for the entire lifetime of the snapshot path.
///     <para>
///         This is the complement to the submodule SHA pin: the SHA
///         pin catches drift at the source, this catches drift that flows
///         through SDK regeneration (the SHA gets bumped, codegen runs, but
///         a critical field renames). Either failure points at the same root
///         cause; together they catch schema breakage from two sides.
///     </para>
///     <para>
///         The critical field list is intentionally small — the fields whose
///         absence would break specific gameplay-relevant features the parser
///         and snapshot builder consume. Adding a field to this list pins
///         that field as load-bearing; removing one says "we accept silent
///         drift here."
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class SchemaKeysAssertionTests
{
    /// <summary>
    ///     The pinned critical-field list. Each entry maps to specific code
    ///     paths in the parser/analyzer that would silently break if the
    ///     field disappeared.
    /// </summary>
    private static readonly CriticalField[] _fields =
    [
        // Player controller — money tracking and pawn linkage.
        //
        // Names are NOT pinned at the entity-field layer. Despite SchemaNames
        // exposing both m_iszPlayerName and m_sSanitizedPlayerName on the
        // controller, neither appears in entity.Fields after full replay on
        // bench demos — names come from parsed.Players (string-table) and
        // PlayerConnectEvent. PlayerSnapshotBuilder reads m_sSanitizedPlayerName
        // as the "primary" path but production works only through the
        // nameBySlot / nameByUserId fallback. This is a separate code-smell
        // finding tracked outside the audit.
        new("CCSPlayerController", SchemaNames.CBasePlayerController.Pawn,
            "Controller → pawn handle. Required for every pawn-derived field "
            + "(health, weapons, armor) in the player snapshot."),
        new("CCSPlayerController",
            SchemaNames.CCSPlayerController.InGameMoneyServices + "."
                                                                + SchemaNames.CCSPlayerControllerInGameMoneyServices.Account,
            "Money tracking — dotted sub-entity path. Missing this means the "
            + "money column reports 0 for everyone."),

        // Player pawn — health, team, alive state.
        new("CCSPlayerPawn", SchemaNames.CBaseEntity.Health,
            "HP. Read by PawnHealthProvider, HurtTeamEnrichmentEdge, "
            + "PlayerSnapshotBuilder."),
        new("CCSPlayerPawn", SchemaNames.CBaseEntity.TeamNum,
            "Team identity. Drives team-classification across the analyzer."),
        new("CCSPlayerPawn", SchemaNames.CBaseEntity.LifeState,
            "Alive/dead transition signal."),
        new("CCSPlayerPawn", "m_hController",
            "Reverse pawn→controller link. PawnLookup.ResolvePawn relies on "
            + "this for slot resolution (forward m_hPawn is unreliable)."),

        // Pawn sub-entities — flattened under dotted parent paths.
        new("CCSPlayerPawn",
            SchemaNames.CBasePlayerPawn.WeaponServices + "."
                                                       + SchemaNames.CPlayerWeaponServices.ActiveWeapon,
            "Active weapon handle. ActiveWeaponProvider reads this via the "
            + "dotted path; the un-dotted leaf does NOT exist on the pawn."),
        new("CCSPlayerPawn",
            SchemaNames.CBasePlayerPawn.WeaponServices + "."
                                                       + SchemaNames.CPlayerWeaponServices.MyWeapons + "[0]",
            "Weapon inventory slot 0 — bracket-notation array entry. "
            + "Utility counting iterates these; .000-style dot indexing "
            + "would never match."),

        // Game rules.
        new("CCSGameRulesProxy",
            SchemaNames.CCSGameRulesProxy.GameRules + "."
                                                    + SchemaNames.CCSGameRules.FreezePeriod,
            "FreezeTime gate for the analyzer's gameplay_phase state machine."),
        new("CCSGameRulesProxy",
            SchemaNames.CCSGameRulesProxy.GameRules + ".m_totalRoundsPlayed",
            "Round counter. Drives round-scoped resets.")
    ];

    /// <summary>Critical schema fields_exist on reference demo.</summary>
    [Test]
    public async Task CriticalSchemaFields_ExistOnReferenceDemo()
    {
        string demoPath = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(demoPath);

        // Replay to end-of-demo. By the final tick, every networked field has
        // received at least one value (string-table snapshot at signon plus all
        // subsequent live updates). Midpoint is fine for transient gameplay
        // state but misses fields populated only at connect-time
        // (e.g. m_iszPlayerName, m_steamID).
        EntityTracker tracker = new();
        tracker.Replay(parsed.Frames);

        // For each critical field, find ONE entity of the right class and
        // assert the field key exists. Accumulate failures so we report them
        // all together in one pass instead of bailing at the first.
        List<string> missing = new();
        HashSet<string> checkedClasses = new();

        foreach (CriticalField cf in _fields)
        {
            // Find any live entity of this class.
            EntityState? sample = tracker.CurrentEntities.All()
                .FirstOrDefault(e => string.Equals(e.ClassName, cf.EntityClass, StringComparison.Ordinal));

            if (sample is null)
            {
                missing.Add($"  CLASS-MISSING  {cf.EntityClass}  (looking for field '{cf.FieldPath}')");
                continue;
            }

            checkedClasses.Add(cf.EntityClass);

            if (!sample.Fields.ContainsKey(cf.FieldPath))
            {
                // Provide a hint: which keys with a similar prefix DO exist?
                string prefix = cf.FieldPath.Split('.', '[')[0];
                string[] siblings = sample.Fields.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .Take(5)
                    .ToArray();
                string siblingHint = siblings.Length > 0
                    ? $"  nearby keys present: [{string.Join(", ", siblings)}]"
                    : "  no keys with that prefix present at all";
                missing.Add($"  FIELD-MISSING  {cf.EntityClass}.{cf.FieldPath}  ({cf.Why}){siblingHint}");
            }
        }

        Console.WriteLine($"Checked {_fields.Length} critical fields across {checkedClasses.Count} entity classes.");
        if (missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Schema drift detected — these critical fields are missing or moved:");
            foreach (string m in missing)
            {
                Console.WriteLine(m);
            }

            Console.WriteLine();
            Console.WriteLine("If this is intentional (schema regen reflects an upstream change):");
            Console.WriteLine("  1. Confirm the new flattened path via the EntityFieldDiff tool or a debug dump");
            Console.WriteLine("  2. Update the _fields[] list in SchemaKeysAssertionTests.cs");
            Console.WriteLine("  3. Update any code (PlayerSnapshotBuilder, providers, etc.) that read the old path");
        }

        await Assert.That(missing.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     One critical field path on a specific entity class.
    /// </summary>
    /// <param name="EntityClass">Class to search for in the live entity table.</param>
    /// <param name="FieldPath">
    ///     Key the field MUST appear under in <c>entity.Fields</c>
    ///     after schema flattening. Sub-entity fields use dotted notation;
    ///     array fields use bracket indexing. See
    ///     <c>project_cs2_wire_encoding</c> memory note for the rules.
    /// </param>
    /// <param name="Why">
    ///     Plain-English reason this field is load-bearing —
    ///     shown in the failure message so a future maintainer can decide
    ///     whether the drift is intentional or a real bug.
    /// </param>
    public sealed record CriticalField(string EntityClass, string FieldPath, string Why);
}
