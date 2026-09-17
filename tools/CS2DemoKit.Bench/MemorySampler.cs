#region

using System.Diagnostics;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     Samples the process's memory on a background thread while an arm runs, keeping the
///     high-water mark of each figure. Heap is <see cref="GC.GetTotalMemory(bool)" /> without a
///     collection (live data plus garbage not yet collected), committed is what the collector
///     holds from the OS as of its last collection, and working set is what the OS says the
///     process has resident. The three answer different questions, and a flat line has to hold
///     on all of them before a path can claim not to grow with the demo.
/// </summary>
internal sealed class MemorySampler
{
    private readonly int _intervalMs;
    private readonly Thread _thread;
    private volatile bool _stop;

    private MemorySampler(int intervalMs)
    {
        _intervalMs = intervalMs;
        _thread = new Thread(Loop) { IsBackground = true, Name = "bench-memory-sampler" };
    }

    public long BaseHeap { get; private set; }
    public long BaseCommitted { get; private set; }
    public long BaseWorkingSet { get; private set; }
    public long PeakHeap { get; private set; }
    public long PeakCommitted { get; private set; }
    public long PeakWorkingSet { get; private set; }
    public long PeakPrivate { get; private set; }
    public int Samples { get; private set; }

    public static MemorySampler Start(int intervalMs = 20)
    {
        MemorySampler sampler = new(intervalMs);
        using (Process p = Process.GetCurrentProcess())
        {
            sampler.BaseHeap = GC.GetTotalMemory(false);
            sampler.BaseCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
            sampler.BaseWorkingSet = p.WorkingSet64;
        }

        sampler._thread.Start();
        return sampler;
    }

    public void Stop()
    {
        _stop = true;
        _thread.Join();
    }

    private void Loop()
    {
        using Process p = Process.GetCurrentProcess();
        while (!_stop)
        {
            Sample(p);
            Thread.Sleep(_intervalMs);
        }

        Sample(p);
    }

    // CS2DEMOKIT_PATHS_LIVE=1 forces a compacting collection before every sample, so the peak is
    // the live set rather than live plus garbage. Slow, and it perturbs the arm; diagnosis only.
    private static readonly bool _live = Environment.GetEnvironmentVariable("CS2DEMOKIT_PATHS_LIVE") == "1";

    private void Sample(Process p)
    {
        Samples++;
        if (_live)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }

        PeakHeap = Math.Max(PeakHeap, GC.GetTotalMemory(false));
        PeakCommitted = Math.Max(PeakCommitted, GC.GetGCMemoryInfo().TotalCommittedBytes);
        p.Refresh();
        PeakWorkingSet = Math.Max(PeakWorkingSet, p.WorkingSet64);
        PeakPrivate = Math.Max(PeakPrivate, p.PrivateMemorySize64);
    }
}
