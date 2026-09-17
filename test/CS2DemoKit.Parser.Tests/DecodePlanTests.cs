namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     A plan's membership rules, the presets, and the mask compiled from them. The mask is what the
///     decode hot path indexes, so it must agree with the plan for every id including the ones past
///     the catalog.
/// </summary>
public class DecodePlanTests
{
    [Test]
    public async Task Everything_DecodesEveryKnownIdAndCommand()
    {
        DecodePlan plan = DecodePlan.Everything;
        await Assert.That(plan.DecodesEverything).IsTrue();
        await Assert.That(plan.WalksPackets).IsTrue();
        foreach (int id in NetMessageCatalog.Names.Keys)
        {
            await Assert.That(plan.Decodes(id)).IsTrue();
        }

        await Assert.That(plan.Decodes(400)).IsTrue();
        foreach (int command in NetMessageCatalog.DemoCommandNames.Keys)
        {
            EDemoCommands cmd = (EDemoCommands)command;
            if (cmd is EDemoCommands.DemStop or EDemoCommands.DemError or EDemoCommands.DemMax or EDemoCommands.DemIsCompressed)
            {
                continue;
            }

            await Assert.That(plan.Decodes(cmd)).IsTrue();
        }

        await Assert.That(plan.Decodes(EDemoCommands.DemStop)).IsFalse();
    }

    [Test]
    public async Task StructureOnly_DecodesNothingButWalksPackets()
    {
        DecodePlan plan = DecodePlan.StructureOnly;
        await Assert.That(plan.DecodesEverything).IsFalse();
        await Assert.That(plan.WalksPackets).IsTrue();
        await Assert.That(plan.RecordStructure).IsTrue();
        foreach (int id in NetMessageCatalog.Names.Keys)
        {
            await Assert.That(plan.Decodes(id)).IsFalse();
        }

        await Assert.That(plan.Decodes(400)).IsFalse();
        await Assert.That(plan.Decodes(EDemoCommands.DemFileHeader)).IsFalse();
        await Assert.That(plan.Decodes(EDemoCommands.DemPacket)).IsTrue();

        DecodeMask mask = DecodeMask.Compile(plan);
        await Assert.That(mask.RecordStructure).IsTrue();
        await Assert.That(mask.UserCmds).IsFalse();
        await Assert.That(mask.FullPacketStringTable).IsFalse();
        await Assert.That(mask.DecodeUnknown).IsFalse();
    }

    [Test]
    public async Task GameEventsOnly_KeepsHeaderRosterAndEvents()
    {
        DecodePlan plan = DecodePlan.GameEventsOnly;
        await Assert.That(plan.Decodes((int)EBaseGameEvents.GeSource1LegacyGameEvent)).IsTrue();
        await Assert.That(plan.Decodes((int)EBaseGameEvents.GeSource1LegacyGameEventList)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcServerInfo)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcCreateStringTable)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcPacketEntities)).IsFalse();
        await Assert.That(plan.Decodes(NetMessageCatalog.UserCmdsTypeId)).IsFalse();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcFlattenedSerializer)).IsFalse();
        await Assert.That(plan.Decodes((int)NET_Messages.NetTick)).IsFalse();
        await Assert.That(plan.Decodes(EDemoCommands.DemSendTables)).IsFalse();
        await Assert.That(plan.Decodes(EDemoCommands.DemFileHeader)).IsTrue();
        await Assert.That(plan.Decodes(EDemoCommands.DemStringTables)).IsTrue();
        await Assert.That(DecodeMask.Compile(plan).FullPacketStringTable).IsTrue();
    }

    [Test]
    public async Task EntityReplay_KeepsWhatTheTrackerConsumes()
    {
        DecodePlan plan = DecodePlan.EntityReplay;
        await Assert.That(plan.RetainSignonPrefix).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcPacketEntities)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcFlattenedSerializer)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcClassInfo)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcServerInfo)).IsTrue();
        await Assert.That(plan.Decodes((int)SVC_Messages.SvcUpdateStringTable)).IsTrue();
        await Assert.That(plan.Decodes(EDemoCommands.DemSendTables)).IsTrue();
        await Assert.That(plan.Decodes(EDemoCommands.DemClassInfo)).IsTrue();
        await Assert.That(plan.Decodes((int)EBaseGameEvents.GeSource1LegacyGameEvent)).IsFalse();
        await Assert.That(plan.Decodes(NetMessageCatalog.UserCmdsTypeId)).IsFalse();
    }

    [Test]
    public async Task IncludeAndExclude_ExcludeWins()
    {
        DecodePlan plan = new()
        {
            Categories = MessageCategories.None,
            IncludeMessageTypes = new HashSet<int> { (int)NET_Messages.NetTick, 400 },
            ExcludeMessageTypes = new HashSet<int> { (int)NET_Messages.NetTick }
        };
        await Assert.That(plan.Decodes((int)NET_Messages.NetTick)).IsFalse();
        await Assert.That(plan.Decodes(400)).IsTrue();
        await Assert.That(plan.Decodes((int)NET_Messages.NetSetConVar)).IsFalse();
        await Assert.That(plan.WalksPackets).IsTrue();
        await Assert.That(plan.DecodesEverything).IsFalse();

        DecodeMask mask = DecodeMask.Compile(plan);
        await Assert.That(mask.DecodesNet((int)NET_Messages.NetTick)).IsFalse();
        await Assert.That(mask.DecodesNet(400)).IsTrue();
        await Assert.That(mask.DecodesNet(100000)).IsFalse();
        await Assert.That(mask.WalkPackets).IsTrue();

        DecodePlan excludeOnly = new() { ExcludeMessageTypes = new HashSet<int> { (int)SVC_Messages.SvcPacketEntities } };
        await Assert.That(excludeOnly.DecodesEverything).IsFalse();
        await Assert.That(excludeOnly.Decodes((int)SVC_Messages.SvcPacketEntities)).IsFalse();
        await Assert.That(excludeOnly.Decodes((int)SVC_Messages.SvcServerInfo)).IsTrue();
        await Assert.That(DecodeMask.Compile(excludeOnly).DecodesNet(100000)).IsTrue();
    }

    [Test]
    public async Task HeaderOnly_DoesNotWalkPackets()
    {
        DecodePlan plan = new() { Categories = MessageCategories.None };
        await Assert.That(plan.WalksPackets).IsFalse();
        await Assert.That(plan.Decodes(EDemoCommands.DemPacket)).IsFalse();
        await Assert.That(plan.Decodes(EDemoCommands.DemFullPacket)).IsFalse();
        await Assert.That(DecodeMask.Compile(plan).WalkPackets).IsFalse();

        DecodeMask everything = DecodeMask.Everything;
        await Assert.That(everything.DecodesCommand(EDemoCommands.DemPacket)).IsTrue();
        await Assert.That(everything.DecodesCommand(EDemoCommands.DemStop)).IsFalse();
        await Assert.That(everything.UserCmds).IsTrue();
        await Assert.That(everything.EventNames).IsNull();
    }
}
