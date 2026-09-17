#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A frame source over a bare frame list, for the evaluator's list overloads and for tests
///     that hand-build frames. Carries an enrichment view when the caller has one and an empty
///     view otherwise, so name and team resolution degrade rather than fail.
/// </summary>
internal sealed class FrameListSource(IReadOnlyList<DemoFrame> frames, IDemoEnrichmentView? enrichment) : IDemoFrameSource
{
    private int _next;
    private IReadOnlyList<DemoFrame>? _signonPrefix;

    public IDemoEnrichmentView Enrichment { get; } = enrichment ?? EmptyEnrichment.Instance;

    public int? FrameCount => frames.Count;

    public double? Progress => null;

    public bool SupportsRandomAccess => true;

    public IReadOnlyList<DemoFrame>? Frames => frames;

    public IReadOnlyList<DemoFrame> SignonPrefix
    {
        get
        {
            if (_signonPrefix is null)
            {
                List<DemoFrame> prefix = [];
                foreach (DemoFrame frame in frames)
                {
                    if (frame.CommandKind == EDemoCommands.DemPacket)
                    {
                        break;
                    }

                    prefix.Add(frame);
                }

                _signonPrefix = prefix;
            }

            return _signonPrefix;
        }
    }

    public DemoFrame? LastInstanceBaselineFullPacket { get; private set; }

    public bool TryReadNext([NotNullWhen(true)] out DemoFrame? frame)
    {
        if (_next >= frames.Count)
        {
            frame = null;
            return false;
        }

        frame = frames[_next++];
        if (frame.CommandKind == EDemoCommands.DemFullPacket && CarriesInstanceBaseline(frame))
        {
            LastInstanceBaselineFullPacket = frame;
        }

        return true;
    }

    public bool TryPeekNext([NotNullWhen(true)] out DemoFrame? frame)
    {
        if (_next >= frames.Count)
        {
            frame = null;
            return false;
        }

        frame = frames[_next];
        return true;
    }

    private static bool CarriesInstanceBaseline(DemoFrame frame)
    {
        foreach (NetMessage msg in frame.DecodedMessages)
        {
            if (msg.Payload is not CDemoStringTables snapshot)
            {
                continue;
            }

            foreach (CDemoStringTables.Types.table_t table in snapshot.Tables)
            {
                if (table.TableName == "instancebaseline")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed class EmptyEnrichment : IDemoEnrichmentView
    {
        public static readonly EmptyEnrichment Instance = new();
        private static readonly IReadOnlyDictionary<int, PlayerInfo> _noPlayers = new Dictionary<int, PlayerInfo>();

        public int TickRate => 64;

        public float TickInterval => 1f / 64f;

        public string MapName => string.Empty;

        public string ServerName => string.Empty;

        public string ClientName => string.Empty;

        public int BuildNumber => 0;

        public int ServerStartTick => 0;

        public int TickCount => 0;

        public bool TickCountIsFinal => true;

        public DemoProfile Profile => DemoProfile.Unknown;

        public RuntimeSchema? Schema => null;

        public IReadOnlyDictionary<int, PlayerInfo> Players => _noPlayers;

        public IReadOnlyList<ParseWarning> Warnings => [];

        public ParseHealth Health => ParseHealth.Clean;

        public DemoDescriptor Snapshot() => new(string.Empty, 64, 1f / 64f, 0, 0, string.Empty, string.Empty, 0,
            DemoProfile.Unknown, _noPlayers);
    }
}
