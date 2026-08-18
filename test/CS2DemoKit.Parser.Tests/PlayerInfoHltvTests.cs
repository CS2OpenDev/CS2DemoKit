#region

using Google.Protobuf;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The <c>userinfo</c> table's <c>fakeplayer</c> / <c>ishltv</c> flags (CMsgPlayerInfo fields 5
///     and 6 — see cs2-opendocs <c>networkbasetypes.proto</c>).
///     <para>
///         Both were skipped by the CS2 protobuf parse path, which had two consequences on every CS2
///         demo: bots were never flagged, and the GOTV proxy — which occupies a real slot, carries a
///         name and has no SteamID — was indistinguishable from a person who played. That inflated
///         every "how many players" surface by one (the reported symptom: Match Overview showing 11
///         players for a 10-player match).
///     </para>
///     <para>
///         Driven through the public <see cref="StringTableProcessor.ProcessCreate" /> entry point
///         with a synthetic table, so it covers the real decode → extract path without a demo file.
///     </para>
/// </summary>
[Category("Unit")]
public class PlayerInfoHltvTests
{
    // Builds a userinfo string table carrying one entry per supplied player blob, in slot order.
    private static CSVCMsg_CreateStringTable UserinfoTable(params CMsgPlayerInfo[] players)
    {
        BitWriter bits = new();
        foreach (CMsgPlayerInfo p in players)
        {
            byte[] blob = p.ToByteArray();
            bits.One();          // isSequential → next index
            bits.Zero();         // hasString = 0 (the name lives in the blob)
            bits.One();          // hasUserData = 1
            bits.VarInt((uint)blob.Length);
            foreach (byte b in blob)
            {
                bits.Raw(b, 8);
            }
        }

        return new CSVCMsg_CreateStringTable
        {
            Name = "userinfo",
            NumEntries = players.Length,
            UsingVarintBitcounts = true,
            StringData = ByteString.CopyFrom(bits.ToArray())
        };
    }

    private static CMsgPlayerInfo Player(string name, ulong xuid, bool fakePlayer = false, bool isHltv = false) =>
        new()
        {
            Name = name,
            Xuid = xuid,
            Fakeplayer = fakePlayer,
            Ishltv = isHltv
        };

    /// <summary>
    ///     The reported bug, at the parser level: a 10-player lobby plus the GOTV recorder yields 11
    ///     userinfo entries, of which exactly one is flagged <see cref="PlayerInfo.IsHltv" /> — so a
    ///     consumer can count 10.
    /// </summary>
    [Test]
    public async Task GotvProxy_IsFlagged_SoRealPlayersCountTen()
    {
        CMsgPlayerInfo[] lobby = new CMsgPlayerInfo[11];
        for (int i = 0; i < 10; i++)
        {
            lobby[i] = Player($"player{i}", 76561190000000000UL + (ulong)i);
        }

        // The recorder as CS2 actually emits it: named, no SteamID, and BOTH flags set.
        lobby[10] = Player("DemoRecorder", 0, fakePlayer: true, isHltv: true);

        StringTableProcessor processor = new();
        processor.ProcessCreate(UserinfoTable(lobby));

        await Assert.That(processor.Players.Count).IsEqualTo(11)
            .Because("the proxy still occupies a slot — it is flagged, not dropped, so slot lookups are unchanged");
        await Assert.That(processor.Players.Values.Count(p => p.IsHltv)).IsEqualTo(1);
        await Assert.That(processor.Players.Values.Count(p => !p.IsHltv && p.Name.Length > 0)).IsEqualTo(10)
            .Because("this is the count a 'players' surface should show");
        await Assert.That(processor.Players.Values.Single(p => p.IsHltv).Name).IsEqualTo("DemoRecorder");
    }

    /// <summary>A bot is flagged from <c>fakeplayer</c> — it is a participant, so it is NOT hltv.</summary>
    [Test]
    public async Task FakePlayer_SetsIsBot_WithoutSettingIsHltv()
    {
        StringTableProcessor processor = new();
        processor.ProcessCreate(UserinfoTable(
            Player("Human", 76561190000000001UL),
            Player("BOT Rock", 0, fakePlayer: true)));

        PlayerInfo bot = processor.Players.Values.Single(p => p.Name == "BOT Rock");
        PlayerInfo human = processor.Players.Values.Single(p => p.Name == "Human");

        await Assert.That(bot.IsBot).IsTrue()
            .Because("fakeplayer was previously never read on the CS2 path, so every bot looked human");
        await Assert.That(bot.IsHltv).IsFalse().Because("a bot plays the match; the GOTV proxy does not");
        await Assert.That(human.IsBot).IsFalse();
        await Assert.That(human.IsHltv).IsFalse();
    }

    // LSB-first bit writer matching BitBuffer's read order (see StringTableBoundsTests.Bits).
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitPos;

        public void One(bool value = true)
        {
            if (_bitPos == 0)
            {
                _bytes.Add(0);
            }

            if (value)
            {
                _bytes[^1] |= (byte)(1 << _bitPos);
            }

            _bitPos = (_bitPos + 1) % 8;
        }

        public void Zero() => One(false);

        public void Raw(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                One((value & (1u << i)) != 0);
            }
        }

        public void VarInt(uint value)
        {
            while (value >= 0x80)
            {
                Raw((value & 0x7F) | 0x80, 8);
                value >>= 7;
            }

            Raw(value, 8);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
