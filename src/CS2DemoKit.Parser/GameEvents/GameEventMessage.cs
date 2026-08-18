#region

using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

#endregion

namespace CS2DemoKit.Parser.GameEvents;

/// <summary>
///     A <see cref="NetMessage" /> whose payload was a CS2 game event that has been fully
///     decoded into a typed <see cref="GameEvent" /> record.
///     Created by the parser's enrichment pass (pass 3) — replaces the raw <c>NetMessage</c>
///     slot for every <c>CMsgSource1LegacyGameEvent</c> message in each frame.
/// </summary>
public sealed class GameEventMessage : NetMessage
{
    private static readonly Empty _syntheticPayload = new();

    [SetsRequiredMembers]
    internal GameEventMessage(
        string typeName, IMessage? payload,
        int? decompressedStart, int? decompressedLength,
        GameEvent decodedEvent)
        : base(typeName, payload, decompressedStart, decompressedLength) =>
        DecodedEvent = decodedEvent;

    /// <summary>The fully decoded, typed game event. Never null.</summary>
    public GameEvent DecodedEvent { get; }

    /// <summary>
    ///     Wraps a <b>synthesized</b> game event — one derived in a downstream layer rather than
    ///     decoded from a wire <c>CMsgSource1LegacyGameEvent</c>. The Analysis-layer
    ///     <c>EntityChangeScanner</c> uses this to inject entity-derived events (e.g.
    ///     <c>molotov_thrown</c>, attributed from a projectile's thrower handle) into the same
    ///     dispatch path as parsed events, so rules can trigger on them with <c>on:</c> + read
    ///     <c>event.*</c> fields. Carries an empty payload placeholder; dispatch keys off
    ///     <see cref="DecodedEvent" />'s runtime type, not the payload.
    /// </summary>
    public static GameEventMessage ForSynthesizedEvent(GameEvent decodedEvent) =>
        new("synthetic", _syntheticPayload, null, null, decodedEvent);
}
