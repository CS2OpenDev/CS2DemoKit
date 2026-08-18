using CS2DemoKit.Parser.GameEvents;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Decode battery for the events the schema extractor cannot see — <c>item_drop</c>,
///     <c>halftime</c>, <c>game_restart</c> — which ship from
///     <c>CS2OpenDev.Sdk.GameEvents</c>' curated supplement since 4.0.
/// </summary>
/// <remarks>
///     Until 4.0 these had local records here (<c>SupplementaryEvents</c>, deleted with this
///     battery's rewrite), because the SDK's registry resolved them to nothing and the decode fell
///     through to <see cref="UnknownGameEvent" /> — which does not match an edge typed on
///     <c>ItemDropEvent</c>, so the trigger went quiet with nothing logged. These tests pin the
///     SDK path so a future SDK build that loses the supplement fails here instead of going quiet
///     again the same way.
/// </remarks>
[Category("Unit")]
public class CuratedEventDecodeTests
{
    private const int ItemDropId = 90;
    private const int HalfTimeId = 91;
    private const int GameRestartId = 92;

    private static GameEventDecoder DecoderWithSchema()
    {
        CMsgSource1LegacyGameEventList list = new();
        list.Descriptors.Add(Descriptor(ItemDropId, "item_drop", ("userid", 9), ("item", 1)));
        list.Descriptors.Add(Descriptor(HalfTimeId, "halftime"));
        list.Descriptors.Add(Descriptor(GameRestartId, "game_restart"));

        GameEventDecoder decoder = new();
        decoder.LoadSchema(list);
        return decoder;
    }

    private static CMsgSource1LegacyGameEventList.Types.descriptor_t Descriptor(
        int id, string name, params (string Name, int Type)[] keys)
    {
        CMsgSource1LegacyGameEventList.Types.descriptor_t d = new() { Eventid = id, Name = name };
        foreach ((string keyName, int type) in keys)
        {
            d.Keys.Add(new CMsgSource1LegacyGameEventList.Types.key_t { Name = keyName, Type = type });
        }

        return d;
    }

    private static CMsgSource1LegacyGameEvent Fire(
        int id, string name, params CMsgSource1LegacyGameEvent.Types.key_t[] keys)
    {
        CMsgSource1LegacyGameEvent e = new() { Eventid = id, EventName = name };
        e.Keys.AddRange(keys);
        return e;
    }

    [Test]
    public async Task ItemDrop_DecodesToTheSdkPayload_WithUserIdAndItem()
    {
        // Key type 9 is the CS2-only controller-slot tag; the value rides in val_short.
        GameEvent evt = DecoderWithSchema().Decode(
            Fire(ItemDropId, "item_drop",
                new CMsgSource1LegacyGameEvent.Types.key_t { Type = 9, ValShort = 4 },
                new CMsgSource1LegacyGameEvent.Types.key_t { Type = 1, ValString = "weapon_ak47" }),
            frameTick: 1200,
            frameNumber: 7);

        await Assert.That(evt.Payload).IsTypeOf<ItemDropEvent>();

        ItemDropEvent drop = (ItemDropEvent)evt.Payload!;
        await Assert.That(drop.UserId).IsEqualTo(4);
        await Assert.That(drop.Item).IsEqualTo("weapon_ak47");
        await Assert.That(evt.Name).IsEqualTo("item_drop");
        await Assert.That(evt.GameTick).IsEqualTo(1200);
    }

    [Test]
    [Arguments(HalfTimeId, "halftime", typeof(HalfTimeEvent))]
    [Arguments(GameRestartId, "game_restart", typeof(GameRestartEvent))]
    public async Task FieldlessCuratedEvents_DecodeToTheirSdkPayloadTypes(int id, string name, Type expected)
    {
        GameEvent evt = DecoderWithSchema().Decode(Fire(id, name), frameTick: 10, frameNumber: 1);

        await Assert.That(evt.Payload).IsNotNull();
        await Assert.That(evt.Payload!.GetType()).IsEqualTo(expected);
        await Assert.That(evt.Name).IsEqualTo(name);
    }

    [Test]
    public async Task AnEventNobodyKnows_StillFallsThroughToUnknown()
    {
        // The fallback stays reachable: the curated supplement is a named list in the SDK, not a
        // catch-all, and a genuinely unknown event must not be typed by guesswork.
        CMsgSource1LegacyGameEventList list = new();
        list.Descriptors.Add(Descriptor(404, "not_a_real_event", ("thing", 1)));

        GameEventDecoder decoder = new();
        decoder.LoadSchema(list);

        GameEvent evt = decoder.Decode(
            Fire(404, "not_a_real_event",
                new CMsgSource1LegacyGameEvent.Types.key_t { Type = 1, ValString = "x" }),
            frameTick: 5,
            frameNumber: 1);

        await Assert.That(evt).IsTypeOf<UnknownGameEvent>();
        // Named from the demo's descriptor, not positionally.
        await Assert.That(((UnknownGameEvent)evt).Fields.ContainsKey("thing")).IsTrue();
    }
}
