#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Parser.Entities;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     What a demo has said about itself so far: header fields, tick rate, schema, roster and
///     warnings. On a <see cref="ParsedDemo" /> every value is final; on a forward reader each one
///     reflects the frames read so far, and a value that arrives late in the file (the playback
///     tick count) is only final once the stream has ended.
/// </summary>
public interface IDemoEnrichmentView
{
    int TickRate { get; }

    float TickInterval { get; }

    string MapName { get; }

    string ServerName { get; }

    string ClientName { get; }

    int BuildNumber { get; }

    int ServerStartTick { get; }

    /// <summary>Playback ticks when known, else the highest tick seen so far.</summary>
    int TickCount { get; }

    /// <summary>True when <see cref="TickCount" /> can no longer change.</summary>
    bool TickCountIsFinal { get; }

    DemoProfile Profile { get; }

    RuntimeSchema? Schema { get; }

    /// <summary>The roster so far, each slot's team from its last <c>player_team</c> event.</summary>
    IReadOnlyDictionary<int, PlayerInfo> Players { get; }

    IReadOnlyList<ParseWarning> Warnings { get; }

    ParseHealth Health { get; }

    /// <summary>An immutable copy of the current values, for output that outlives the source.</summary>
    DemoDescriptor Snapshot();
}

/// <summary>
///     The demo-level facts an analysis result carries, detached from whichever source produced it.
/// </summary>
public sealed record DemoDescriptor(
    string MapName,
    int TickRate,
    float TickInterval,
    int TickCount,
    int ServerStartTick,
    string ServerName,
    string ClientName,
    int BuildNumber,
    DemoProfile Profile,
    IReadOnlyDictionary<int, PlayerInfo> Players)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(TickCount * TickInterval);

    public static DemoDescriptor From(ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return new DemoDescriptor(demo.MapName, demo.TickRate, demo.TickInterval, demo.TickCount,
            demo.ServerStartTick, demo.ServerName, demo.ClientName, demo.BuildNumber, demo.Profile, demo.Players);
    }
}

/// <summary>
///     Frames in recording order, read forward. A <see cref="ParsedDemo" /> is one implementation
///     (<see cref="ParsedDemoFrameSource.AsFrameSource" />) and keeps random access; the forward
///     reader is the other and keeps only what the caller still holds. A consumer that never
///     touches <see cref="Frames" /> runs the same over both.
/// </summary>
public interface IDemoFrameSource
{
    IDemoEnrichmentView Enrichment { get; }

    /// <summary>The total frame count when known up front, else null.</summary>
    int? FrameCount { get; }

    /// <summary>Fraction of the input consumed when <see cref="FrameCount" /> is unknown, else null.</summary>
    double? Progress { get; }

    bool SupportsRandomAccess { get; }

    /// <summary>The whole frame list, non-null exactly when <see cref="SupportsRandomAccess" />.</summary>
    IReadOnlyList<DemoFrame>? Frames { get; }

    /// <summary>
    ///     The frames before the first <c>DEM_Packet</c>, which carry the schema, class table and
    ///     initial string tables. Empty on a reader whose plan does not retain them.
    /// </summary>
    IReadOnlyList<DemoFrame> SignonPrefix { get; }

    /// <summary>
    ///     The most recent <c>DEM_FullPacket</c> read so far whose string-table snapshot carried
    ///     <c>instancebaseline</c>, or null before one has been read.
    /// </summary>
    DemoFrame? LastInstanceBaselineFullPacket { get; }

    /// <summary>Reads the next frame, or returns false once the stream has ended.</summary>
    bool TryReadNext([NotNullWhen(true)] out DemoFrame? frame);

    /// <summary>The frame the next <see cref="TryReadNext" /> would return, without consuming it.</summary>
    bool TryPeekNext([NotNullWhen(true)] out DemoFrame? frame);
}
