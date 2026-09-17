#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Parser.Entities;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>Presents a <see cref="ParsedDemo" /> as a frame source.</summary>
public static class ParsedDemoFrameSource
{
    /// <summary>
    ///     A fresh forward cursor over the demo's frames, with random access kept. Each call starts
    ///     at frame zero; the demo itself is not touched.
    /// </summary>
    public static IDemoFrameSource AsFrameSource(this ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return new Source(demo);
    }

    private sealed class Source(ParsedDemo demo) : IDemoFrameSource, IDemoEnrichmentView
    {
        private int _next;
        private IReadOnlyList<DemoFrame>? _signonPrefix;

        public IDemoEnrichmentView Enrichment => this;

        public int? FrameCount => demo.Frames.Count;

        public double? Progress => null;

        public bool SupportsRandomAccess => true;

        public IReadOnlyList<DemoFrame>? Frames => demo.Frames;

        public IReadOnlyList<DemoFrame> SignonPrefix
        {
            get
            {
                if (_signonPrefix is null)
                {
                    List<DemoFrame> prefix = [];
                    foreach (DemoFrame frame in demo.Frames)
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
            if (_next >= demo.Frames.Count)
            {
                frame = null;
                return false;
            }

            frame = demo.Frames[_next++];
            if (frame.CommandKind == EDemoCommands.DemFullPacket && DemoEnrichmentCursor.CarriesInstanceBaseline(frame))
            {
                LastInstanceBaselineFullPacket = frame;
            }

            return true;
        }

        public bool TryPeekNext([NotNullWhen(true)] out DemoFrame? frame)
        {
            if (_next >= demo.Frames.Count)
            {
                frame = null;
                return false;
            }

            frame = demo.Frames[_next];
            return true;
        }

        public int TickRate => demo.TickRate;

        public float TickInterval => demo.TickInterval;

        public string MapName => demo.MapName;

        public string ServerName => demo.ServerName;

        public string ClientName => demo.ClientName;

        public int BuildNumber => demo.BuildNumber;

        public int ServerStartTick => demo.ServerStartTick;

        public int TickCount => demo.TickCount;

        public bool TickCountIsFinal => true;

        public DemoProfile Profile => demo.Profile;

        public RuntimeSchema? Schema => demo.Schema;

        public IReadOnlyDictionary<int, PlayerInfo> Players => demo.Players;

        public IReadOnlyList<ParseWarning> Warnings => demo.Warnings;

        public ParseHealth Health => demo.Health;

        public DemoDescriptor Snapshot() => DemoDescriptor.From(demo);
    }
}
