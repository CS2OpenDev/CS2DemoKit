#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.PlayerStats;

/// <summary>
///     Pure-function builder that derives the per-player <see cref="PlayerSnapshot" />
///     list from a live <see cref="EntityTracker" />. Extracted from the UI ViewModel
///     so the field-lookup logic — historically a source of silent bugs because the
///     storage format diverges from the SchemaNames constants — can be tested
///     directly. The ViewModel is now a thin caller that hands its name lookups in
///     and assigns the result.
///     <para>
///         <b>Storage-format facts the builder encodes</b> (verified empirically):
///         <list type="bullet">
///             <item>
///                 Entity handles arrive as <c>UInt64</c>; cast via
///                 <see cref="ReadHandleIndex" />.
///             </item>
///             <item>
///                 Networked bools arrive as <c>Int32</c> 0/1; cast via
///                 <see cref="ReadBool" />.
///             </item>
///             <item>
///                 Sub-entity fields are flattened under dotted parent paths
///                 (e.g. <c>m_pWeaponServices.m_hActiveWeapon</c>); never use the
///                 un-dotted leaf name alone.
///             </item>
///             <item>
///                 Array entries use bracket indexing
///                 (<c>m_pWeaponServices.m_hMyWeapons[0]</c>), not dot+zero-pad.
///             </item>
///         </list>
///         See <c>project_cs2_wire_encoding</c> memory note for details.
///     </para>
/// </summary>
public static class PlayerSnapshotBuilder
{
    // ── Display helpers ───────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> _weaponShortNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CWeaponAK47"] = "AK-47",
            ["CWeaponM4A1"] = "M4A1-S",
            ["CWeaponM4A4"] = "M4A4",
            ["CWeaponAWP"] = "AWP",
            ["CWeaponUSPSilencer"] = "USP-S",
            ["CWeaponGlock"] = "Glock",
            ["CWeaponP250"] = "P250",
            ["CWeaponDeagle"] = "Deagle",
            ["CWeaponSSG08"] = "Scout",
            ["CWeaponSG556"] = "SG-553",
            ["CWeaponFamas"] = "FAMAS",
            ["CWeaponGalilAR"] = "Galil AR",
            ["CWeaponMP9"] = "MP9",
            ["CWeaponMac10"] = "Mac-10",
            ["CWeaponUMP45"] = "UMP-45",
            ["CWeaponP90"] = "P90",
            ["CWeaponBizon"] = "PP-Bizon",
            ["CWeaponMP5SD"] = "MP5-SD",
            ["CWeaponNegev"] = "Negev",
            ["CWeaponM249"] = "M249",
            ["CWeaponXM1014"] = "XM1014",
            ["CWeaponSawedoff"] = "Sawed-Off",
            ["CWeaponNova"] = "Nova",
            ["CWeaponMag7"] = "MAG-7",
            ["CWeaponG3SG1"] = "G3SG1",
            ["CWeaponScar20"] = "SCAR-20",
            ["CWeaponTec9"] = "Tec-9",
            ["CWeaponCZ75a"] = "CZ75",
            ["CWeaponFiveSeven"] = "Five-SeveN",
            ["CWeaponRevolver"] = "R8",
            ["CWeaponMP7"] = "MP7",
            ["CC4"] = "C4",
            ["CFlashbangGrenade"] = "Flash",
            ["CSmokeGrenade"] = "Smoke",
            ["CHEGrenade"] = "HE",
            ["CMolotovGrenade"] = "Molotov",
            ["CIncendiaryGrenade"] = "Incendiary",
            ["CDecoyGrenade"] = "Decoy"
        };

    /// <summary>
    ///     Convenience overload: takes a <see cref="ParsedDemo" /> and constructs the
    ///     name lookups internally from <c>Players</c> and <c>PlayerConnectEvent</c>s.
    ///     Use this when you don't already have the lookups cached (e.g. tests and
    ///     one-off snapshot scripts). The ViewModel caches the lookups once at
    ///     load time and calls the three-arg overload on every seek.
    /// </summary>
    public static IReadOnlyList<PlayerSnapshot> Build(EntityTracker tracker, ParsedDemo demo)
    {
        (IReadOnlyDictionary<int, string> nameBySlot,
            IReadOnlyDictionary<int, string> nameByUserId) = BuildNameLookups(demo);
        return Build(tracker, nameBySlot, nameByUserId);
    }

    /// <summary>
    ///     Builds the per-player snapshot list from the current entity state. Output
    ///     is ordered CT-side first then T-side; spectators and unassigned slots are
    ///     filtered out. Players with no resolvable display name (still mid-connect
    ///     or already disconnected) are skipped.
    /// </summary>
    /// <param name="tracker">Live entity tracker positioned at the desired tick.</param>
    /// <param name="nameBySlot">
    ///     Map from player slot to display name, populated from
    ///     connect events and string-table updates by the caller.
    /// </param>
    /// <param name="nameByUserId">Map from user-id to display name; secondary fallback.</param>
    public static IReadOnlyList<PlayerSnapshot> Build(
        EntityTracker tracker,
        IReadOnlyDictionary<int, string> nameBySlot,
        IReadOnlyDictionary<int, string> nameByUserId)
    {
        List<PlayerSnapshot> ctList = new();
        List<PlayerSnapshot> tList = new();

        foreach ((int ctrlIdx, EntityState ctrl) in tracker.CurrentEntities.AllIndexed())
        {
            if (!ctrl.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Wrap the controller (and pawn below) in typed wrappers so
            // simple-field reads route through the lane-indexed wrapper getters instead
            // of `Fields[string]` dict lookups. The wrapper for CCSPlayerController is
            // CSPlayerController; for CCSPlayerPawn it's CSPlayerPawn. Concrete weapon
            // classes (CWeaponAK47, etc.) are NOT in the curated 14, so the weapon
            // resolution + m_hMyWeapons array iteration stay on the legacy dict path
            // (arrays are one object-lane slot; per-element bracket reads route to
            // _fallback).
            CSPlayerController ctrlWrapper = SdkEntityWorlds.Wrap<CSPlayerController>(tracker, ctrl)!;

            // Resolve display name: entity field → connect-event slot → user-id fallback.
            // Slot is (controller index - 1) in CS2. The wrapper exposes SanitizedPlayerName
            // as an `object?` getter (object-lane field with Transform.None); cast to string.
            string? name = ctrlWrapper.SanitizedPlayerName as string;
            if (string.IsNullOrEmpty(name))
            {
                // Defensive fallback to a path-keyed read in case the field landed on the
                // fallback dict (e.g. an early-tick controller that only received the
                // bare-leaf form). Cheap; preserves the prior reader's tolerance.
                name = ReadString(ctrl.Fields, SchemaNames.CCSPlayerController.SSanitizedPlayerName);
            }

            int slot = ctrlIdx - 1;
            if (string.IsNullOrEmpty(name))
            {
                nameBySlot.TryGetValue(slot, out name);
            }

            if (string.IsNullOrEmpty(name))
            {
                nameByUserId.TryGetValue(slot, out name);
            }

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // The SDK wrapper exposes PawnHandle as the raw packed uint (no mask, no
            // decode). Mask + sentinel-check matches the legacy ReadHandleIndex behaviour.
            int pawnIdx = MaskHandle(ctrlWrapper.PawnHandle);

            // Money: m_pInGameMoneyServices.m_iAccount → wrapper.Account. The legacy bare-
            // leaf fallback was a defensive holdover that empirically never resolved; drop it.
            int money = ctrlWrapper.Account;

            EntityState? pawn = tracker.CurrentEntities[pawnIdx];
            CSPlayerPawn? pawnWrapper = pawn is not null ? SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)! : null;

            int health = pawnWrapper?.Health ?? 0;

            // LIFE_ALIVE = 0; any non-zero state is dead/respawning. Fall back to
            // "health > 0" only when the field is absent (pre-spawn entities that have
            // never received a m_lifeState update). Use the seen-aware
            // CSPlayerPawn.LifeState getter (int?, TryGetInt-backed) so the null/absent
            // distinction replaces the prior Fields.ContainsKey dict touch — equivalent
            // because m_lifeState is lens-mapped to the int lane, so the seen-bit == key-present.
            bool alive = pawnWrapper is not null && (pawnWrapper.LifeState is { } ls ? ls == 0 : health > 0);

            // Armor lives on the pawn ROOT (m_ArmorValue) — unlike helmet/defuser below, the
            // wire never spells it under m_pItemServices, so the old dotted fallback here
            // probed a path that cannot exist and never fired (cutover-inventory F1).
            int armor = pawnWrapper?.ArmorValue ?? 0;

            // HasHelmet / HasDefuser: wrapper exposes the bare-leaf form (BoolFromInt);
            // the dotted sub-service form was a defensive fallback for older schema
            // dumps and stays on the dict path.
            bool helmet = pawnWrapper is not null
                          && (pawnWrapper.HasHelmet
                              || ReadBool(pawn!.Fields,
                                  SchemaNames.CBasePlayerPawn.ItemServices + "." + SchemaNames.CCSPlayerItemServices.HasHelmet));
            bool defuser = pawnWrapper is not null
                           && (pawnWrapper.HasDefuser
                               || ReadBool(pawn!.Fields,
                                   SchemaNames.CBasePlayerPawn.ItemServices + "." + SchemaNames.CCSPlayerItemServices.HasDefuser));
            int team = pawnWrapper?.TeamNum ?? 0;

            string activeWeapon = "";
            if (pawnWrapper is not null)
            {
                // First hop via wrapper (lane-indexed); second hop on dict path because
                // concrete weapon classes aren't in the curated wrapper set (the factory
                // would return null, but tracker.CurrentEntities[slot] returns the raw
                // EntityState regardless).
                int awIdx = MaskHandle(pawnWrapper.ActiveWeaponHandle);
                if (awIdx > 0)
                {
                    EntityState? wpn = tracker.CurrentEntities[awIdx];
                    if (wpn is not null)
                    {
                        activeWeapon = WeaponShortName(wpn.ClassName);
                    }
                }
            }

            int flash = 0, smoke = 0, he = 0, mol = 0, decoy = 0;
            if (pawn is not null)
            {
                // R6 (V1 architecture): m_hMyWeapons is a bracket-indexed array
                // (m_pWeaponServices.m_hMyWeapons[i]). Per-element bracket reads route to
                // _fallback (arrays get one object-lane container slot, not per-element slots).
                // This loop intentionally stays on the dict path. Migrating to a typed
                // per-element array accessor is a V2 problem.
                string myWeaponsPrefix = SchemaNames.CBasePlayerPawn.WeaponServices + "."
                                                                                    + SchemaNames.CPlayerWeaponServices.MyWeapons;
                for (int i = 0; i < 64; i++)
                {
                    int wIdx = ReadHandleIndex(pawn.Fields, $"{myWeaponsPrefix}[{i}]");
                    if (wIdx == 0)
                    {
                        continue;
                    }

                    EntityState? w = tracker.CurrentEntities[wIdx];
                    if (w is null)
                    {
                        continue;
                    }

                    string cls = w.ClassName;
                    if (cls.Contains("Flashbang", StringComparison.OrdinalIgnoreCase))
                    {
                        flash++;
                    }
                    else if (cls.Contains("Smoke", StringComparison.OrdinalIgnoreCase))
                    {
                        smoke++;
                    }
                    else if (cls.Contains("HEGrenade", StringComparison.OrdinalIgnoreCase)
                             || cls.Contains("HeGrenade", StringComparison.OrdinalIgnoreCase))
                    {
                        he++;
                    }
                    else if (cls.Contains("Molotov", StringComparison.OrdinalIgnoreCase)
                             || cls.Contains("Incendiary", StringComparison.OrdinalIgnoreCase))
                    {
                        mol++;
                    }
                    else if (cls.Contains("Decoy", StringComparison.OrdinalIgnoreCase))
                    {
                        decoy++;
                    }
                }
            }

            PlayerSnapshot snapshot = new(
                ctrlIdx,
                name,
                team,
                alive,
                health,
                armor,
                helmet,
                defuser,
                money,
                activeWeapon,
                BuildUtilString(flash, smoke, he, mol, decoy),
                0,
                0,
                0);

            if (team == 3)
            {
                ctList.Add(snapshot);
            }
            else if (team == 2)
            {
                tList.Add(snapshot);
            }
            // Team 0 (unassigned) and 1 (spectator) are filtered out.
        }

        List<PlayerSnapshot> combined = new(ctList.Count + tList.Count);
        combined.AddRange(ctList);
        combined.AddRange(tList);
        return combined;
    }

    /// <summary>
    ///     Builds the two name-lookup dictionaries the snapshot builder consumes,
    ///     using the same logic the ViewModel runs at file-load time:
    ///     <list type="bullet">
    ///         <item><c>nameByUserId</c> seeded from <c>parsed.Players</c> (string-table).</item>
    ///         <item>Both maps overlaid from any <see cref="PlayerConnectEvent" /> in the demo.</item>
    ///     </list>
    ///     Extracted so tests can construct identical input to the ViewModel without
    ///     copy-pasting the loop.
    /// </summary>
    public static (IReadOnlyDictionary<int, string> NameBySlot,
        IReadOnlyDictionary<int, string> NameByUserId)
        BuildNameLookups(ParsedDemo demo)
    {
        Dictionary<int, string> nameByUserId = new();
        Dictionary<int, string> nameBySlot = new();

        foreach ((int slot, PlayerInfo p) in demo.Players)
        {
            if (p.Name.Length > 0)
            {
                nameByUserId[slot] = p.Name;
            }
        }

        foreach (GameEvent ge in demo.AllGameEvents)
        {
            if (ge.Payload is PlayerConnectEvent { Name.Length: > 0 } c)
            {
                nameByUserId[c.UserId] = c.Name;
                nameBySlot[c.UserId] = c.Name;
            }
        }

        return (nameBySlot, nameByUserId);
    }

    /// <summary>
    ///     Formats utility counts as a compact summary (e.g. <c>"F2 S1 HE1"</c>).
    ///     Returns an empty string when the player has no utility.
    /// </summary>
    public static string BuildUtilString(int flash, int smoke, int he, int mol, int decoy)
    {
        List<string> parts = new(5);
        if (flash > 0)
        {
            parts.Add($"F{flash}");
        }

        if (smoke > 0)
        {
            parts.Add($"S{smoke}");
        }

        if (he > 0)
        {
            parts.Add($"HE{he}");
        }

        if (mol > 0)
        {
            parts.Add($"M{mol}");
        }

        if (decoy > 0)
        {
            parts.Add($"D{decoy}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    ///     Reads a bool field. Networked bools arrive as <c>Int32</c> 0/1 on the wire,
    ///     so the naive <c>is bool</c> cast always fails — this method coerces from any
    ///     integral type as well.
    /// </summary>
    public static bool ReadBool(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? v))
        {
            return false;
        }

        return v switch
        {
            bool b => b,
            int i => i != 0,
            uint u => u != 0,
            long l => l != 0,
            ulong u => u != 0,
            short s => s != 0,
            ushort u => u != 0,
            byte b => b != 0,
            sbyte s => s != 0,
            _ => false
        };
    }

    /// <summary>
    ///     CS2 entity-handle index decode for a raw int handle. Returns 0 for the
    ///     zero handle and the <c>0xFFFFFFFF</c> sentinel; otherwise returns the
    ///     low-14-bit slot index. Used on <c>wrapper.&lt;Name&gt;Handle</c> getters to
    ///     keep the legacy "0 means no live handle" convention without a second dict
    ///     lookup (the uint overload below matches the SDK wrappers' raw getters).
    /// </summary>
    public static int MaskHandle(int rawHandle) =>
        MaskIndex(rawHandle == 0 || rawHandle == -1 ? 0 : rawHandle & 0x3FFF);

    /// <summary>
    ///     <see cref="MaskHandle(int)" /> for the SDK wrappers' raw <c>uint</c> handle
    ///     getters (same packed value, same 0 / 0xFFFFFFFF sentinels, same low-14-bit index).
    /// </summary>
    public static int MaskHandle(uint rawHandle) =>
        MaskIndex(rawHandle is 0u or 0xFFFF_FFFFu ? 0 : (int)(rawHandle & 0x3FFF));

    /// <summary>
    ///     Folds the all-ones index into the "no live handle" zero. A narrower handle field encodes
    ///     invalid within its own width rather than as <c>0xFFFFFFFF</c>: a dead pawn's
    ///     <c>m_hController</c> reads <c>0x00FFFFFF</c>, which masks to 0x3FFF. Nothing lives at that
    ///     index, so without this it leaves here as a live handle and becomes a garbage slot 16382.
    ///     <para>
    ///         Same fold as <see cref="PawnLookup.IndexOf" /> but to a different sentinel:
    ///         these are public getters whose documented convention is "0 means no live handle", so
    ///         they cannot switch to -1 without breaking callers.
    ///     </para>
    /// </summary>
    private static int MaskIndex(int index) => index == 0x3FFF ? 0 : index;

    /// <summary>
    ///     CS2 entity-handle decoder: lower 14 bits = entity index. Wire values arrive
    ///     as <c>UInt64</c> most often. Returns 0 for missing / zero / sentinel
    ///     (<c>0xFFFFFFFF</c>) / non-numeric — callers treat 0 as "no live handle".
    /// </summary>
    public static int ReadHandleIndex(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? v))
        {
            return 0;
        }

        uint handle = v switch
        {
            uint u => u,
            int i => unchecked((uint)i),
            long l => unchecked((uint)l),
            ulong u => unchecked((uint)u),
            short s => unchecked((uint)s),
            ushort u => u,
            byte b => b,
            sbyte s => unchecked((uint)s),
            _ => 0u
        };
        if (handle == 0 || handle == 0xFFFF_FFFF)
        {
            return 0;
        }

        return MaskIndex((int)(handle & 0x3FFF));
    }

    /// <summary>
    ///     Reads any numerically-typed field as <see cref="int" /> via wide coercion.
    ///     Returns 0 for missing or non-numeric values. Apply this even for fields
    ///     declared as <c>uint</c> in the schema — the wire type may be wider.
    /// </summary>
    public static int ReadInt(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? v))
        {
            return 0;
        }

        return v switch
        {
            int i => i,
            uint u => unchecked((int)u),
            long l => unchecked((int)l),
            ulong u => unchecked((int)u),
            short s => s,
            ushort u => u,
            byte b => b,
            sbyte s => s,
            _ => 0
        };
    }

    // ── Defensive readers ─────────────────────────────────────────────────────
    // Networked field decoders produce a variety of integer types — see
    // project_cs2_wire_encoding memory note. These readers coerce widely so the
    // builder doesn't need to know the exact wire-decoded type per field.

    /// <summary>
    ///     Reads a string field, returning <c>null</c> for missing/empty values rather
    ///     than the default empty-string. Used for the name lookup so the caller can
    ///     short-circuit when the entity field hasn't been populated yet.
    /// </summary>
    public static string? ReadString(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out object? v) && v is string { Length: > 0 } s ? s : null;

    /// <summary>
    ///     Maps a CS2 weapon entity class name to a display label. Falls back to
    ///     stripping the leading <c>CWeapon</c> / <c>C_Weapon</c> / <c>C_</c> / <c>C</c>
    ///     prefix when the class isn't in the lookup table.
    /// </summary>
    public static string WeaponShortName(string className)
    {
        if (_weaponShortNames.TryGetValue(className, out string? n))
        {
            return n;
        }

        if (className.StartsWith("CWeapon", StringComparison.Ordinal))
        {
            return className[7..];
        }

        if (className.StartsWith("C_Weapon", StringComparison.Ordinal))
        {
            return className[8..];
        }

        if (className.StartsWith("C_", StringComparison.Ordinal))
        {
            return className[2..];
        }

        if (className.StartsWith('C'))
        {
            return className[1..];
        }

        return className;
    }
}
