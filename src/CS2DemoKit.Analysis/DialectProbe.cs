#region

using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The stop rule for the vocabulary probe. The whole file only confirms what the first round
///     decides: a matchmaking recording fires <c>round_officially_ended</c> 19 to 20 ticks after
///     each in-match <c>cs_pre_restart</c>, a tournament recording never fires it. The
///     <c>cs_pre_restart</c> every recording fires before the first freeze precedes no round end on
///     either, so it does not count.
/// </summary>
internal static class DialectProbe
{
    // Ten seconds at 64 ticks; the marker that would decide matchmaking follows in twenty.
    internal const int WindowTicks = 640;

    /// <summary>
    ///     A predicate for <see cref="DemoReader.ProbeGameEventNames(Func{string, int, bool}, CancellationToken)" />:
    ///     true on the first <c>round_officially_ended</c>, or on the first fire more than
    ///     <see cref="WindowTicks" /> past an in-match <c>cs_pre_restart</c> without one.
    /// </summary>
    public static Func<string, int, bool> Stop()
    {
        bool freezeSeen = false;
        int preRestartTick = int.MinValue;
        return (name, tick) =>
        {
            if (name == "round_officially_ended")
            {
                return true;
            }

            if (preRestartTick != int.MinValue && tick - preRestartTick > WindowTicks)
            {
                return true;
            }

            if (name == "round_freeze_end")
            {
                freezeSeen = true;
            }
            else if (name == "cs_pre_restart" && freezeSeen && preRestartTick == int.MinValue)
            {
                preRestartTick = tick;
            }

            return false;
        };
    }
}
