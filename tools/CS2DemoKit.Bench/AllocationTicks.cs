#region

using System.Diagnostics.Tracing;
using System.Globalization;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     Allocation by type, from the runtime's <c>GCAllocationTick</c> events: one sample per
///     roughly 100 KB allocated, attributed to the type being allocated. Enabled by
///     <c>CS2DEMOKIT_PATHS_ALLOCTICK=1</c> on a <c>path-measure</c> run; the top types go to
///     stderr after the row. A sample, not a count: it says what allocates, not how many times.
/// </summary>
internal sealed class AllocationTicks : EventListener
{
    private readonly Dictionary<string, (long Bytes, long Ticks)> _byType = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _totalBytes;

    public static AllocationTicks? StartIfRequested() =>
        Environment.GetEnvironmentVariable("CS2DEMOKIT_PATHS_ALLOCTICK") == "1" ? new AllocationTicks() : null;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // Keyword 0x1 is GC; AllocationTick is emitted at Verbose.
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is null || !eventData.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)
            || eventData.PayloadNames is null || eventData.Payload is null)
        {
            return;
        }

        int typeAt = eventData.PayloadNames.IndexOf("TypeName");
        int amountAt = eventData.PayloadNames.IndexOf("AllocationAmount64");
        if (amountAt < 0)
        {
            amountAt = eventData.PayloadNames.IndexOf("AllocationAmount");
        }

        if (typeAt < 0 || amountAt < 0)
        {
            return;
        }

        string type = eventData.Payload[typeAt] as string ?? "?";
        long amount = Convert.ToInt64(eventData.Payload[amountAt], CultureInfo.InvariantCulture);
        lock (_gate)
        {
            (long bytes, long ticks) = _byType.GetValueOrDefault(type);
            _byType[type] = (bytes + amount, ticks + 1);
            _totalBytes += amount;
        }
    }

    public void Report(TextWriter writer, int top = 30)
    {
        lock (_gate)
        {
            writer.WriteLine($"allocation ticks: {_totalBytes / (1024.0 * 1024):F0} MB sampled over {_byType.Values.Sum(v => v.Ticks)} ticks");
            foreach ((string type, (long bytes, long ticks)) in _byType.OrderByDescending(kv => kv.Value.Bytes).Take(top))
            {
                writer.WriteLine($"  {bytes / (1024.0 * 1024),8:F1} MB  {100.0 * bytes / Math.Max(1, _totalBytes),5:F1}%  {ticks,6} ticks  {type}");
            }
        }
    }
}
