// Generated Valve protobuf types (CDemoPacket, CSVCMsg_PacketEntities, CCSUsrMsg_*, ...) live in
// CS2OpenSchema.Protos, shipped prebuilt by the CS2OpenDev.Protos package. They were in the global
// namespace once, which made the published package a CS0433 hazard next to any other CS2 parser.
// Importing them globally keeps every unqualified reference (and every <see cref="..."/> naming
// one) working without touching call sites.

global using CS2OpenSchema.Protos;

// Typed game-event payload records (PlayerDeathEvent, PlayerTeamEvent, ...) from
// CS2OpenDev.Sdk.GameEvents, reached transitively through CS2DemoKit.Parser. These replaced the 272
// records our own generator used to emit; importing them globally keeps unqualified references
// reading the way the generated ones did.
global using CS2OpenSchema.Events;
