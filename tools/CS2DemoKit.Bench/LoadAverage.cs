using System.Runtime.InteropServices;

namespace CS2DemoKit.Bench;

/// <summary>
///     The 1-minute load average, stamped on every CSV row. Read through libc's <c>getloadavg</c>
///     rather than by shelling out to <c>uptime</c>, whose output format differs between platforms.
///     Returns NaN where the call is unavailable, so a row still gets written.
/// </summary>
internal static class LoadAverage
{
    public static double OneMinute()
    {
        if (OperatingSystem.IsWindows())
        {
            return double.NaN;
        }

        try
        {
            double[] samples = new double[3];
            return GetLoadAvg(samples, samples.Length) > 0 ? samples[0] : double.NaN;
        }
        catch (DllNotFoundException)
        {
            return double.NaN;
        }
        catch (EntryPointNotFoundException)
        {
            return double.NaN;
        }
    }

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks, which is
    // a lot of blast radius for one call that reads three doubles.
    [DllImport("libc", EntryPoint = "getloadavg")]
    private static extern int GetLoadAvg([Out] double[] loadavg, int nelem);
}
