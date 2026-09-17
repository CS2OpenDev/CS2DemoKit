#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the per-pawn digest stream on the committed sample demo. The fixtures were generated
///     by the boxed digest (<c>List&lt;(int, object?[])&gt;</c> rows) before the typed-column
///     rewrite, so they hold the typed digest to the exact rows and cells the boxed one emitted.
///     <para>
///         Section A hashes the rows of ONE sequential delta stream (what
///         <c>EntityChangeScanner.BuildDigest</c> produces on the fallback path): every emitted row in
///         emission order, every present cell in column order, value formatted invariantly. Two arms,
///         because the generic <see cref="GenericPerPlayerFieldProvider" /> gate has arms
///         (PositiveOnly, UnseenAsDefault, the two handle hops) that the hand-written classes never
///         exercise: <see cref="PerPlayerEntityValueProviderRegistry.CreateDefault" /> and
///         <see cref="BuiltinProviderSpecs.CreateGenericPerPlayerProviders" />.
///     </para>
///     <para>
///         Section B hashes the pipelined producer's stream as a FOLD: the cells whose folded value
///         changed after each frame. Rows there are chunk-dependent by construction (a worker
///         re-emits every live cell on its chunk's first frame, and the chunk boundaries follow the
///         demo's full-packet cadence), so Section B renders no row count at all: not in the totals and
///         not in the checkpoints. What it renders, the folded cell count and hash, is
///         chunk-invariant, which is exactly the property nobody may re-pin over: a Section B
///         mismatch is a real equivalence bug, not a layout difference.
///     </para>
///     <para>
///         The fixture is a chained 64-bit FNV-1a over every cell, checkpointed every 512 frames so a
///         mismatch localises to a frame window, plus the row and cell totals. Regenerate only after
///         an intended behaviour change, with <c>CS2DEMOKIT_UPDATE_PER_PAWN_FOLD=1</c>. NaN and
///         negative zero do not occur in the sample; <c>PerPawnDeltaStateTests</c> pins those.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PerPawnFoldGoldenTests
{
    private const string UpdateVariable = "CS2DEMOKIT_UPDATE_PER_PAWN_FOLD";
    private const int CheckpointStride = 512;
    private const int SampleFrameCount = 19238;
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    [Test]
    public async Task SequentialDeltaRows_DefaultRegistry_MatchGolden()
    {
        await RunSectionA("per-pawn-fold.default.golden.txt",
            () => PerPlayerEntityValueProviderRegistry.CreateDefault().All.ToList());
    }

    [Test]
    public async Task SequentialDeltaRows_GenericRegistry_MatchGolden()
    {
        await RunSectionA("per-pawn-fold.generic.golden.txt",
            BuiltinProviderSpecs.CreateGenericPerPlayerProviders);
    }

    [Test]
    public async Task PipelinedFold_DefaultRegistry_MatchesGolden()
    {
        await RunSectionB("per-pawn-fold.parallel.golden.txt",
            () => PerPlayerEntityValueProviderRegistry.CreateDefault().All.Select(Clone).ToList());
    }

    private static async Task RunSectionA(string fixtureName, Func<List<IPerPlayerEntityValueProvider>> providers)
    {
        ParsedDemo demo = RequireSample();
        List<IPerPlayerEntityValueProvider> perPlayer = providers();
        Hasher hasher = new();

        EntityStateLayer layer = new(demo.Frames);
        PerPawnDeltaState delta = new(DigestColumnLayout.For(perPlayer));
        for (int n = 0; n < demo.Frames.Count; n++)
        {
            layer.SeekToTick(demo.Frames[n].ServerTick);
            EntityFrameDigest d = EntityDigestExtractor.Build(layer, delta, [], false);
            hasher.BeginFrame(n);
            PerPawnColumns rows = d.PerPawn;
            for (int r = 0; r < rows.Count; r++)
            {
                hasher.Row();
                int slot = rows.SlotAt(r);
                for (int p = 0; p < rows.Layout.Count; p++)
                {
                    if (rows.IsPresent(r, p))
                    {
                        hasher.Cell(slot, p, rows.GetBoxed(r, p)!);
                    }
                }
            }
        }

        await Compare(fixtureName, hasher, demo.Frames.Count);
    }

    private static async Task RunSectionB(string fixtureName, Func<List<IPerPlayerEntityValueProvider>> factory)
    {
        ParsedDemo demo = RequireSample();
        EntityFrameDigest[] digests = PipelinedDigests.Produce(demo.AsFrameSource(), factory, () => [], false);

        Hasher hasher = new(countRows: false);
        Dictionary<(int Provider, int Slot), object> snapshot = [];
        for (int n = 0; n < digests.Length; n++)
        {
            hasher.BeginFrame(n);
            PerPawnColumns rows = digests[n].PerPawn;
            for (int r = 0; r < rows.Count; r++)
            {
                int slot = rows.SlotAt(r);
                for (int p = 0; p < rows.Layout.Count; p++)
                {
                    if (!rows.IsPresent(r, p))
                    {
                        continue;
                    }

                    object cell = rows.GetBoxed(r, p)!;
                    if (snapshot.TryGetValue((p, slot), out object? held) && Equals(held, cell))
                    {
                        continue;
                    }

                    snapshot[(p, slot)] = cell;
                    hasher.Cell(slot, p, cell);
                }
            }
        }

        await Compare(fixtureName, hasher, digests.Length);
    }

    private static ParsedDemo RequireSample()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        if (demo.Frames.Count != SampleFrameCount)
        {
            throw new InvalidOperationException(
                $"the fixture describes exactly the {SampleFrameCount}-frame trim of the sample demo; "
                + $"this file has {demo.Frames.Count}");
        }

        return demo;
    }

    private static async Task Compare(string fixtureName, Hasher hasher, int frames)
    {
        string root = RepoRoot() ?? throw new SkipTestException("repo root not found");
        string goldenPath = Path.Combine(root, "tests", "fixtures", "sample-de_nuke", fixtureName);
        string rendered = hasher.Render(frames);

        // Regenerate mode writes the fixture first, so the existence check below is reached with a
        // file present and only ever judges a verify run.
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            await File.WriteAllTextAsync(goldenPath, rendered);
            Console.WriteLine($"wrote {goldenPath}");
        }

        // The fixture IS the assertion on this stream, so a missing one fails rather than skips:
        // a skip would let deleting the file stand in for passing it.
        if (!File.Exists(goldenPath))
        {
            Assert.Fail($"no fixture at {goldenPath}, and it is the only gate on this stream. Restore the "
                        + $"committed file, or regenerate it with {UpdateVariable}=1 and commit the result.");
        }

        string expected = await File.ReadAllTextAsync(goldenPath);
        if (expected != rendered)
        {
            Assert.Fail($"per-pawn fold diverged from {fixtureName}:{Environment.NewLine}{FirstDifference(expected, rendered)}");
        }

        // A run that hashed no cells renders a short, stable text that a fixture re-pinned from the
        // same nothing would match, so the total is asserted on its own rather than left to the
        // comparison above.
        await Assert.That(hasher.Cells).IsGreaterThan(0L)
            .Because("every round of the sample changes per-pawn state, so the stream cannot be empty");
    }

    private static string FirstDifference(string expected, string actual)
    {
        string[] e = expected.Split('\n');
        string[] a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            string el = i < e.Length ? e[i] : "<missing>";
            string al = i < a.Length ? a[i] : "<missing>";
            if (el != al)
            {
                return $"line {i + 1}: expected '{el}' actual '{al}'";
            }
        }

        return "no textual difference found";
    }

    private static IPerPlayerEntityValueProvider Clone(IPerPlayerEntityValueProvider p) =>
        p is IWorkerCloneable<IPerPlayerEntityValueProvider> cloneable
            ? cloneable.CloneForWorker()
            : (IPerPlayerEntityValueProvider)Activator.CreateInstance(p.GetType())!;

    private static string? RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "CS2DemoKit.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    ///     Formats a boxed cell by its runtime type, which is the column's declared type. The
    ///     fixtures were pinned with this same formatting over the boxed digest's cells, whose
    ///     runtime type was the provider's declared type on every shipped provider.
    /// </summary>
    private static string FormatCell(object cell) => cell switch
    {
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        string s => s,
        _ => throw new InvalidOperationException($"cell type {cell.GetType().Name} is outside the digest's closed type set")
    };

    // countRows is false for the fold arm, whose row count is chunk-dependent and must not be pinned.
    private sealed class Hasher(bool countRows = true)
    {
        private readonly StringBuilder _checkpoints = new();
        private ulong _hash = FnvOffset;
        private long _cells;
        private long _rows;

        /// <summary>Cells mixed into the hash; zero means the stream carried nothing to pin.</summary>
        public long Cells => _cells;

        public void BeginFrame(int frame)
        {
            if (frame > 0 && frame % CheckpointStride == 0)
            {
                string rows = countRows ? $"rows {_rows} " : "";
                _checkpoints.Append(CultureInfo.InvariantCulture, $"frame {frame} {rows}cells {_cells} hash {_hash:x16}\n");
            }
        }

        public void Row() => _rows++;

        public void Cell(int slot, int provider, object cell)
        {
            _cells++;
            Mix(slot);
            Mix(provider);
            Mix(FormatCell(cell));
        }

        public string Render(int frames)
        {
            string rows = countRows ? $"rows {_rows}\n" : "";
            return $"frames {frames}\n{rows}cells {_cells}\nhash {_hash:x16}\n{_checkpoints}";
        }

        private void Mix(int value)
        {
            for (int i = 0; i < 4; i++)
            {
                _hash = (_hash ^ (byte)(value >> (8 * i))) * FnvPrime;
            }
        }

        private void Mix(string text)
        {
            foreach (char c in text)
            {
                _hash = (_hash ^ (byte)c) * FnvPrime;
                _hash = (_hash ^ (byte)(c >> 8)) * FnvPrime;
            }

            _hash = (_hash ^ 0xFF) * FnvPrime;
        }
    }
}
