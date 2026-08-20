#region

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Google.Protobuf;
using Snappier;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     Decodes string table data from all three sources that appear in a CS2 demo:
///     <list type="number">
///         <item>
///             <see cref="ProcessSnapshot" /> — <c>CDemoStringTables</c> in <c>DEM_StringTables</c>
///             frames and <c>DEM_FullPacket</c> checkpoints.  Direct protobuf encoding; no bitstream
///             decoding required.
///         </item>
///         <item>
///             <see cref="ProcessCreate" /> — <c>svc_CreateStringTable</c> net messages.  The
///             <c>string_data</c> field is a Source-engine bitstream (see §7 of DEMO_FILE_STRUCTURE.md).
///         </item>
///         <item>
///             <see cref="ProcessUpdate" /> — <c>svc_UpdateStringTable</c> net messages.  Same
///             bitstream format as CreateStringTable but carries only changed entries and uses a
///             table-id reference instead of a name.
///         </item>
///     </list>
///     <para>
///         Player info is extracted from the <c>userinfo</c> string table after each update.
///         The binary user-data per entry follows the Source engine <c>player_info_t</c> struct
///         layout (see inline comments on <see cref="TryParsePlayerInfo" />).
///     </para>
/// </summary>
internal sealed class StringTableProcessor
{
    // player_info_t binary layout (Source engine, little-endian):
    //
    //   offset   0  (8): version     — uint64, format version
    //   offset   8  (8): xuid        — uint64, SteamID64
    //   offset  16 (128): name       — char[128], null-terminated display name
    //   offset 144  (4): userId      — int32, in-game userid (matches game-event userid fields)
    //   offset 148 (33): guid        — char[33], "STEAM_X:X:XXXXXXX" null-terminated
    //   offset 181  (3): _pad0       — alignment padding
    //   offset 184  (4): friendsId   — uint32, Steam friends ID
    //   offset 188 (128): friendsName — char[128], null-terminated
    //   offset 316  (1): fakePlayer  — byte, non-zero = bot
    //   offset 317  (1): isHLTV      — byte, non-zero = GOTV proxy
    //   (total minimum to read fakePlayer: 318 bytes; full struct: 341 bytes)
    //
    // Note: Team is not stored in the userinfo table. It is tracked via
    // player_team game events or CCSPlayerController entity state.
    private const int MinPlayerInfoBytes = 318;
    private const string UserinfoTable = "userinfo";

    // ── Hostile-input bounds ──────────────────────────────────────────────────
    // A .dem is untrusted input: demos arrive from third-party sites, and a truncated download
    // reaches this decoder too — BitBuffer zero-fills past the end rather than failing, so an
    // over-read is not an error signal. Every attacker-controlled size below is therefore bounded
    // before it drives an allocation or a loop.

    // Entries are KEYED by their string-table index, so an index no longer sizes an
    // allocation: naming slot 2,000,000,000 costs exactly one map slot, the same as naming slot 0.
    // The index therefore needs no domain ceiling any more — only the representational one below
    // (the key is an `int`). What still costs memory is the number of entries PRESENT, so that is
    // what is bounded, and it is bounded per-table-lifetime rather than per-message because the
    // map accumulates across create + update deltas.
    //
    // The bound cannot be left to the structural `num_entries` check alone. With the old dense
    // list, max-index and entry-count were the same number, so capping the index capped both; a
    // keyed map decouples them and `num_entries <= RemainingBits / MinBitsPerEntry` constrains only
    // the count's rate, not its total. The cheapest entry that still yields a DISTINCT index is the
    // 3-bit sequential shorthand (isSequential=1, hasString=0, hasUserData=0), and — unlike the
    // varint path — a run of those needs no entropy, so it Snappy-compresses to nothing: ~200
    // compressed bytes expand to the 16 MiB MaxStringDataBytes ceiling, declare ~44.7M entries,
    // and every one of them is a distinct key. At ~36 bytes per Dictionary<int, Entry> slot that
    // is ~1.6 GiB live from ~200 bytes of demo. So the count needs its own explicit ceiling.
    //
    // 4096 is the entry ceiling the old index bound already enforced in practice (index <= 4096 implied
    // count <= 4097), so the memory ceiling this file guarantees is unchanged — only the index
    // restriction is lifted. It stays far above the domain: only `userinfo` is materialized, whose
    // real size is 64 entries (CS2 caps at 64 player slots), so valid demos never approach it and
    // output is unchanged by construction. Revisit if another table joins _materializedTables.
    private const int MaxEntriesPerTable = 4096;

    // Highest valid player slot. CS2 has 64 player slots, so a `userinfo` index outside 0..63 is
    // not a player no matter what its blob decodes to — see ExtractPlayersFromState, which is
    // where this is actually true. Kept separate from MaxEntriesPerTable on purpose: that one
    // bounds MEMORY (entries present), this one bounds MEANING (which keys can become a
    // PlayerInfo). A table may legally hold 64 cheap entries at absurd indices; none of them is a
    // player.
    private const int MaxPlayerSlot = 63;

    // Ceiling on a DECOMPRESSED string_data blob, checked against Snappy's declared output length
    // before decompressing (a decompression bomb: a few KiB of input declaring gigabytes of
    // output). Valve's own maximum_size_bytes hints are 48 KiB (create) / 256 KiB (update) and a
    // real userinfo table is ~22 KiB, so 16 MiB is far above anything legitimate.
    private const int MaxStringDataBytes = 16 * 1024 * 1024;

    // Cheapest possible entry: isSequential + hasString + hasUserData, all zero. A message cannot
    // contain more entries than its own bits allow, which bounds the decode loop against a
    // declared num_entries of int.MaxValue (a ~2-billion-iteration spin over zero-filled reads).
    private const int MinBitsPerEntry = 3;

    // Allowlist of string tables whose entries enrich actually consumes. Today only `userinfo`
    // (it is the sole input to `Players`, this processor's only output). Every other table's
    // entries are never read downstream, so we skip decoding/copying them entirely — that work
    // was ~100% of Pass-3 enrich allocation (and the generic decoder mis-reads non-userinfo tables
    // such as `instancebaseline` anyway: the entity layer has its own correct instancebaseline
    // decoder). To materialize another table here, add its name AND confirm the generic
    // bitstream decoder can actually decode it — class-id-keyed tables (instancebaseline) and the
    // avatar table need a bespoke decoder, not just an allowlist entry. Player extraction stays
    // bound to `userinfo` specifically (see ExtractPlayers), independent of this set.
    private static readonly HashSet<string> _materializedTables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            UserinfoTable
        };

    // Tables indexed by creation order (for UpdateStringTable.table_id lookups).
    // Index i corresponds to the i-th svc_CreateStringTable message received.
    private readonly List<TableState> _byId = [];

    // Tables indexed by name (for CDemoStringTables and CreateStringTable lookups).
    private readonly Dictionary<string, TableState> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, PlayerInfo> _players = new();

    /// <summary>
    ///     Snapshot of all known players, keyed by slot (0–63), after each update.
    /// </summary>
    public IReadOnlyDictionary<int, PlayerInfo> Players => _players;

    private static bool IsMaterialized(string tableName) => _materializedTables.Contains(tableName);

    /// <summary>
    ///     Processes a <c>svc_CreateStringTable</c> message.
    ///     Assigns the next sequential table-id and decodes initial entries from
    ///     <c>string_data</c> (a Source-engine bitstream; Snappy-decompressed first if
    ///     <c>data_compressed</c> is set).
    /// </summary>
    public void ProcessCreate(CSVCMsg_CreateStringTable msg)
    {
        TableState state = GetOrCreateByName(msg.Name);
        state.UserDataFixedSize = msg.UserDataFixedSize;
        state.UserDataSizeBits = msg.UserDataSizeBits;
        state.UsingVarintBitcounts = msg.UsingVarintBitcounts;
        _byId.Add(state);

        // Decode only the tables enrich consumes (see MaterializedTables). The table is still
        // registered above so `table_id` lookups in ProcessUpdate stay aligned for skipped tables.
        if (IsMaterialized(state.Name) && !msg.StringData.IsEmpty)
        {
            try
            {
                // Must stay inside the try. DecompressBounded and Snappy both throw on bad input,
                // and nothing between here and the public Parse overloads catches.
                ReadOnlySpan<byte> raw = msg.DataCompressed
                    ? DecompressBounded(msg.StringData.Span, msg.Name)
                    : msg.StringData.Span;

                DecodeEntries(raw, msg.NumEntries, state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StringTableProcessor] CreateStringTable decode error for '{msg.Name}': {ex.Message}");
                // S11 diagnostics channel (v0.6.0): Debug.WriteLine is [Conditional("DEBUG")] — a
                // Release build saw NOTHING, and every table rejected = a silent no-player parse.
                ParseDiagnostics.Warn(ParseWarningCodes.StringTableCreateFailed,
                    $"String table '{msg.Name}' failed to decode and was skipped ({ex.GetType().Name}).");
            }
        }

        ExtractPlayers();
    }

    // ── Data sources ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Processes a <c>CDemoStringTables</c> message (from <c>DEM_StringTables</c> or a
    ///     <c>DEM_FullPacket</c> checkpoint).  Entries are stored directly in the protobuf with
    ///     no bitstream encoding — each <c>items_t</c> has a <c>str</c> key and a <c>data</c> blob.
    ///     This snapshot is treated as authoritative: existing entries for each table are reset
    ///     before repopulation.
    /// </summary>
    public void ProcessSnapshot(CDemoStringTables msg)
    {
        foreach (CDemoStringTables.Types.table_t table in msg.Tables)
        {
            // Materialize only the tables enrich consumes (see MaterializedTables); copying other
            // tables' blobs here is never read downstream.
            if (!IsMaterialized(table.TableName))
            {
                continue;
            }

            TableState state = GetOrCreateByName(table.TableName);
            state.Entries.Clear();

            // Item ORDINAL is the string-table index — stated explicitly now that entries are
            // keyed. (The old code appended to a dense list, which said the same thing implicitly:
            // the i-th item landed at position i.) Key by position, never by `item.Str`: the
            // position is the slot, the string is only the entry's name, and duplicate names are
            // legal.
            int ordinal = 0;
            foreach (CDemoStringTables.Types.items_t item in table.Items)
            {
                // Same ceiling as the bitstream path, so MaxEntriesPerTable's per-table-lifetime
                // guarantee holds however a table was populated. Without it a huge snapshot would
                // not merely cost memory — it would leave the map permanently above the ceiling and
                // so make every LATER update on this table throw on its first new entry, including
                // legitimate re-keys of slots 0-63. Stop rather than throw: unlike ProcessCreate /
                // ProcessUpdate this call site has no try/catch, and one bad table must not abort
                // the demo. The `Clear` above means there is no accumulation to reason about — this
                // bounds a single message.
                if (ordinal >= MaxEntriesPerTable)
                {
                    Debug.WriteLine(
                        $"[StringTableProcessor] snapshot for '{table.TableName}' exceeds "
                        + $"{MaxEntriesPerTable} entries; ignoring the remainder.");
                    ParseDiagnostics.Warn(ParseWarningCodes.StringTableTruncated,
                        $"String-table snapshot '{table.TableName}' exceeds {MaxEntriesPerTable} entries; "
                        + "the remainder was ignored.");
                    break;
                }

                state.Entries[ordinal++] =
                    new Entry(item.Str, item.Data.IsEmpty ? null : item.Data.ToByteArray());
            }
        }

        // We do NOT clear _players before re-extracting. GOTV demos recorded mid-match may
        // have initial CDemoStringTables entries for players who later disconnect; subsequent
        // CDemoStringTables snapshots (from DEM_FullPacket checkpoints) will omit those players
        // because the server has cleaned them up. Clearing would silently lose their names,
        // making game-event death/kill rows show "#4" instead of the real name.
        // Merge-only: update slots where we have valid data; keep historical entries intact.
        ExtractPlayers();
    }

    /// <summary>
    ///     Processes a <c>svc_UpdateStringTable</c> message.
    ///     Looks up the table by <c>table_id</c> (assigned during <see cref="ProcessCreate" />)
    ///     and decodes the delta entries from <c>string_data</c>.
    /// </summary>
    public void ProcessUpdate(CSVCMsg_UpdateStringTable msg)
    {
        if (msg.TableId < 0 || msg.TableId >= _byId.Count)
        {
            return;
        }

        TableState state = _byId[msg.TableId];

        // Decode only the tables enrich consumes (see MaterializedTables); skip the rest entirely.
        if (!IsMaterialized(state.Name))
        {
            return;
        }

        if (!msg.StringData.IsEmpty)
        {
            try
            {
                DecodeEntries(msg.StringData.Span, msg.NumChangedEntries, state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StringTableProcessor] UpdateStringTable decode error for table id {msg.TableId}: {ex.Message}");
                ParseDiagnostics.Warn(ParseWarningCodes.StringTableUpdateFailed,
                    $"An update to string table '{state.Name}' failed to decode and was skipped "
                    + $"({ex.GetType().Name}).");
            }
        }

        // Player extraction is bound to `userinfo` specifically (not just "any materialized table"),
        // so adding a future table to the allowlist can't accidentally re-route player parsing.
        if (state.Name.Equals(UserinfoTable, StringComparison.OrdinalIgnoreCase))
        {
            ExtractPlayersFromState(state);
        }
    }

    /// <summary>
    ///     Snappy-decompresses a <c>string_data</c> blob, refusing one whose DECLARED output size
    ///     exceeds <see cref="MaxStringDataBytes" />. The declared length is attacker-controlled and
    ///     drives the allocation, so it is checked before decompressing, not after.
    /// </summary>
    private static byte[] DecompressBounded(ReadOnlySpan<byte> compressed, string tableName)
    {
        // GetUncompressedLength returns int, so >= 2^31 wraps negative and passes an
        // upper-bound-only check.
        int declared = Snappy.GetUncompressedLength(compressed);
        if (declared < 0 || declared > MaxStringDataBytes)
        {
            throw new InvalidDataException(
                $"String table '{tableName}': compressed string_data declares {declared} bytes, "
                + $"above the {MaxStringDataBytes}-byte maximum (malformed or hostile demo data).");
        }

        return Snappy.DecompressToArray(compressed);
    }

    // ── Source-engine string table bitstream decoder ──────────────────────────

    /// <summary>
    ///     Decodes <paramref name="numEntries" /> entries from the Source-engine string-table
    ///     bitstream format into <paramref name="state" />.  The format is:
    ///     <para>
    ///         <b>CS2 <c>UsingVarintBitcounts</c> mode:</b> when the create-message has this flag,
    ///         non-sequential entry indices and variable-size user-data lengths stop using fixed bit
    ///         fields — but they do NOT switch to the same encoding as each other. The index becomes a
    ///         protobuf <c>UVarInt32</c>; the user-data length becomes Source's <c>UBitVar</c>. The
    ///         flag is stored in <see cref="TableState.UsingVarintBitcounts" /> and checked per-entry.
    ///     </para>
    ///     <code>
    ///     for each entry:
    ///       isSequential : 1 bit
    ///       if !isSequential:
    ///         UsingVarintBitcounts=true  → entryIndex : UVarInt32
    ///         UsingVarintBitcounts=false → entryIndex : 11 bits (safe default)
    ///       hasString    : 1 bit
    ///       if hasString:
    ///         isSubstring: 1 bit
    ///         if isSubstring: historyIndex : 5 bits; bytesToCopy : 5 bits; suffix : null-str
    ///         else:           fullString   : null-terminated UTF-8
    ///       hasUserData  : 1 bit
    ///       if hasUserData:
    ///         if fixedSize: userData : UserDataSizeBits bits
    ///         UsingVarintBitcounts=true  → length : UBitVar;  userData : length bytes
    ///         UsingVarintBitcounts=false → length : 17 bits;  userData : length bytes
    ///     </code>
    /// </summary>
    /// <remarks>
    ///     <c>internal</c> rather than private so the hostile-input bounds can be exercised
    ///     directly: both call sites swallow exceptions per-table (by design — one bad table must
    ///     not abort the demo), so a test driving them could never observe the guard firing.
    /// </remarks>
    internal static void DecodeEntries(ReadOnlySpan<byte> data, int numEntries, TableState state)
    {
        BitBuffer buf = new(data);

        // num_entries is attacker-controlled and used directly as the loop bound. Bits present is
        // a hard structural ceiling on how many entries the message can actually carry.
        int maxEntriesInBuffer = buf.RemainingBits / MinBitsPerEntry;
        if (numEntries < 0 || numEntries > maxEntriesInBuffer)
        {
            throw new InvalidDataException(
                $"String table '{state.Name}': declares {numEntries} entries, but the message holds "
                + $"at most {maxEntriesInBuffer} (malformed or hostile demo data).");
        }

        // Circular ring buffer of recent entry names — avoids O(n) List.RemoveAt(0) on eviction.
        string[] history = new string[32];
        int historyHead = 0; // next write position (oldest slot after wrap)
        int historyCount = 0; // number of valid entries (≤ 32)
        int lastIndex = -1;
        bool varint = state.UsingVarintBitcounts;

        for (int i = 0; i < numEntries; i++)
        {
            // Entry index: sequential shorthand (+1) or explicit value. Read the explicit form as
            // UNSIGNED and range-check before narrowing: values >= 2^31 cast to a negative int,
            // which would slip past the growth loop below and fault later at the indexer instead.
            uint declaredIndex = buf.ReadOneBit()
                ? (uint)(lastIndex + 1)
                : varint
                    ? buf.ReadUVarInt32()
                    : buf.ReadUBits(11); // 11 bits → max 2047 entries; safe default

            // The only ceiling the INDEX still needs is representational — entries are keyed by
            // `int`. It is not a domain bound: a large sparse index is now legal and costs one map
            // slot. This check remains load-bearing anyway, because it is what stops a value >= 2^31
            // from narrowing to a NEGATIVE key (which would corrupt the next sequential index too).
            // Also covers `lastIndex + 1` overflowing off int.MaxValue, which wraps to int.MinValue
            // and so reads back as 2^31 here.
            if (declaredIndex > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"String table '{state.Name}': entry index {declaredIndex} is not representable "
                    + "as a signed 32-bit key (malformed or hostile demo data).");
            }

            int entryIndex = (int)declaredIndex;
            lastIndex = entryIndex;

            // Bound the map by entries PRESENT — checked BEFORE the insert, so nothing is allocated
            // once the ceiling is reached. Re-keying an entry that already exists is always allowed
            // (that is what a real userinfo delta does), hence the ContainsKey; it only runs once
            // the table is already at the ceiling — never on valid input — so the happy path stays
            // a single lookup. See MaxEntriesPerTable for why the count, not the index, is the
            // quantity that needs a ceiling.
            if (state.Entries.Count >= MaxEntriesPerTable && !state.Entries.ContainsKey(entryIndex))
            {
                throw new InvalidDataException(
                    $"String table '{state.Name}': holds {state.Entries.Count} entries, at the maximum of "
                    + $"{MaxEntriesPerTable} (malformed or hostile demo data).");
            }

            // One lookup for the whole per-entry read-modify-write. The previous dense form
            // re-indexed the collection for each `with` expression; through a ref, the name and
            // user-data writes below both land in the map's own storage with no further hashing.
            ref Entry entry = ref CollectionsMarshal.GetValueRefOrAddDefault(
                state.Entries, entryIndex, out bool existed);
            if (!existed)
            {
                // default(Entry) has a NULL Key; the dense list padded with string.Empty. Match the
                // old initial value exactly — an entry claimed with hasString=0 keeps an empty name.
                entry = new Entry(string.Empty, null);
            }

            // String name, with optional prefix compression against history.
            bool hasString = buf.ReadOneBit();
            if (hasString)
            {
                string name;
                if (buf.ReadOneBit()) // is a suffix of a historical entry
                {
                    int histIdx = (int)buf.ReadUBits(5); // which history slot (0-31, relative to oldest)
                    int copyLen = (int)buf.ReadUBits(5); // bytes to copy from that entry
                    string prefix;
                    if (histIdx < historyCount)
                    {
                        int actual = (historyHead - historyCount + histIdx + 32) % 32;
                        string hist = history[actual];
                        prefix = hist[..Math.Min(copyLen, hist.Length)];
                    }
                    else
                    {
                        prefix = string.Empty;
                    }

                    name = prefix + buf.ReadStringUtf8();
                }
                else
                {
                    name = buf.ReadStringUtf8();
                }

                entry.Key = name;

                // Write into the circular ring buffer (evicts oldest entry when full).
                history[historyHead] = name;
                historyHead = (historyHead + 1) % 32;
                if (historyCount < 32)
                {
                    historyCount++;
                }
            }

            // User data blob (e.g. player_info_t for userinfo entries).
            bool hasUserData = buf.ReadOneBit();
            if (hasUserData)
            {
                // Both branches size an allocation from attacker-controlled input. Unlike an entry
                // index these ARE lengths — they describe bytes that must be present — so the
                // buffer itself supplies the bound, no magic number needed.
                byte[] userData;
                if (state.UserDataFixedSize)
                {
                    // Fixed-size: read exactly UserDataSizeBits bits into a byte array. The bit
                    // count comes from the create-message (attacker-controlled); long arithmetic
                    // so a hostile int.MaxValue can't overflow the +7 rounding into a negative.
                    long byteCount = ((long)state.UserDataSizeBits + 7) / 8;
                    if (state.UserDataSizeBits < 0 || byteCount > buf.RemainingBytes)
                    {
                        throw new InvalidDataException(
                            $"String table '{state.Name}': fixed user-data size of {state.UserDataSizeBits} bits "
                            + $"exceeds the {buf.RemainingBytes} bytes remaining (malformed or hostile demo data).");
                    }

                    userData = new byte[byteCount];
                    buf.ReadBitsAsBytes(userData.AsSpan(), state.UserDataSizeBits);
                }
                else
                {
                    // Variable-size: length prefix then bytes.
                    // UsingVarintBitcounts → UBitVar; legacy → 17-bit fixed.
                    //
                    // UBitVar, NOT UVarInt32 — despite the flag's name and despite the entry index
                    // above reading UVarInt32 under that same flag. The two fields differ, and
                    // reading this one as a protobuf varint consumes 8 bits where the wire spent 6,
                    // desyncing the rest of the entry: the length came back as ~5140 instead of
                    // ~35, tripped the bounds check below, and threw away `userinfo` on EVERY CS2
                    // demo (all 15 protocol versions tested). The roster survived only because
                    // CDemoStringTables snapshots repopulate it by a different path, so the visible
                    // damage was a spurious "this demo may be damaged" warning plus the loss of the
                    // GOTV recorder's slot.
                    uint declaredBytes = varint
                        ? buf.ReadUBitVar()
                        : buf.ReadUBits(17);
                    if (declaredBytes > (uint)buf.RemainingBytes)
                    {
                        throw new InvalidDataException(
                            $"String table '{state.Name}': entry user-data claims {declaredBytes} bytes, but only "
                            + $"{buf.RemainingBytes} remain (malformed or hostile demo data).");
                    }

                    userData = buf.ReadBytes((int)declaredBytes);
                }

                entry.Value = userData;
            }
        }
    }

    // ── Player info extraction ────────────────────────────────────────────────

    private void ExtractPlayers()
    {
        if (!_byName.TryGetValue(UserinfoTable, out TableState? state))
        {
            return;
        }

        ExtractPlayersFromState(state);
    }

    private void ExtractPlayersFromState(TableState state)
    {
        // Merge delta: only write slots that have a non-empty blob; do not clear entries that
        // are absent or have a zero-length blob.
        //
        // A null Value means the entry was not present in the bitstream (unchanged slot).
        // A zero-length byte[] means the server sent a blob header but with 0 bytes of content.
        // In CS2 DEM_FullPacket frames the CDemoStringTables snapshot (which has real data for
        // all 10 slots) is immediately followed by a svc_UpdateStringTable delta that may encode
        // those same slots with empty blobs.  If we treat byte[0] as "slot freed" we silently
        // delete players that are still active.
        //
        // Skipping empty blobs is safe because NO caller clears _players first — not even
        // ProcessSnapshot, which is merge-only on purpose (see its comment: GOTV snapshots recorded
        // mid-match omit players who later disconnect, and clearing would lose their names). So a
        // slot with nothing meaningful to say simply keeps whatever we already knew about it,
        // rather than being wrongly deleted.
        // The slot comes from the entry's KEY. It used to be inferred from the list position, which
        // only worked because position and index were the same thing; reading the key states it.
        // Iteration order is unspecified for a Dictionary, and that is fine: every slot below is
        // written or removed independently of the others, so the resulting _players map is the same
        // whatever order the keys arrive in.
        foreach ((int slot, Entry entry) in state.Entries)
        {
            // A `userinfo` index outside CS2's 0..63 player slots is not a player, whatever its
            // blob contains — reject it BEFORE the parse, so a hostile key can neither be added to
            // _players nor take the "slot freed" branch and Remove something. This is the domain
            // knowledge that used to live in the decoder's index cap, moved to the one place where
            // "this integer is a player slot" is actually true.
            if ((uint)slot > MaxPlayerSlot)
            {
                continue;
            }

            if (entry.Value is not { Length: > 0 } blob)
            {
                continue; // null or empty blob = no meaningful data, skip
            }

            PlayerInfo? player = TryParsePlayerInfo(slot, blob);
            if (player is not null)
            {
                _players[slot] = player;
            }
            else
            {
                // Non-empty blob present but unreadable = slot freed. When the slot was OCCUPIED,
                // that is a silent player deletion — the exact S11 "no players, no explanation"
                // ingredient — so it now leaves a warning. (An empty→unreadable transition on a
                // never-occupied slot is normal churn and stays quiet.)
                if (_players.Remove(slot))
                {
                    ParseDiagnostics.Warn(ParseWarningCodes.PlayerInfoUnreadable,
                        $"Player slot {slot}'s userinfo became unreadable; the player was dropped from the roster.");
                }
            }
        }
    }

    // ── Internal state ────────────────────────────────────────────────────────

    private TableState GetOrCreateByName(string name)
    {
        if (!_byName.TryGetValue(name, out TableState? state))
        {
            state = new TableState(name);
            _byName[name] = state;
        }

        return state;
    }

    private static string ReadNullTerminatedUtf8(byte[] data, int offset, int maxLen)
    {
        int end = offset;
        int limit = Math.Min(offset + maxLen, data.Length);
        while (end < limit && data[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(data, offset, end - offset);
    }

    private static PlayerInfo? TryParsePlayerInfo(int slot, byte[] data)
    {
        // CS2 encodes userinfo entries as protobuf (~30–80 bytes).
        // CS:GO used a fixed 341-byte binary player_info_t struct (318+ bytes minimum).
        if (data.Length < MinPlayerInfoBytes)
        {
            return TryParsePlayerInfoCs2Proto(slot, data);
        }

        try
        {
            ulong xuid = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8, 8));
            string name = ReadNullTerminatedUtf8(data, 16, 128);
            int userId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(144, 4));
            bool isBot = data[316] != 0;
            bool isHltv = data[317] != 0;

            // Reject completely empty slots (no player connected).
            if (xuid == 0 && !isBot && name.Length == 0)
            {
                return null;
            }

            return new PlayerInfo(slot, name, xuid, userId, 0, isBot)
            {
                IsHltv = isHltv
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StringTableProcessor] TryParsePlayerInfo (CSGO binary) slot {slot}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Parses a CS2 proto-encoded userinfo entry using <see cref="CodedInputStream" />
    ///     which correctly handles all protobuf wire types and safely skips any unknown fields.
    ///     Wire format (proto field numbers observed in CS2 demos):
    ///     <list type="bullet">
    ///         <item>Field 1 (LEN): player name (UTF-8 string)</item>
    ///         <item>Field 2 (FIXED64): SteamID64</item>
    ///         <item>Field 3+ (any): other IDs — skipped</item>
    ///     </list>
    ///     In CS2, the string-table slot index equals the game-event type-9 controller index,
    ///     so we use <paramref name="slot" /> directly as <see cref="PlayerInfo.UserId" />.
    /// </summary>
    private static PlayerInfo? TryParsePlayerInfoCs2Proto(int slot, byte[] data)
    {
        try
        {
            CodedInputStream stream = new(data);
            string name = string.Empty;
            ulong xuid = 0;
            bool isBot = false;
            bool isHltv = false;

            // Field numbers are CMsgPlayerInfo's (cs2-opendocs networkbasetypes.proto): 1 name,
            // 2 xuid, 3 userid, 4 steamid, 5 fakeplayer, 6 ishltv. fakeplayer/ishltv were skipped
            // here until 2026-07-26, which left every CS2 demo's bots unflagged and — because the
            // GOTV proxy occupies a userinfo slot with a name and no SteamID — made the recorder
            // indistinguishable from a player, inflating player counts by one.
            uint tag;
            while ((tag = stream.ReadTag()) != 0)
            {
                int fieldNum = WireFormat.GetTagFieldNumber(tag);
                switch (fieldNum)
                {
                    case 1: name = stream.ReadString(); break;
                    case 2: xuid = stream.ReadFixed64(); break;
                    case 5: isBot = stream.ReadBool(); break;
                    case 6: isHltv = stream.ReadBool(); break;
                    default: stream.SkipLastField(); break;
                }
            }

            if (name.Length == 0 && xuid == 0)
            {
                return null;
            }

            // In CS2, string-table slot == game-event type-9 controller index.
            return new PlayerInfo(slot, name, xuid, slot, 0, isBot)
            {
                IsHltv = isHltv
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StringTableProcessor] TryParsePlayerInfoCs2Proto slot {slot}: {ex.Message}");
            return null;
        }
    }

    // internal (not private) so the bounds tests can drive DecodeEntries directly — see its
    // remarks. Nested in an internal class, so this widens nothing outside the assembly.
    internal sealed class TableState(string name)
    {
        /// <summary>
        ///     Entries keyed by string-table index (for <c>userinfo</c>, the player slot).
        ///     Keyed rather than positional so cost tracks entries PRESENT rather than the largest
        ///     index seen — string tables are sparsely addressed (a delta may touch slot 40 without
        ///     mentioning 0–39), and with a dense list that sparsity had to be paid for in padding.
        /// </summary>
        public Dictionary<int, Entry> Entries { get; } = new();

        /// <summary>Name.</summary>
        public string Name => name;

        /// <summary>User data fixed size.</summary>
        public bool UserDataFixedSize { get; set; }

        /// <summary>User data size bits.</summary>
        public int UserDataSizeBits { get; set; }

        // When true, non-sequential indices and variable user-data lengths use UVarInt32
        // encoding rather than fixed bit widths (CS2-native string table format).
        /// <summary>Using varint bitcounts.</summary>
        public bool UsingVarintBitcounts { get; set; }
    }

    internal record struct Entry(string Key, byte[]? Value);
}
