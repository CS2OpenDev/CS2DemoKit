namespace CS2DemoKit.Parser;

/// <summary>Which entry point produced a result.</summary>
public enum DecodeSource
{
    /// <summary>
    ///     The whole-file parse: <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile)" />, its
    ///     options overload, and <see cref="DemoReader.Materialize" />, which is what they run.
    /// </summary>
    DemoParserParse,

    /// <summary>The forward reader.</summary>
    DemoReader
}

/// <summary>How the frames were decoded.</summary>
public enum DecodeMode
{
    /// <summary>Every frame decoded in one parallel pass over the whole file, then retained.</summary>
    ParallelWholeFile,

    /// <summary>One frame at a time on the consuming thread.</summary>
    Sequential,

    /// <summary>A bounded window of frames decoded in parallel, yielded in order, then dropped.</summary>
    WindowedParallel
}

/// <summary>Why a frame stream ended.</summary>
public enum ReadEndReason
{
    /// <summary><c>DEM_Stop</c> was reached.</summary>
    Stop,

    /// <summary>The input ended on a frame boundary with no <c>DEM_Stop</c>.</summary>
    EndOfData,

    /// <summary>The input ended inside a frame header or payload.</summary>
    Truncated,

    /// <summary>A frame header declared a size that cannot be real; nothing past it is readable.</summary>
    Corrupt
}

/// <summary>
///     What a parse or read actually did, so a caller and a measurement can say which path produced a
///     result and how much of the file it decoded. Counts are totals over the frames read.
/// </summary>
/// <param name="Source">The entry point.</param>
/// <param name="Mode">The decode arrangement.</param>
/// <param name="ReadAheadFrames">The read-ahead window; zero for a sequential read or a whole-file parse.</param>
/// <param name="MaxDegreeOfParallelism">The worker cap in force, or null for unbounded.</param>
/// <param name="FramesRead">Frames decoded, whether or not they were retained.</param>
/// <param name="MessagesDecoded">Inner messages that became a <see cref="NetMessage" />.</param>
/// <param name="MessagesSkipped">Inner messages left undecoded because the plan did not ask for them.</param>
/// <param name="UserCmdsStored">Payloads appended to the user-command arena.</param>
/// <param name="BytesDecompressed">Snappy output bytes across every compressed frame.</param>
public sealed record DecodeProvenance(
    DecodeSource Source,
    DecodeMode Mode,
    int ReadAheadFrames,
    int? MaxDegreeOfParallelism,
    long FramesRead,
    long MessagesDecoded,
    long MessagesSkipped,
    long UserCmdsStored,
    long BytesDecompressed);
