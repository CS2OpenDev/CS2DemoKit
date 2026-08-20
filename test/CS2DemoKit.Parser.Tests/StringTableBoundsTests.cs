using System.Text;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Hostile-input bounds on the string-table bitstream decoder. A <c>.dem</c> is
///     untrusted: demos come from third-party sites, and a truncated download hits the same decoder
///     — <see cref="BitBuffer" /> zero-fills past the end instead of failing, so an over-read is not
///     an error signal and every attacker-controlled size must be bounded explicitly.
///     <para>
///         These drive <c>StringTableProcessor.DecodeEntries</c> directly (internal, see its
///         remarks): the public entry points catch per-table by design — one bad table must not
///         abort the demo — so a test going through them could never observe a guard firing. The
///         last test covers that swallowing path on purpose.
///     </para>
///     <para>
///         Fixtures are built with <see cref="StringTableBitWriter" />, which appends LSB-first to
///         match <see cref="BitBuffer" />'s read order and is the single place the wire encodings
///         are written down.
///     </para>
/// </summary>
[Category("Unit")]
public class StringTableBoundsTests
{
    /// <summary>
    ///     S14 hygiene: the swallow paths here Warn without ever constructing a
    ///     <see cref="ParsedDemo" />, stranding [ThreadStatic] warnings on this pool thread for
    ///     whatever runs on it next (the mechanism behind the ±1 Warnings.Count flake). This
    ///     drain is BEST-EFFORT residue reduction only — an async test body may resume on a
    ///     different thread than it warned on, and hook thread-affinity is not guaranteed — so
    ///     count-sensitive tests must still pre-drain on their own thread; see
    ///     <c>ParseOptionsTests.EmptyOptions_ParsesIdenticallyToTheOptionsLessOverload</c>.
    /// </summary>
    [After(Test)]
    public void DrainStrandedWarnings() => ParseDiagnostics.Drain();

    private static StringTableProcessor.TableState VarintTable() =>
        new("userinfo")
        {
            UsingVarintBitcounts = true
        };

    // The headline defect, and the point of keying the entries: a 5-byte varint naming slot
    // 2,147,483,647 used to pad a dense list to it — tens of GiB of Entry structs from five bytes
    // of input — so the old bounds fix had to REFUSE the index outright at a domain-derived 4096. With the
    // entries keyed, the same index costs exactly one map slot, so it is simply decoded. No cap on
    // the index is needed because the index no longer sizes anything.
    [Test]
    public async Task DecodeEntries_HugeVarintIndex_StoresOneEntry_InsteadOfPaddingToIt()
    {
        StringTableProcessor.TableState state = VarintTable();
        byte[] data = new StringTableBitWriter()
            .Zero().VarInt(2147483647) // explicit index int.MaxValue
            .One() // hasString
            .Zero() // not a history suffix
            .Raw('h', 8).Raw('i', 8).Raw(0, 8) // "hi\0"
            .Zero() // hasUserData = 0
            .ToArray();

        StringTableProcessor.DecodeEntries(data, 1, state);

        await Assert.That(state.Entries.Count).IsEqualTo(1)
            .Because("cost tracks entries PRESENT, not the largest index seen");
        await Assert.That(state.Entries[2147483647].Key).IsEqualTo("hi")
            .Because("the entry is addressable at the index the wire named");
    }

    // Values >= 2^31 used to cast to a NEGATIVE int, slipping past the growth loop and faulting
    // later at the indexer instead — a different, more confusing failure. Reading unsigned and
    // range-checking before the narrowing cast is what makes this the same clean rejection.
    [Test]
    public async Task DecodeEntries_IndexAboveInt32Range_ThrowsInvalidData_NotIndexOutOfRange()
    {
        StringTableProcessor.TableState state = VarintTable();
        byte[] data = new StringTableBitWriter().Zero().VarInt(2147483648).ToArray();

        Exception? ex = null;
        try
        {
            StringTableProcessor.DecodeEntries(data, 1, state);
        }
        catch (Exception e)
        {
            ex = e;
        }

        await Assert.That(ex).IsTypeOf<InvalidDataException>()
            .Because("the unsigned range check runs before the int cast that would go negative");
    }

    // The cap that replaced the index cap. Keying the entries decouples "largest index" from
    // "number of entries", and only the second one costs memory — so that is what is bounded, at
    // the same 4096 ceiling the old index bound enforced in practice (index <= 4096 implied count <= 4097).
    // Each entry here is the cheapest that still yields a DISTINCT key: the 3-bit sequential
    // shorthand.
    [Test]
    [Arguments(4096, false)] // exactly the cap — allowed
    [Arguments(4097, true)] // one past — refused
    public async Task DecodeEntries_EntryCountCapBoundary(int entries, bool shouldThrow)
    {
        StringTableProcessor.TableState state = VarintTable();
        StringTableBitWriter bits = new();
        for (int i = 0; i < entries; i++)
        {
            bits.One().Zero().Zero(); // isSequential=1, hasString=0, hasUserData=0
        }

        bool threw = false;
        try
        {
            StringTableProcessor.DecodeEntries(bits.ToArray(), entries, state);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        await Assert.That(threw).IsEqualTo(shouldThrow);
        await Assert.That(state.Entries.Count).IsLessThanOrEqualTo(4096)
            .Because("the guard fires before the insert, so the map never exceeds the ceiling");
    }

    // Why the structural num_entries bound cannot carry the memory guarantee alone once entries are
    // keyed. A run of 3-bit sequential entries needs no entropy, so it Snappy-compresses to nothing:
    // ~200 compressed bytes reach the 16 MiB string_data ceiling, declare ~44.7M entries, and every
    // one is a distinct key (~1.6 GiB of map). RemainingBits/3 happily admits all of them — this is
    // the shape MaxEntriesPerTable exists to stop.
    [Test]
    public async Task DecodeEntries_CompressibleSequentialRun_StopsAtTheEntryCap()
    {
        StringTableProcessor.TableState state = VarintTable();
        const int claimed = 60000; // well past the cap, and within RemainingBits / 3
        StringTableBitWriter bits = new();
        for (int i = 0; i < claimed; i++)
        {
            bits.One().Zero().Zero();
        }

        InvalidDataException? ex = null;
        try
        {
            StringTableProcessor.DecodeEntries(bits.ToArray(), claimed, state);
        }
        catch (InvalidDataException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull()
            .Because("num_entries is structurally satisfiable here — only the entry-count cap refuses it");
        await Assert.That(state.Entries.Count).IsLessThanOrEqualTo(4096);
    }

    // Sparse addressing is real and load-bearing: a userinfo delta legitimately names slot 63
    // without mentioning 0-62. It used to be preserved by PADDING the list to the index; it is now
    // preserved by keying, which is the same capability at a fraction of the cost.
    [Test]
    public async Task DecodeEntries_ValidSparseIndex_KeysWithoutPadding()
    {
        StringTableProcessor.TableState state = VarintTable();
        byte[] data = new StringTableBitWriter()
            .Zero().VarInt(63) // explicit index 63
            .One() // hasString
            .Zero() // not a history suffix
            .Raw('h', 8).Raw('i', 8).Raw(0, 8) // "hi\0"
            .Zero() // hasUserData = 0
            .ToArray();

        StringTableProcessor.DecodeEntries(data, 1, state);

        await Assert.That(state.Entries.Count).IsEqualTo(1)
            .Because("slots 0-62 were never mentioned, so they cost nothing");
        await Assert.That(state.Entries[63].Key).IsEqualTo("hi");
    }

    // A LENGTH is checkable against the buffer — it describes bytes that must be present. This is
    // the case where "just use what the buffer knows" genuinely works, unlike an index.
    [Test]
    public async Task DecodeEntries_UserDataLengthBeyondBuffer_Throws()
    {
        StringTableProcessor.TableState state = VarintTable();
        byte[] data = new StringTableBitWriter()
            .One() // sequential index → 0
            .Zero() // hasString = 0
            .One() // hasUserData = 1
            .UBitVar(2000000000) // claims ~2 GB of payload
            .ToArray();

        InvalidDataException? ex = null;
        try
        {
            StringTableProcessor.DecodeEntries(data, 1, state);
        }
        catch (InvalidDataException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull().Because("a payload longer than the message cannot be real");
        await Assert.That(ex!.Message).Contains("2000000000");
    }

    // num_entries is attacker-controlled and was used directly as the loop bound: int.MaxValue
    // spins ~2 billion iterations over zero-filled reads (a hang, not an OOM).
    [Test]
    public async Task DecodeEntries_NumEntriesBeyondWhatTheMessageCanHold_Throws()
    {
        StringTableProcessor.TableState state = VarintTable();
        byte[] data = new StringTableBitWriter().One().Zero().Zero().ToArray(); // one tiny entry's worth of bits

        InvalidDataException? ex = null;
        try
        {
            StringTableProcessor.DecodeEntries(data, int.MaxValue, state);
        }
        catch (InvalidDataException e)
        {
            ex = e;
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("at most")
            .Because("the bound is structural — bits present divided by the cheapest entry");
    }

    // The caller's contract: a hostile table is dropped, the demo keeps parsing. This is why the
    // guards throw rather than clamp — the graceful degradation already exists one level up.
    [Test]
    public async Task ProcessCreate_WithHostileTable_SwallowsAndLeavesNoPlayers()
    {
        StringTableProcessor processor = new();
        CSVCMsg_CreateStringTable msg = new()
        {
            Name = "userinfo",
            NumEntries = 1,
            UsingVarintBitcounts = true,
            // 2^31 — not representable as a signed key, and the value that used to narrow to a
            // NEGATIVE index and fault later at the indexer instead.
            StringData = Google.Protobuf.ByteString.CopyFrom(
                new StringTableBitWriter().Zero().VarInt(2147483648).ToArray())
        };

        processor.ProcessCreate(msg); // must not throw out of the public entry point

        await Assert.That(processor.Players.Count).IsEqualTo(0);
    }

    // ── Player-slot range (the domain knowledge the index cap used to carry) ──────────────────

    /// <summary>
    ///     A one-entry <c>userinfo</c> create-message whose entry sits at <paramref name="index" />
    ///     and carries a CS2 proto-encoded player blob (field 1 = name).
    /// </summary>
    private static CSVCMsg_CreateStringTable UserinfoWithPlayerAt(uint index, string name)
    {
        byte[] blob = [0x0A, (byte)name.Length, .. Encoding.UTF8.GetBytes(name)];

        StringTableBitWriter bits = new StringTableBitWriter()
            .Zero().VarInt(index) // explicit entry index — UVarInt32 on the wire
            .Zero() // hasString = 0
            .One() // hasUserData = 1
            .UBitVar((uint)blob.Length); // length — UBitVar on the wire, NOT the index's encoding
        foreach (byte b in blob)
        {
            bits.Raw(b, 8);
        }

        bits.Raw(0, 8).Raw(0, 8); // trailing pad, so the length check has whole bytes to count

        return new CSVCMsg_CreateStringTable
        {
            Name = "userinfo",
            NumEntries = 1,
            UsingVarintBitcounts = true,
            StringData = Google.Protobuf.ByteString.CopyFrom(bits.ToArray())
        };
    }

    // Control: the range check must not be over-tight. 63 is the highest legal CS2 player slot and
    // must still produce a player, keyed by that slot.
    [Test]
    public async Task ProcessCreate_PlayerAtHighestLegalSlot_IsExtracted()
    {
        StringTableProcessor processor = new();
        processor.ProcessCreate(UserinfoWithPlayerAt(63, "Bob"));

        await Assert.That(processor.Players.Count).IsEqualTo(1);
        await Assert.That(processor.Players[63].Name).IsEqualTo("Bob")
            .Because("the slot comes from the entry KEY, not from a list position");
    }

    // With the index cap gone, an absurd index decodes cheaply into the table — which is the point
    // — but it must not become a PlayerInfo. CS2 has 64 player slots, so the constraint lives in
    // ExtractPlayersFromState, where "this integer is a player slot" is actually true.
    [Test]
    public async Task ProcessCreate_EntryBeyondTheLastPlayerSlot_IsNotAPlayer()
    {
        StringTableProcessor processor = new();
        processor.ProcessCreate(UserinfoWithPlayerAt(2000000000, "Mallory"));

        await Assert.That(processor.Players.Count).IsEqualTo(0)
            .Because("a userinfo index outside 0..63 is not a player, whatever its blob decodes to");
    }

    // The range check must reject BEFORE the parse: reaching TryParsePlayerInfo with an unreadable
    // blob takes the "slot freed" branch, and an out-of-range key must never drive a Remove.
    [Test]
    public async Task ProcessCreate_OutOfRangeSlot_DoesNotEvictAValidPlayer()
    {
        StringTableProcessor processor = new();
        processor.ProcessCreate(UserinfoWithPlayerAt(7, "Alice"));
        processor.ProcessCreate(UserinfoWithPlayerAt(2000000007, "Mallory"));

        await Assert.That(processor.Players.Count).IsEqualTo(1);
        await Assert.That(processor.Players[7].Name).IsEqualTo("Alice");
    }
}
