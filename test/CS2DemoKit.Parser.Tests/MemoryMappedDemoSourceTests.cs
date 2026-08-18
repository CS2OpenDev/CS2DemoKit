#region

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Covers <see cref="MemoryMappedDemoSource" />: byte-identical parse output versus the
///     <c>byte[]</c> entry point, and the ownership/lifetime contract (safe to dispose the instant
///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile?)" /> returns; a read after
///     disposal surfaces as <see cref="ObjectDisposedException" /> rather than a process-killing
///     access violation).
///     <para>
///         <b>[NotInParallel]</b> — these parse full-size demos; two concurrent parses OOM a 16 GB
///         machine. The equivalence test deliberately parses twice <i>sequentially</i> and reduces
///         each parse to a small digest before the next, so only one <see cref="ParsedDemo" /> is
///         live at a time.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class MemoryMappedDemoSourceTests
{
    /// <summary>
    ///     The correctness bar for the memory-mapped buffer: parsing the same file through a mapping
    ///     must produce output identical to parsing it from a <c>byte[]</c>. The digest covers every
    ///     frame's command/tick/offsets/compression flag and inner-message type names, plus the
    ///     demo-level counts and header metadata — i.e. everything the parse can get wrong if a
    ///     slice lands one byte off.
    /// </summary>
    [Test]
    public async Task MemoryMappedParse_ProducesIdenticalOutputToByteArrayParse()
    {
        string path = DemoTestHelper.RequireDemo();

        // Sequential, one ParsedDemo alive at a time — each parse is reduced to a hex digest and the
        // demo released before the next parse starts.
        string mappedDigest = DigestOfMappedParse(path);
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();

        string arrayDigest = DigestOfArrayParse(path);

        await Assert.That(mappedDigest).IsEqualTo(arrayDigest);
    }

    /// <summary>
    ///     Pins the load-bearing half of the ownership contract: the mapping may be released the
    ///     moment <c>Parse</c> returns because nothing on the parse output points into it.
    ///     <see cref="MemoryMappedDemoSource.ParseFile" /> disposes internally, so every access here
    ///     happens against an already-unmapped file. If a <see cref="DemoFrame" /> or
    ///     <see cref="NetMessage" /> ever starts retaining a slice, this test stops being a pass and
    ///     starts being an access violation — which is exactly the signal we want.
    /// </summary>
    [Test]
    public async Task ParseFile_OutputIsUsableAfterTheMappingIsDisposed()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo demo = MemoryMappedDemoSource.ParseFile(path);

        // The mapping is gone at this point. Touch everything that could plausibly have kept a
        // reference into it: frame metadata, inner-message protobufs, decoded events, schema.
        long touched = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            touched += frame.RawStart + frame.RawLength + frame.HeaderLength;
            foreach (NetMessage msg in frame.InnerMessages)
            {
                touched += msg.MessageTypeName.Length;
            }
        }

        await Assert.That(demo.Frames.Count).IsGreaterThan(0);
        await Assert.That(touched).IsGreaterThan(0);
        await Assert.That(demo.MapName).IsNotEmpty();
        await Assert.That(demo.AllGameEvents.Count).IsGreaterThan(0);
    }

    /// <summary>
    ///     The <c>ReadOnlySpan</c> overload of <see cref="DownstreamUtilities.GetDecompressedPayload(DemoFrame,byte[])" />
    ///     — the API a mapped-source consumer needs for hex/raw views — must agree with the
    ///     <c>byte[]</c> overload for every frame, including the bounds of the last frame.
    /// </summary>
    [Test]
    public async Task GetDecompressedPayload_SpanOverloadMatchesArrayOverload()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo demo = DemoParser.Parse(bytes.AsMemory());

        using MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);

        int compared = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            if (frame.PayloadLength == 0)
            {
                continue;
            }

            byte[] fromArray = DownstreamUtilities.GetDecompressedPayload(frame, bytes);
            byte[] fromMap = DownstreamUtilities.GetDecompressedPayload(frame, src.Memory.Span);
            if (!fromArray.AsSpan().SequenceEqual(fromMap))
            {
                throw new InvalidOperationException(
                    $"Payload mismatch at frame {frame.FrameNumber} ({frame.Command}), " +
                    $"start={frame.PayloadStart} len={frame.PayloadLength}.");
            }

            compared++;
        }

        await Assert.That(compared).IsGreaterThan(0);
    }

    /// <summary>
    ///     The mapped length must equal the length of the file's actual CONTENT, with no truncation or
    ///     padding.
    ///     <para>
    ///         The length is read from the opened stream, NOT from <c>FileInfo.Length</c>, and this
    ///         test asserts the same way on purpose: when the demo is reached through a symlink (the
    ///         worktree tooling symlinks <c>demos/benchmarks/*.dem</c>) <c>FileInfo.Length</c> reports
    ///         the link's own size — 98 bytes — so an implementation that trusted it would map a
    ///         98-byte window of a 180 MB demo and parse to zero frames without erroring. That is
    ///         exactly the bug this assertion caught.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Open_LengthMatchesFileLength()
    {
        string path = DemoTestHelper.RequireDemo();
        using MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);

        await using FileStream fs = File.OpenRead(path);
        await Assert.That((long)src.Length).IsEqualTo(fs.Length);
        await Assert.That(src.Memory.Length).IsEqualTo(src.Length);
    }

    /// <summary>
    ///     Use-after-unmap on the owning thread must surface as a catchable
    ///     <see cref="ObjectDisposedException" />, not an access violation. This is the diagnostic
    ///     guard, not a race guard — a cross-thread dispose during a read is still undefined
    ///     behaviour (documented on the type).
    /// </summary>
    [Test]
    public async Task Memory_ReadAfterDispose_ThrowsObjectDisposedException()
    {
        string path = DemoTestHelper.RequireDemo();
        MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);
        ReadOnlyMemory<byte> escaped = src.Memory;

        // Sanity: the slice is readable while the source is alive.
        await Assert.That(escaped.Span[0]).IsEqualTo((byte)'P');

        src.Dispose();

        await Assert.That(src.IsDisposed).IsTrue();
        await Assert.That(ThrowsObjectDisposed(() => _ = escaped.Span[0])).IsTrue();
        await Assert.That(ThrowsObjectDisposed(() => _ = escaped.Slice(16, 32).Span[0])).IsTrue();
    }

    /// <summary>
    ///     Runs <paramref name="read" /> and reports whether it threw <see cref="ObjectDisposedException" />.
    ///     Written as a helper rather than an assertion lambda because a <c>Span</c> access cannot appear
    ///     in an expression-tree/async assertion delegate.
    /// </summary>
    private static bool ThrowsObjectDisposed(Action read)
    {
        try
        {
            read();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>Double-dispose must be a no-op, not a double-release of the view handle.</summary>
    [Test]
    public async Task Dispose_IsIdempotent()
    {
        string path = DemoTestHelper.RequireDemo();
        MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);

        src.Dispose();
        src.Dispose();

        await Assert.That(src.IsDisposed).IsTrue();
    }

    /// <summary>
    ///     The finalizer backstop must actually unmap a source that was dropped without
    ///     <see cref="MemoryMappedDemoSource.Dispose" />.
    ///     <para>
    ///         Why this test is not paranoia: the constructor's <c>AcquirePointer</c> takes an extra ref
    ///         on the view's <c>SafeMemoryMappedViewHandle</c>. Without our own finalizer that
    ///         ref is never dropped — the accessor's SafeHandle finalizer takes the count 2 → 1, never
    ///         to 0, so <c>munmap</c> never runs and the mapping leaks for the process lifetime.
    ///     </para>
    ///     <para>
    ///         The assertion deliberately uses the internal <c>FinalizerReleaseCount</c> rather than
    ///         <c>SafeHandle.IsClosed</c>: <c>IsClosed</c> flips as soon as a dispose is
    ///         *requested*, so it is true in the leaking case too and would make this test pass
    ///         against the bug. The counter increments only on the finalizer's release path, so it
    ///         fails (stays 0) if the finalizer is removed. The weak reference additionally proves the
    ///         source itself became collectable.
    ///     </para>
    ///     Small temp file, not the demo — this must not depend on a large mapping.
    /// </summary>
    [Test]
    public async Task DroppedWithoutDispose_FinalizerUnmapsTheView()
    {
        string path = Path.Combine(Path.GetTempPath(), $"finalizer-demo-{Guid.NewGuid():N}.dem");
        await File.WriteAllBytesAsync(path, new byte[64 * 1024]);
        try
        {
            long before = MemoryMappedDemoSource.FinalizerReleaseCount;
            WeakReference weak = OpenAndAbandon(path);

            for (int i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
            }

            await Assert.That(weak.IsAlive).IsFalse()
                .Because("nothing must root an abandoned source, or the finalizer can never run");
            await Assert.That(MemoryMappedDemoSource.FinalizerReleaseCount).IsGreaterThan(before)
                .Because("the finalizer must run the unmap path for a source dropped without Dispose");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     Opens a source, touches it, and drops every reference — in a non-inlined frame so no live
    ///     local (or a JIT-extended lifetime in the caller) keeps it rooted past the collection below.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndAbandon(string path)
    {
        MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);
        // Materialise a span so the mapping is genuinely faulted in, exactly as a real use would.
        _ = src.Memory.Span[0];
        return new WeakReference(src);
    }

    /// <summary>A missing path fails fast with <see cref="FileNotFoundException" />, never a null map.</summary>
    [Test]
    public async Task Open_MissingFile_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"no-such-demo-{Guid.NewGuid():N}.dem");
        bool threw = false;
        try
        {
            MemoryMappedDemoSource.Open(missing).Dispose();
        }
        catch (FileNotFoundException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    /// <summary>
    ///     A zero-length file cannot be mapped (the OS rejects a zero-size view), so it must be
    ///     rejected with a clear <see cref="InvalidDataException" /> rather than a platform error.
    /// </summary>
    [Test]
    public async Task Open_EmptyFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), $"empty-demo-{Guid.NewGuid():N}.dem");
        await File.WriteAllBytesAsync(path, []);
        bool threw = false;
        try
        {
            MemoryMappedDemoSource.Open(path).Dispose();
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        finally
        {
            File.Delete(path);
        }

        await Assert.That(threw).IsTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string DigestOfMappedParse(string path)
    {
        using MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);
        return Digest(DemoParser.Parse(src.Memory));
    }

    private static string DigestOfArrayParse(string path) =>
        Digest(DemoParser.Parse(File.ReadAllBytes(path).AsMemory()));

    /// <summary>
    ///     Reduces a whole <see cref="ParsedDemo" /> to a fixed-size hash so two parses can be
    ///     compared without holding both in memory.
    /// </summary>
    private static string Digest(ParsedDemo demo)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        StringBuilder sb = new(256);

        void Feed()
        {
            hash.AppendData(Encoding.UTF8.GetBytes(sb.ToString()));
            sb.Clear();
        }

        sb.Append(demo.MapName).Append('|').Append(demo.ServerName).Append('|').Append(demo.ClientName)
            .Append('|').Append(demo.BuildNumber).Append('|').Append(demo.TickCount)
            .Append('|').Append(demo.ServerStartTick).Append('|').Append(demo.DemoVersionGuid)
            .Append('|').Append(demo.Frames.Count).Append('|').Append(demo.AllGameEvents.Count)
            .Append('|').Append(demo.Players.Count).Append('\n');
        Feed();

        foreach (DemoFrame frame in demo.Frames)
        {
            sb.Append(frame.FrameNumber).Append(',').Append(frame.Command).Append(',')
                .Append(frame.ServerTick).Append(',').Append(frame.RawStart).Append(',')
                .Append(frame.RawLength).Append(',').Append(frame.HeaderLength).Append(',')
                .Append(frame.IsCompressed ? '1' : '0').Append(',').Append(frame.InnerMessages.Count);
            foreach (NetMessage msg in frame.InnerMessages)
            {
                sb.Append(';').Append(msg.MessageTypeName);
            }

            sb.Append('\n');
            if (sb.Length > 8192)
            {
                Feed();
            }
        }

        foreach (GameEvents.GameEvent ev in demo.AllGameEvents)
        {
            sb.Append(ev.GetType().Name).Append(',').Append(ev.GameTick).Append(',')
                .Append(ev.ServerTick).Append('\n');
            if (sb.Length > 8192)
            {
                Feed();
            }
        }

        foreach ((int slot, PlayerInfo info) in demo.Players.OrderBy(p => p.Key))
        {
            sb.Append(slot).Append(',').Append(info.Name).Append(',').Append(info.SteamId64)
                .Append(',').Append(info.Team).Append('\n');
        }

        Feed();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
