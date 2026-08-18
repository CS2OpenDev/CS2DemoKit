#region

using System.Diagnostics;
using System.Diagnostics.Metrics;

#endregion

namespace CS2DemoKit.Analysis.Diagnostics;

/// <summary>
///     The runtime half of the unified profiling switch. When the <c>CS2DEMOKIT_PROFILE</c> environment
///     variable is truthy (<c>1</c>/<c>true</c>/<c>yes</c>, case-insensitive),
///     <see cref="StartFromEnvironment" /> attaches in-proc listeners to the analysis
///     <see cref="Meter" /> (<c>CS2DemoKit.Analysis.Evaluator</c> counters) and
///     <see cref="ActivitySource" /> (<c>CS2DemoKit.Analysis</c> phase timeline), and on
///     <see cref="Dispose" /> writes a combined report to a <see cref="TextWriter" /> (default
///     <see cref="Console.Out" />).
///     <para>
///         Default (env unset): no session is created, no listeners attach, and the Meter/Activity
///         sources idle at ~one predicted branch — general users pay nothing. The same live capture is
///         also available with no application code via <c>dotnet-counters</c> / <c>dotnet-trace</c> (see
///         <c>docs/profiling.md</c>); this type is the one-env-var convenience for a host that wants a
///         report dumped on exit.
///     </para>
/// </summary>
public sealed class ProfilingSession : IDisposable
{
    private const string EnvVar = "CS2DEMOKIT_PROFILE";

    // The name this switch shipped under before the library moved out of the DemoViewer.NET repo.
    private const string LegacyEnvVar = "DEMOVIEWER_PROFILE";
    private const string MeterName = "CS2DemoKit.Analysis.Evaluator";

    private readonly ActivityListener _activityListener;
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly MeterListener _meterListener;
    private readonly TextWriter _out;
    private readonly List<(string Name, DateTime Start, double Ms, int Depth)> _spans = [];
    private bool _disposed;

    /// <summary>Constructs a session and immediately attaches the Meter + ActivitySource listeners.</summary>
    public ProfilingSession(TextWriter? output = null)
    {
        _out = output ?? Console.Out;

        _meterListener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == MeterName)
                {
                    l.EnableMeasurementEvents(inst);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
        {
            lock (_gate)
            {
                _counters[inst.Name] = _counters.GetValueOrDefault(inst.Name) + measurement;
            }
        });
        _meterListener.Start();

        _activityListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == AnalysisDiagnostics.SourceName,
            Sample = static (ref options) => ActivitySamplingResult.AllData,
            ActivityStopped = OnSpanStopped
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    /// <summary>Detaches the listeners and writes the combined report. Spans/counters are final by call time.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meterListener.Dispose();
        _activityListener.Dispose();
        WriteReport();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Returns a started session iff <c>CS2DEMOKIT_PROFILE</c> is set to <c>1</c>/<c>true</c>/<c>yes</c>
    ///     (case-insensitive); otherwise <c>null</c> — the default, with no listeners and no cost.
    /// </summary>
    public static ProfilingSession? StartFromEnvironment(TextWriter? output = null)
    {
        string? v = Environment.GetEnvironmentVariable(EnvVar) ?? Environment.GetEnvironmentVariable(LegacyEnvVar);
        bool on = v == "1"
                  || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
        return on ? new ProfilingSession(output) : null;
    }

    private void OnSpanStopped(Activity a)
    {
        int depth = 0;
        for (Activity? p = a.Parent; p is not null; p = p.Parent)
        {
            depth++;
        }

        lock (_gate)
        {
            _spans.Add((a.OperationName, a.StartTimeUtc, a.Duration.TotalMilliseconds, depth));
        }
    }

    private void WriteReport()
    {
        lock (_gate)
        {
            _out.WriteLine();
            _out.WriteLine("─── CS2DemoKit Profiling Report (CS2DEMOKIT_PROFILE) ────");
            if (_spans.Count > 0)
            {
                _out.WriteLine("  Phase timeline:");
                foreach ((string name, _, double ms, int depth) in _spans.OrderBy(s => s.Start))
                {
                    _out.WriteLine($"    {new string(' ', depth * 2)}{name,-22} {ms,9:F1} ms");
                }
            }

            if (_counters.Count > 0)
            {
                _out.WriteLine("  Evaluator counters:");
                foreach (KeyValuePair<string, long> kv in _counters.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    _out.WriteLine($"    {kv.Key,-34} {kv.Value,14:N0}");
                }
            }

            if (_spans.Count == 0 && _counters.Count == 0)
            {
                _out.WriteLine("  (no analysis activity captured this session)");
            }
        }
    }
}
