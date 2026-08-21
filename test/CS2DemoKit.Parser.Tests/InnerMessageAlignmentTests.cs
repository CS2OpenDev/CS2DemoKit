#region

using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using Google.Protobuf;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Regression coverage for <see cref="DownstreamUtilities.ExtractInnerMessageBytesAligned" /> — the
///     type-id-aware byte recovery that replaces the positional
///     <see cref="DownstreamUtilities.ExtractInnerMessageBytes" /> in the card/hex views.
///     <para>
///         Two demo-wide invariants:
///         <list type="number">
///             <item>
///                 <b>Clean-frame identity (also the type-id↔name bridge check):</b> on frames whose
///                 every bitstream message is a known inner message (no unknowns, no parse-failures),
///                 the aligned bytes must be byte-identical to the positional extraction. If the bridge
///                 (slice type id → <see cref="NetMessage.MessageTypeName" />) were broken, this diverges
///                 immediately.
///             </item>
///             <item>
///                 <b>Parse-equality oracle:</b> on frames that DO contain unknown messages, each known
///                 message's aligned bytes must re-parse to the already-decoded
///                 <see cref="NetMessage.Payload" />. The positional extraction fails this for messages
///                 after an unknown — proving the fix corrects a real defect, not a no-op.
///             </item>
///         </list>
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class InnerMessageAlignmentTests
{
    private const int CleanFrameCap = 200;
    private const int UnknownFrameCap = 300;

    [Test]
    public async Task AlignedExtraction_MatchesPositional_OnCleanFrames()
    {
        (ParsedDemo demo, byte[] bytes, _) = ParseWithUnknownCensus();

        int cleanFramesChecked = 0;
        for (int f = 0; f < demo.Frames.Count && cleanFramesChecked < CleanFrameCap; f++)
        {
            DemoFrame frame = demo.Frames[f];
            if (frame.Command is not ("DEM_Packet" or "DEM_SignonPacket") || frame.InnerMessages.Count == 0)
            {
                continue;
            }

            byte[] payload = DownstreamUtilities.GetDecompressedPayload(frame, bytes);

            // "Clean" = every bitstream slice is a known inner message (no unknown/dropped messages),
            // so positional and aligned must agree exactly.
            if (DownstreamUtilities.ExtractInnerMessageSlices(frame, payload).Count != frame.InnerMessages.Count)
            {
                continue;
            }

            byte[]?[] positional = DownstreamUtilities.ExtractInnerMessageBytes(frame, payload);
            byte[]?[] aligned = DownstreamUtilities.ExtractInnerMessageBytesAligned(frame, payload);

            await Assert.That(aligned.Length).IsEqualTo(positional.Length);
            for (int i = 0; i < positional.Length; i++)
            {
                await Assert.That(BytesEqual(positional[i], aligned[i])).IsTrue();
            }

            cleanFramesChecked++;
        }

        Console.WriteLine($"clean frames checked (aligned == positional): {cleanFramesChecked}");
        await Assert.That(cleanFramesChecked).IsGreaterThan(0);
    }

    [Test]
    public async Task AlignedExtraction_RoundTripsKnownMessages_InUnknownContainingFrames()
    {
        (ParsedDemo demo, byte[] bytes, HashSet<int> unknownFrames) = ParseWithUnknownCensus();
        if (unknownFrames.Count == 0)
        {
            throw new SkipTestException("Reference demo contains no unknown net-messages.");
        }

        int alignedFailures = 0;
        int positionalFailures = 0;
        int knownChecked = 0;
        int framesChecked = 0;

        foreach (int f in unknownFrames.Order())
        {
            if (framesChecked >= UnknownFrameCap)
            {
                break;
            }

            DemoFrame frame = demo.Frames[f];
            if (frame.Command is not ("DEM_Packet" or "DEM_SignonPacket" or "DEM_FullPacket"))
            {
                continue;
            }

            byte[] payload = DownstreamUtilities.GetDecompressedPayload(frame, bytes);
            byte[]?[] aligned = DownstreamUtilities.ExtractInnerMessageBytesAligned(frame, payload);
            byte[]?[] positional = DownstreamUtilities.ExtractInnerMessageBytes(frame, payload);

            for (int i = 0; i < frame.InnerMessages.Count; i++)
            {
                NetMessage msg = frame.InnerMessages[i];
                // GameEventMessage is an enriched wrapper the parser substitutes for the raw
                // CMsgSource1LegacyGameEvent — its wire bytes do not round-trip to Payload, so it is not
                // a valid oracle. Every other known message's Payload IS the proto parsed from its bytes.
                if (msg is GameEventMessage)
                {
                    continue;
                }

                MessageParser parser = msg.Payload.Descriptor.Parser;

                if (i < aligned.Length && aligned[i] is { } ab)
                {
                    knownChecked++;
                    if (!RoundTrips(parser, ab, msg.Payload))
                    {
                        alignedFailures++;
                    }
                }

                if (i < positional.Length && positional[i] is { } pb && !RoundTrips(parser, pb, msg.Payload))
                {
                    positionalFailures++;
                }
            }

            framesChecked++;
        }

        Console.WriteLine($"unknown frames checked: {framesChecked} of {unknownFrames.Count}; known msgs checked: {knownChecked}");
        Console.WriteLine($"aligned round-trip failures: {alignedFailures}; positional round-trip failures: {positionalFailures}");

        await Assert.That(knownChecked).IsGreaterThan(0);
        // The fix: every known message's aligned bytes re-parse to the decoded Payload.
        await Assert.That(alignedFailures).IsEqualTo(0);
        // The bug it fixes: the old positional extraction was wrong for ≥1 known message after an unknown.
        await Assert.That(positionalFailures).IsGreaterThan(0);
    }

    [Test]
    public async Task AlignedExtraction_RoundTripsKnownMessages_InFullPacketFrames()
    {
        // DEM_FullPacket exercises the branch Test A/B don't deterministically cover: message 0 is the
        // CDemoStringTables blob (result[0]) and the type-id alignment starts at index 1. We oracle the
        // aligned inner messages (i >= 1), which works whether or not the frame also carries unknowns.
        (ParsedDemo demo, byte[] bytes, _) = ParseWithUnknownCensus();

        int alignedFailures = 0;
        int knownChecked = 0;
        int fullPacketFrames = 0;

        for (int f = 0; f < demo.Frames.Count && fullPacketFrames < 50; f++)
        {
            DemoFrame frame = demo.Frames[f];
            if (frame.Command != "DEM_FullPacket" || frame.InnerMessages.Count == 0)
            {
                continue;
            }

            byte[] payload = DownstreamUtilities.GetDecompressedPayload(frame, bytes);
            byte[]?[] aligned = DownstreamUtilities.ExtractInnerMessageBytesAligned(frame, payload);

            for (int i = 1; i < frame.InnerMessages.Count; i++)
            {
                NetMessage msg = frame.InnerMessages[i];
                if (msg is GameEventMessage)
                {
                    continue;
                }

                if (i < aligned.Length && aligned[i] is { } ab)
                {
                    knownChecked++;
                    if (!RoundTrips(msg.Payload.Descriptor.Parser, ab, msg.Payload))
                    {
                        alignedFailures++;
                    }
                }
            }

            fullPacketFrames++;
        }

        Console.WriteLine($"DEM_FullPacket frames checked: {fullPacketFrames}; known msgs checked: {knownChecked}; aligned failures: {alignedFailures}");
        if (fullPacketFrames == 0)
        {
            throw new SkipTestException("Reference demo contains no DEM_FullPacket frames.");
        }

        await Assert.That(knownChecked).IsGreaterThan(0);
        await Assert.That(alignedFailures).IsEqualTo(0);
    }

    private static bool RoundTrips(MessageParser parser, byte[] bytes, IMessage expected)
    {
        try
        {
            // A DeferredMessage never .Equals the real parsed message, so compare against its
            // materialized form. The parser no longer produces these (svc_UserCmds goes to the
            // user-command store), but a hand-built frame still can.
            IMessage target = expected is DeferredMessage d ? d.Materialize() : expected;
            return parser.ParseFrom(bytes).Equals(target);
        }
        catch
        {
            return false;
        }
    }

    private static bool BytesEqual(byte[]? a, byte[]? b) =>
        a is null && b is null || a is not null && b is not null && a.AsSpan().SequenceEqual(b);

    private static (ParsedDemo Demo, byte[] Bytes, HashSet<int> UnknownFrames) ParseWithUnknownCensus()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = File.ReadAllBytes(path);

        HashSet<int> unknownFrames = new();

        void Handler(UnknownMessageInfo info)
        {
            lock (unknownFrames)
            {
                unknownFrames.Add(info.FrameNumber);
            }
        }

        DemoParser.OnUnknownMessageType += Handler;
        ParsedDemo demo;
        try
        {
            demo = DemoParser.Parse(bytes.AsMemory());
        }
        finally
        {
            DemoParser.OnUnknownMessageType -= Handler;
        }

        return (demo, bytes, unknownFrames);
    }
}
