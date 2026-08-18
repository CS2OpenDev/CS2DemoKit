#region

using System.Buffers;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

#endregion

namespace CS2DemoKit.Parser;

/// <summary>
///     A read-only memory-mapped view of a .dem file on disk, exposed as a
///     <see cref="ReadOnlyMemory{T}" /> so it can be handed straight to
///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile?)" /> without ever
///     materialising the file as a managed <c>byte[]</c>.
///     <para>
///         <b>Why:</b> a 180 MB demo read with <c>File.ReadAllBytes</c> is a single
///         ~166 MB array on the Large Object Heap for the whole life of the load. Mapping
///         the file instead puts those bytes in file-backed OS pages: they never enter the GC
///         heap, are not copied, are not compacted, and the kernel can evict and re-fault them
///         under pressure.
///     </para>
///     <para>
///         <b>OWNERSHIP CONTRACT — read this before using the type.</b>
///         <list type="number">
///             <item>
///                 <b>The caller owns the source and MUST dispose it.</b> Nothing else takes
///                 ownership — in particular <see cref="ParsedDemo" /> does <b>not</b>, and is
///                 deliberately not <see cref="IDisposable" />. Use
///                 <c>using MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);</c>.
///                 There is a finalizer backstop so a missed dispose is a delay rather than a
///                 permanent leak (see the finalizer's remarks for why that is load-bearing), but it
///                 is a backstop, not a licence: until it runs the file stays mapped.
///             </item>
///             <item>
///                 <b>It is safe to dispose the instant <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile?)" />
///                 returns.</b> No part of the parse output points into this memory: a
///                 <see cref="DemoFrame" /> stores only integer offsets
///                 (<see cref="DemoFrame.RawStart" />, <see cref="DemoFrame.RawLength" />,
///                 <see cref="DemoFrame.HeaderLength" />) and protobuf <c>IMessage</c>s whose bytes
///                 Google.Protobuf already copied out of the input during <c>ParseFrom</c>.
///                 <see cref="ParsedDemo" /> holds no buffer at all. The only structure that holds
///                 slices is the parser's internal frame-descriptor list, which dies with the
///                 <c>Parse</c> call.
///             </item>
///             <item>
///                 <b>Do NOT let a <see cref="Memory" /> slice outlive the source.</b> Anything that
///                 re-reads the file <i>after</i> the parse — notably
///                 <see cref="DownstreamUtilities.GetDecompressedPayload(DemoFrame,ReadOnlySpan{byte})" />
///                 and hex/raw views — must run while the source is still alive, or be given its own
///                 copy. Reading a slice after disposal is a use-after-unmap.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Failure mode if you get it wrong.</b> Reading unmapped memory is an access violation
///         that <b>cannot be caught</b> in .NET and kills the process. To make the common
///         single-threaded mistake diagnosable, every <see cref="Memory" /> span materialisation
///         goes through a disposed-check that throws <see cref="ObjectDisposedException" /> instead.
///         That check is <b>not</b> a race guard: a dispose that happens on another thread between
///         the check and the read is still an access violation. If a background reader can outlive
///         the owner, the owner must cancel and <i>await</i> it before disposing.
///     </para>
///     <para>
///         <b>ONLY MAP FILES THAT ARE NOT BEING WRITTEN. </b> This is a failure mode
///         <c>File.ReadAllBytes</c> does <i>not</i> have, and it is not defensible in code.
///         <see cref="Open" /> uses <see cref="FileShare.ReadWrite" /> so the open cannot fail
///         against a demo another process holds — but that also means another process may
///         <b>truncate</b> the file while we hold the mapping. Touching a page past the new
///         end-of-file raises SIGBUS/EXCEPTION_IN_PAGE_ERROR, which the CLR surfaces as a
///         <b>fatal, uncatchable</b> <see cref="AccessViolationException" />: the process dies, and
///         no <c>try</c>/<c>catch</c> around the read fires. Verified experimentally
///         (map → external <c>SetLength</c> smaller → read past the new length → "Fatal error.
///         System.AccessViolationException", surrounding catch never ran).
///         A file that only <i>grows</i> is safe: the view is a fixed length fixed at open.
///         <br />
///         Consequence for callers: use this type for <b>stable, complete files on local disk</b>
///         (benchmarks, an already-downloaded demo the user opened). Do <b>not</b> use it for a demo
///         that is still being recorded, downloaded, or copied by a truncate-and-rewrite copier —
///         those must stay on the <c>byte[]</c> entry point, which snapshots the bytes and is immune.
///     </para>
///     <para>
///         <b>Platform:</b> desktop/CLI only. Memory mapping is unavailable on the Browser/WASM
///         head — that head must keep using the <c>byte[]</c> entry point, which is unchanged.
///     </para>
///     <para><b>Thread-safety:</b> reads of <see cref="Memory" /> are safe from any number of
///     threads (the parser's pass 2 does exactly that). <see cref="Dispose" /> is not concurrent
///     with reads — see above.</para>
/// </summary>
/// <example>
///     <code>
/// using MemoryMappedDemoSource src = MemoryMappedDemoSource.Open(path);
/// ParsedDemo demo = DemoParser.Parse(src.Memory);
/// // src may be disposed here; `demo` does not reference it.
///     </code>
/// </example>
public sealed unsafe class MemoryMappedDemoSource : IDisposable
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly ViewMemoryManager _manager;
    private readonly MemoryMappedFile _map;
    private readonly SafeMemoryMappedViewHandle _viewHandle;
    private readonly ReadOnlyMemory<byte> _memory;
    private static long s_finalizerReleaseCount;
    private byte* _pointer;
    private bool _pointerAcquired;

    /// <summary>
    ///     How many instances have been unmapped by the <b>finalizer</b> (i.e. were dropped without
    ///     <see cref="Dispose" />) in this process. Test observability only — internal, surfaced to the
    ///     Parser test assembly via <c>InternalsVisibleTo</c>. It exists because the finalizer is the
    ///     difference between a delayed release and an unreclaimable leak, and "the finalizer really
    ///     ran the release path" is otherwise not observable from managed code:
    ///     <c>SafeHandle.IsClosed</c> flips as soon as <c>Dispose</c> is requested, even when a
    ///     stray <c>AcquirePointer</c> ref keeps the refcount above zero and the <c>munmap</c> never runs.
    /// </summary>
    internal static long FinalizerReleaseCount => Interlocked.Read(ref s_finalizerReleaseCount);

    private MemoryMappedDemoSource(MemoryMappedFile map, MemoryMappedViewAccessor accessor, int length)
    {
        _map = map;
        _accessor = accessor;
        _viewHandle = accessor.SafeMemoryMappedViewHandle;
        Length = length;

        // AcquirePointer ref-counts the handle so it cannot be finalised while we hold the pointer.
        byte* basePtr = null;
        _viewHandle.AcquirePointer(ref basePtr);
        if (basePtr is null)
        {
            _viewHandle.ReleasePointer();
            accessor.Dispose();
            map.Dispose();
            throw new IOException("Failed to acquire a pointer to the memory-mapped demo view.");
        }

        _pointerAcquired = true;
        // PointerOffset is 0 for a full-file view created at offset 0, but the runtime is allowed to
        // round the mapping down to an allocation-granularity boundary — always add it.
        _pointer = basePtr + accessor.PointerOffset;
        _manager = new ViewMemoryManager(this);
        // Materialised once: the Memory itself stays valid, while every .Span off it re-enters
        // ViewMemoryManager.GetSpan() and so keeps the disposed-check.
        _memory = _manager.Memory;
    }

    /// <summary>True once <see cref="Dispose" /> has run. Reading <see cref="Memory" />'s span afterwards throws.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Byte length of the mapped file.</summary>
    public int Length { get; }

    /// <summary>
    ///     The whole file as a zero-copy <see cref="ReadOnlyMemory{T}" /> over OS pages.
    ///     Valid only until <see cref="Dispose" /> — see the ownership contract on the type.
    /// </summary>
    public ReadOnlyMemory<byte> Memory => _memory;

    /// <summary>
    ///     Finalizer backstop for a caller who drops the instance without disposing.
    ///     <para>
    ///         <b>This is not optional and not belt-and-braces — without it a missed dispose is an
    ///         UNRECLAIMABLE leak.</b> The constructor calls
    ///         <c>SafeBuffer.AcquirePointer</c>, which takes an extra ref on the view's
    ///         <see cref="SafeMemoryMappedViewHandle" />. That ref is dropped only by
    ///         <see cref="Dispose" />. If the instance is dropped un-disposed, the accessor's own
    ///         SafeHandle finalizer takes the count 2 → 1, never 0, so <c>ReleaseHandle</c> (the
    ///         <c>munmap</c>) never runs and the view stays mapped for the rest of the process —
    ///         strictly worse than the <c>byte[]</c> it replaces, which is at least collectable.
    ///         Measured: 20 non-disposed opens of a 172 MB demo left ~6.8 GB of mapped-file regions
    ///         alive through three aggressive collections + <c>WaitForPendingFinalizers</c>.
    ///     </para>
    ///     <para>
    ///         Safe to run from a finalizer: everything it touches is a
    ///         <c>CriticalFinalizerObject</c> (SafeHandle
    ///         and its wrappers), which is finalized <i>after</i> ordinary finalizers, so the handles
    ///         are still valid here.
    ///     </para>
    ///     <para>
    ///         The finalizer cannot run while any <see cref="ReadOnlyMemory{T}" /> slice of this
    ///         source is reachable — a slice holds the <c>ViewMemoryManager</c>, which holds this
    ///         object. A bare <see cref="ReadOnlySpan{T}" /> does <b>not</b> root anything, so code
    ///         that keeps only a span while dropping every reference to the source must
    ///         <see cref="GC.KeepAlive" /> the source (the parser keeps the
    ///         <see cref="ReadOnlyMemory{T}" /> itself alive for the whole parse, so the supported
    ///         entry points are fine).
    ///     </para>
    /// </summary>
    ~MemoryMappedDemoSource() => ReleaseUnmanaged(true);

    /// <summary>
    ///     Unmaps the file. After this call every slice previously handed out is invalid; touching one
    ///     is undefined behaviour (see the type-level contract). Idempotent.
    /// </summary>
    public void Dispose()
    {
        ReleaseUnmanaged(false);
        GC.SuppressFinalize(this);
    }

    private void ReleaseUnmanaged(bool fromFinalizer)
    {
        if (IsDisposed)
        {
            return;
        }

        // Order matters: flag first (so a same-thread late read throws rather than faults), then
        // release the pointer ref-count, then the accessor, then the mapping itself.
        IsDisposed = true;
        _pointer = null;
        if (_pointerAcquired)
        {
            _pointerAcquired = false;
            _viewHandle.ReleasePointer();
        }

        _accessor.Dispose();
        _map.Dispose();

        if (fromFinalizer)
        {
            // Test observability ONLY (see FinalizerReleaseCount). Kept off the public surface, and
            // incremented LAST on purpose: a finalizer that throws is fatal, so observing a bumped
            // count in a still-living process proves the WHOLE release path ran — ReleasePointer
            // included. Incrementing on entry would have proved only "some finalizer ran", which
            // would still be true if the ReleasePointer call were deleted (the exact leak this
            // finalizer exists to prevent).
            Interlocked.Increment(ref s_finalizerReleaseCount);
        }
    }

    /// <summary>
    ///     Opens <paramref name="path" /> as a read-only memory map. The file is opened with
    ///     <see cref="FileShare.ReadWrite" /> so concurrent readers (and the game still writing the
    ///     demo) do not fail the open.
    /// </summary>
    /// <param name="path">Path to the .dem file.</param>
    /// <returns>A source the caller MUST dispose (see the ownership contract on the type).</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">
    ///     The file is empty, or larger than <see cref="int.MaxValue" /> — a single
    ///     <see cref="ReadOnlyMemory{T}" /> cannot address more than 2 GB, and the parser's
    ///     offsets are <see cref="int" />, so a bigger file needs a chunked design rather than a
    ///     silent truncation.
    /// </exception>
    public static MemoryMappedDemoSource Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Demo file not found.", path);
        }

        FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            // Length MUST come from the opened stream, not from FileInfo.Length: for a SYMLINK the
            // FileInfo reports the link's own size (98 bytes for a typical path), which would map a
            // 98-byte view of a 180 MB demo and parse to ZERO frames without ever erroring. The repo's
            // own worktree tooling symlinks demos/benchmarks/*.dem, so this is not hypothetical — it
            // was caught by MemoryMappedParse_ProducesIdenticalOutputToByteArrayParse.
            long length = fs.Length;
            if (length == 0)
            {
                throw new InvalidDataException($"Demo file is empty: {path}");
            }

            if (length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Demo file is {length:N0} bytes; the memory-mapped path supports at most {int.MaxValue:N0} " +
                    "(one ReadOnlyMemory<byte> / int frame offsets). Use the byte[] entry point or a chunked reader.");
            }

            return OpenCore(fs, length);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Builds the map + view + pointer for an already-validated stream, disposing every partially
    ///     created resource if a later step fails. Separated from <see cref="Open" /> so the validation
    ///     and the multi-step resource acquisition each have exactly one failure path.
    /// </summary>
    private static MemoryMappedDemoSource OpenCore(FileStream fs, long length)
    {
        // mapName MUST be null — named maps are Windows-only and throw on macOS/Linux. leaveOpen:false
        // hands the FileStream to the map, which closes it when it is disposed.
        MemoryMappedFile map = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, false);

        MemoryMappedViewAccessor accessor;
        try
        {
            accessor = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        }
        catch
        {
            map.Dispose();
            throw;
        }

        try
        {
            return new MemoryMappedDemoSource(map, accessor, (int)length);
        }
        catch
        {
            accessor.Dispose();
            map.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Convenience for the overwhelmingly common case: map, parse, unmap. The mapping never
    ///     escapes this method, so the ownership contract cannot be violated by the caller.
    ///     Prefer this over hand-rolling <see cref="Open" /> + <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile?)" />
    ///     unless you need the raw bytes after the parse (hex views, on-demand payload decompression).
    /// </summary>
    /// <param name="path">Path to the .dem file.</param>
    /// <param name="profileOverride">Optional explicit <see cref="DemoProfile" />; see <see cref="DemoParser.Parse(ReadOnlyMemory{byte},DemoProfile?)" />.</param>
    public static ParsedDemo ParseFile(string path, DemoProfile? profileOverride = null)
    {
        using MemoryMappedDemoSource src = Open(path);
        ParsedDemo demo = DemoParser.Parse(src.Memory, profileOverride);
        // Belt-and-braces against the finalizer: `using` already keeps `src` live until the finally,
        // but stating it here makes the requirement explicit for anyone copying this shape.
        GC.KeepAlive(src);
        return demo;
    }

    /// <summary>
    ///     Bridges the unmanaged view pointer to <see cref="ReadOnlyMemory{T}" />. Instances are owned
    ///     one-to-one by their <see cref="MemoryMappedDemoSource" /> and never handed out directly.
    /// </summary>
    private sealed class ViewMemoryManager(MemoryMappedDemoSource owner) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            // Cheap guard so a same-thread use-after-dispose surfaces as a catchable exception rather
            // than a process-killing access violation. Not a race guard — see the type docs.
            ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
            return new Span<byte>(owner._pointer, owner.Length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
            ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, owner.Length);
            // The pages are unmanaged: already "pinned" for GC purposes, so there is nothing to do
            // beyond handing back the address.
            return new MemoryHandle(owner._pointer + elementIndex);
        }

        /// <summary>No-op: unmanaged memory is never moved by the GC.</summary>
        public override void Unpin() { }

        /// <summary>The lifetime is the owning <see cref="MemoryMappedDemoSource" />'s; nothing to release here.</summary>
        protected override void Dispose(bool disposing) { }
    }
}
