#region

using System.Diagnostics;
using Google.Protobuf;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.Models;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     <see cref="UserCmdReconstructor" /> over every demo this machine has: the sample,
///     <c>&lt;repo-root&gt;/demos/**</c>, <c>DEMO_PATH</c>, and each entry of
///     <c>CS2DEMOKIT_USERCMD_DEMOS</c> (a list of <c>.dem</c> files or directories, separated by
///     <see cref="Path.PathSeparator" />; a directory contributes its top-level <c>*.dem</c>). Demos are
///     read in place. Explicit because each demo is parsed and walked in full.
///     <para>
///         Per demo it proves that every command rebuilds (no failures, missing baselines, out-of-order
///         commands or checkpoint mismatches, and every rebuilt <c>base.client_tick</c> equal to the
///         outer one), that the forward reader and the retained parse rebuild the same commands, and
///         that a reconstructor started cold at the middle <c>DEM_FullPacket</c> reproduces every
///         later command exactly, which is what a seek relies on.
///     </para>
/// </summary>
[Explicit]
[NotInParallel]
[Category("Integration")]
public class UserCmdReconstructionCorpusTests
{
    public static IEnumerable<string> Demos()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> all = [.. DemoReaderCorpusTests.Demos()];

        string? fromEnv = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            all.Add(fromEnv);
        }

        string? list = Environment.GetEnvironmentVariable("CS2DEMOKIT_USERCMD_DEMOS");
        foreach (string entry in (list ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(entry))
            {
                all.AddRange(Directory.EnumerateFiles(entry, "*.dem").OrderBy(p => p, StringComparer.Ordinal));
            }
            else if (File.Exists(entry))
            {
                all.Add(entry);
            }
        }

        return all.Where(p => seen.Add(Path.GetFullPath(p)));
    }

    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task EveryCommandRebuilds_OnBothPaths_AndFromASeek(string path)
    {
        if (!File.Exists(path))
        {
            throw new SkipTestException($"Demo '{path}' is not present on this machine.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo demo = DemoParser.Parse(bytes.AsMemory());

        Stopwatch extract = Stopwatch.StartNew();
        UserCmdReconstructor extractCmds = new();
        List<SubTickEvent> events = SubTickExtractor.Extract(demo.Frames, extractCmds);
        extract.Stop();

        UserCmdReconstructionStats s = extractCmds.Stats;
        if (s.Full + s.Delta == 0)
        {
            throw new SkipTestException($"{Path.GetFileName(path)} carries no player commands");
        }

        // The seek point: the first full packet at or after the middle frame.
        int seekFrame = -1;
        for (int i = demo.Frames.Count / 2; i < demo.Frames.Count; i++)
        {
            if (demo.Frames[i].CommandKind == EDemoCommands.DemFullPacket)
            {
                seekFrame = i;
                break;
            }
        }

        // Three reconstructors in lockstep: retained frames, forward frames, and forward frames from
        // the seek point on with nothing carried over.
        UserCmdReconstructor retained = new(), forward = new(), seek = new();
        long forwardDiffers = 0, seekCompared = 0, seekDiffers = 0;
        int frameIndex = 0;
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory()))
        {
            foreach (DemoFrame frame in reader.ReadFrames())
            {
                IReadOnlyList<ReconstructedUserCmd> f = forward.AdvanceOneFrame(frame);
                IReadOnlyList<ReconstructedUserCmd> r = retained.AdvanceOneFrame(demo.Frames[frameIndex]);
                forwardDiffers += Differences(f, r, bytesToo: false);

                if (seekFrame >= 0 && frameIndex >= seekFrame)
                {
                    IReadOnlyList<ReconstructedUserCmd> k = seek.AdvanceOneFrame(frame);
                    seekCompared += f.Count;
                    seekDiffers += Differences(f, k, bytesToo: true);
                }

                frameIndex++;
            }
        }

        double deltaShare = (double)s.Delta / (s.Full + s.Delta);
        Console.WriteLine($"{Path.GetFileName(path)}: full={s.Full} delta={s.Delta} ({deltaShare:P2} delta) "
                          + $"primed={s.CheckpointPrimed} dup={s.CheckpointDuplicates} unknownFields={s.UnknownFieldsSkipped} "
                          + $"events={events.Count} extract={extract.ElapsedMilliseconds} ms; "
                          + $"seek from frame {seekFrame}: {seekCompared} commands compared, {seekDiffers} differ, "
                          + $"seek missing baselines={seek.Stats.MissingBaseline}");

        await Assert.That(frameIndex).IsEqualTo(demo.Frames.Count);
        await Assert.That(s.DecodeFailed).IsEqualTo(0L);
        await Assert.That(s.MissingBaseline).IsEqualTo(0L);
        await Assert.That(s.OutOfOrder).IsEqualTo(0L);
        await Assert.That(s.CheckpointMismatches).IsEqualTo(0L);
        await Assert.That(s.ClientTickMismatches).IsEqualTo(0L);

        await Assert.That(forward.Stats).IsEqualTo(retained.Stats);
        await Assert.That(retained.Stats).IsEqualTo(s);
        await Assert.That(forwardDiffers).IsEqualTo(0L);

        if (seekFrame >= 0)
        {
            await Assert.That(seek.Stats.MissingBaseline).IsEqualTo(0L);
            await Assert.That(seekDiffers).IsEqualTo(0L);
            await Assert.That(seekCompared).IsGreaterThan(0L);
        }
    }

    /// <summary>
    ///     Pairs two frames' rebuilt commands in order and counts the pairs that differ, plus any
    ///     count mismatch.
    /// </summary>
    private static long Differences(IReadOnlyList<ReconstructedUserCmd> a, IReadOnlyList<ReconstructedUserCmd> b,
        bool bytesToo)
    {
        long differ = Math.Abs(a.Count - b.Count);
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            ReconstructedUserCmd x = a[i], y = b[i];
            bool same = x.PlayerSlot == y.PlayerSlot && x.CmdNumber == y.CmdNumber && x.FromDelta == y.FromDelta
                        && x.Command.Equals(y.Command);
            if (same && bytesToo)
            {
                same = x.Command.ToByteString().Equals(y.Command.ToByteString());
            }

            if (!same)
            {
                differ++;
            }
        }

        return differ;
    }
}
