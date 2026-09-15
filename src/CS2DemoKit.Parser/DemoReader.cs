#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Parser.Entities;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     Reads a demo forward, one frame at a time, decoding what the plan asks for and keeping
///     nothing the caller has let go of. Forward-only in what it retains, not in what it can
///     address: the input is always a buffer or a mapping, so <see cref="Configure" /> and
///     <see cref="ProbeGameEventNames" /> before the first read are cheap rewinds.
///     <para>
///         Single consumer. <see cref="TryReadNext" />, <see cref="TryPeekNext" />,
///         <see cref="ReadFrames" />, <see cref="Materialize" /> and <see cref="Dispose" /> must
///         not overlap. A yielded frame holds offsets, decoded messages and arena blocks, never a
///         slice of the input, so it stays valid after the reader is disposed. The enrichment view
///         runs one frame ahead of the consumer while a frame is peeked.
///     </para>
///     <para>
///         Live memory while reading is the enrichment state, the current frame plus whatever the
///         caller still holds, the signon prefix when retained, one instancebaseline full packet,
///         one grow-only decompression buffer, and at most two arena blocks when user commands are
///         decoded. Nothing scales with the frames already read.
///     </para>
/// </summary>
public sealed class DemoReader : IDemoFrameSource, IDisposable
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly MemoryMappedDemoSource? _owned;
    private readonly ParseOptions _options;
    private readonly DemoProfile? _profileOverride;
    private readonly ParseDiagnostics _diagnostics = new();
    private readonly DemoParser.PartitionState _state = new();
    private readonly Dictionary<string, int>? _dropCounts;
    private readonly List<DemoFrame> _signonPrefix = [];
    private readonly View _view;
    private DecodeMask _mask;
    private DemoEnrichmentCursor _cursor;
    private int _pos = 16;
    private int _frameNumber;
    private int _nextProgressAt;
    private DemoFrame? _peeked;
    private bool _started;
    private bool _prefixDone;
    private bool _disposed;

    private DemoReader(ReadOnlyMemory<byte> data, MemoryMappedDemoSource? owned, ParseOptions? options,
        DemoProfile? profileOverride)
    {
        if (data.Length < 16 || !"PBDEMS2"u8.SequenceEqual(data.Span[..7]))
        {
            owned?.Dispose();
            throw new InvalidDataException("Not a CS2 demo file (invalid magic bytes).");
        }

        _data = data;
        _owned = owned;
        _options = options ?? new ParseOptions();
        _profileOverride = profileOverride;
        _dropCounts = _options.CountDropSites ? new Dictionary<string, int>() : null;
        _mask = ReferenceEquals(_options.Plan, DecodePlan.Everything) ? DecodeMask.Everything : DecodeMask.Compile(_options.Plan);
        _cursor = new DemoEnrichmentCursor(_diagnostics, _mask, profileOverride);
        _view = new View(this);
        _nextProgressAt = ProgressStride;
        ProbeHeader();
    }

    private static readonly DecodeMask _headerProbeMask =
        DecodeMask.Compile(new DecodePlan { Categories = MessageCategories.Header });

    // Feeds the cursor the file header and the server info before the first read, so TickRate,
    // MapName and Profile are known at build time. Nothing is consumed: the read starts at frame
    // zero and observes the same frames again, which is idempotent for these fields.
    private void ProbeHeader()
    {
        DemoParser.PartitionState scratch = new();
        ParseDiagnostics scratchDiagnostics = new();
        int pos = 16;
        int frameNumber = 0;
        while (!_cursor.TickIntervalObserved
               && DemoParser.TryScanFrame(_data, ref pos, frameNumber, scratchDiagnostics, out DemoParser.FrameDescriptor d)
               == DemoParser.FrameScanResult.Frame)
        {
            if (d.Command == EDemoCommands.DemPacket)
            {
                break;
            }

            _cursor.Observe(DemoParser.DecodeFrame(d, frameNumber++, scratch, _headerProbeMask, null, null), null);
        }
    }

    /// <summary>Opens a reader over bytes the caller owns and keeps alive for the read.</summary>
    /// <exception cref="InvalidDataException">The input is not a CS2 demo.</exception>
    public static DemoReader Open(ReadOnlyMemory<byte> data, ParseOptions? options = null, DemoProfile? profileOverride = null) =>
        new(data, null, options, profileOverride);

    /// <summary>
    ///     Opens a reader over a memory-mapped file. The reader owns the mapping and releases it in
    ///     <see cref="Dispose" />. Only for files that will not be written while mapped; see
    ///     <see cref="MemoryMappedDemoSource" />.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a CS2 demo.</exception>
    public static DemoReader OpenFile(string path, ParseOptions? options = null, DemoProfile? profileOverride = null)
    {
        MemoryMappedDemoSource source = MemoryMappedDemoSource.Open(path);
        return new DemoReader(source.Memory, source, options, profileOverride);
    }

    public DecodePlan Plan => _mask.Plan;

    /// <summary>A fresh snapshot of the counters so far.</summary>
    public DecodeProvenance Provenance => new(DecodeSource.DemoReader, DecodeMode.Sequential, 0,
        _options.MaxDegreeOfParallelism is > 0 ? _options.MaxDegreeOfParallelism : null,
        _frameNumber, _state.MessagesDecoded, _state.MessagesSkipped, _state.UserCmdsStored, _state.BytesDecompressed);

    /// <summary>Why the stream ended, or null while frames remain.</summary>
    public ReadEndReason? EndReason { get; private set; }

    /// <summary>Byte offset of the next frame header.</summary>
    public long Position => _pos;

    public long Length => _data.Length;

    public IDemoEnrichmentView Enrichment => _view;

    public int? FrameCount => null;

    public double? Progress => (double)_pos / _data.Length;

    public bool SupportsRandomAccess => false;

    public IReadOnlyList<DemoFrame>? Frames => null;

    public IReadOnlyList<DemoFrame> SignonPrefix => _signonPrefix;

    public DemoFrame? LastInstanceBaselineFullPacket => _cursor.LastInstanceBaselineFullPacket;

    /// <summary>
    ///     Replaces the plan before the first read. The enrichment state starts over, which costs
    ///     nothing before a frame has been read.
    /// </summary>
    /// <exception cref="InvalidOperationException">A frame has already been read or peeked.</exception>
    public void Configure(DecodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfDisposed();
        if (_started)
        {
            throw new InvalidOperationException("The plan can only be changed before the first frame is read.");
        }

        _mask = ReferenceEquals(plan, DecodePlan.Everything) ? DecodeMask.Everything : DecodeMask.Compile(plan);
        _cursor = new DemoEnrichmentCursor(_diagnostics, _mask, _profileOverride);
        ProbeHeader();
    }

    /// <summary>True once a frame has been read or peeked; the before-first-read operations are then closed.</summary>
    public bool Started => _started;

    /// <summary>
    ///     The distinct game-event names the demo fires, from a structure-only pass that decodes
    ///     the event list and each fire's id and nothing else. What tells a tournament recording
    ///     from a matchmaking one when the header cannot. Allowed only before the first read.
    /// </summary>
    /// <exception cref="InvalidOperationException">A frame has already been read or peeked.</exception>
    public IReadOnlySet<string> ProbeGameEventNames(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_started)
        {
            throw new InvalidOperationException("The vocabulary probe runs before the first frame is read.");
        }

        DecodeMask mask = DecodeMask.Compile(new DecodePlan { Categories = MessageCategories.GameEvents });
        DemoParser.PartitionState state = new();
        ParseDiagnostics scratch = new();
        Dictionary<int, string> nameById = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        int pos = 16;
        int frameNumber = 0;
        while (DemoParser.TryScanFrame(_data, ref pos, frameNumber, scratch, out DemoParser.FrameDescriptor d) == DemoParser.FrameScanResult.Frame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DemoFrame frame = DemoParser.DecodeFrame(d, frameNumber++, state, mask, null, null);
            foreach (NetMessage msg in frame.MessageList)
            {
                switch (msg.Payload)
                {
                    case CMsgSource1LegacyGameEventList list:
                        foreach (CMsgSource1LegacyGameEventList.Types.descriptor_t descriptor in list.Descriptors)
                        {
                            nameById[descriptor.Eventid] = descriptor.Name;
                        }

                        break;
                    case CMsgSource1LegacyGameEvent fire:
                        names.Add(nameById.TryGetValue(fire.Eventid, out string? name) ? name : $"event_{fire.Eventid}");
                        break;
                }
            }
        }

        return names;
    }

    public bool TryReadNext([NotNullWhen(true)] out DemoFrame? frame)
    {
        ThrowIfDisposed();
        if (_peeked is not null)
        {
            frame = _peeked;
            _peeked = null;
            return true;
        }

        frame = DecodeNext();
        return frame is not null;
    }

    public bool TryPeekNext([NotNullWhen(true)] out DemoFrame? frame)
    {
        ThrowIfDisposed();
        _peeked ??= DecodeNext();
        frame = _peeked;
        return frame is not null;
    }

    /// <summary>Every remaining frame, in order.</summary>
    public IEnumerable<DemoFrame> ReadFrames()
    {
        while (TryReadNext(out DemoFrame? frame))
        {
            yield return frame;
        }
    }

    /// <summary>
    ///     Decodes the whole file into a <see cref="ParsedDemo" /> under the reader's plan, using the
    ///     parallel whole-file parse. Allowed only before the first read; the reader is spent after.
    /// </summary>
    /// <exception cref="InvalidOperationException">A frame has already been read or peeked.</exception>
    public ParsedDemo Materialize()
    {
        ThrowIfDisposed();
        if (_started)
        {
            throw new InvalidOperationException("Materialize runs instead of a forward read, not after one.");
        }

        _started = true;
        return DemoParser.Parse(_data, _options with { Plan = _mask.Plan }, _profileOverride);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _peeked = null;
        _owned?.Dispose();
    }

    private const int ProgressStride = 1 << 20;

    private DemoFrame? DecodeNext()
    {
        if (EndReason is not null)
        {
            return null;
        }

        _started = true;
        _options.CancellationToken.ThrowIfCancellationRequested();

        DemoParser.FrameScanResult scan = DemoParser.TryScanFrame(_data, ref _pos, _frameNumber, _diagnostics,
            out DemoParser.FrameDescriptor descriptor);
        if (scan != DemoParser.FrameScanResult.Frame)
        {
            EndReason = scan switch
            {
                DemoParser.FrameScanResult.Stop => ReadEndReason.Stop,
                DemoParser.FrameScanResult.EndOfData => ReadEndReason.EndOfData,
                DemoParser.FrameScanResult.Truncated => ReadEndReason.Truncated,
                _ => ReadEndReason.Corrupt
            };
            DemoParser.EmitDropWarnings(_diagnostics, _dropCounts);
            _options.Progress?.Report(1.0);
            return null;
        }

        DemoFrame frame = DemoParser.DecodeFrame(descriptor, _frameNumber, _state, _mask, _options.OnUnknownMessage, _dropCounts);
        _frameNumber++;
        _cursor.Observe(frame, null);

        if (!_prefixDone)
        {
            if (frame.CommandKind == EDemoCommands.DemPacket)
            {
                _prefixDone = true;
            }
            else if (_mask.Plan.RetainSignonPrefix)
            {
                _signonPrefix.Add(frame);
            }
        }

        if (_options.Progress is { } progress && _pos >= _nextProgressAt)
        {
            _nextProgressAt = _pos + ProgressStride;
            progress.Report(Math.Min(1.0, (double)_pos / _data.Length));
        }

        return frame;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class View(DemoReader reader) : IDemoEnrichmentView
    {
        public int TickRate => (int)MathF.Round(1f / reader._cursor.TickInterval);

        public float TickInterval => reader._cursor.TickInterval;

        public string MapName => reader._cursor.MapName;

        public string ServerName => reader._cursor.ServerName;

        public string ClientName => reader._cursor.ClientName;

        public int BuildNumber => reader._cursor.BuildNumber;

        public int ServerStartTick => reader._cursor.ServerStartTick;

        public int TickCount => reader._cursor.TickCount;

        public bool TickCountIsFinal => reader._cursor.PlaybackTicks is not null || reader.EndReason is not null;

        public DemoProfile Profile => reader._cursor.Profile;

        public RuntimeSchema? Schema => reader._cursor.Schema;

        public IReadOnlyDictionary<int, PlayerInfo> Players => reader._cursor.Players;

        public IReadOnlyList<ParseWarning> Warnings => reader._diagnostics.Snapshot();

        public ParseHealth Health => ParseWarningCodes.WorstOf(reader._diagnostics.Snapshot());

        public DemoDescriptor Snapshot() => new(MapName, TickRate, TickInterval, TickCount, ServerStartTick,
            ServerName, ClientName, BuildNumber, Profile, Players);
    }
}
