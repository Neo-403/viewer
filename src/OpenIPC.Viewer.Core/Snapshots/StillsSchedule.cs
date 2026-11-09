using System;
using System.Globalization;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Timing policy for the grid's "stills" mode.
///
/// Every tile used to start its own <c>grab → Delay(interval)</c> loop at the
/// same instant, so an N-camera wall fired N JPEG requests simultaneously at
/// t=0, t=interval, t=2*interval … — an N-wide bandwidth spike every cycle.
///
/// The naive fix (wait for camera i to finish before starting camera i+1) makes
/// a round take sum(all grab durations). With a 2 s interval and nine cameras
/// at 300 ms each that is 2.7 s, so the wall silently refreshes slower than
/// requested — and a single slow camera holds up every other one.
///
/// Instead we keep each camera on its own fixed-origin cadence of exactly
/// <c>interval</c> and only offset WHEN in that cycle it starts: camera i of N
/// begins at <c>i * interval / N</c>, then repeats every interval. Peak
/// concurrency drops from N to about one, no camera ever waits for another,
/// and the user's interval is honoured exactly (even at 2 s). When a single
/// grab lasts longer than its slot, a couple of requests overlap — still far
/// cheaper than the original all-at-once burst, and without any queuing.
///
/// Fixed origins also matter for the non-staggered case: the old loop measured
/// the interval from the END of a grab, so the real refresh period was
/// "grab duration + interval" and tiles with different latencies drifted apart.
/// </summary>
public static class StillsSchedule
{
    /// <summary>Smallest accepted refresh interval, in seconds.</summary>
    public const int MinIntervalSeconds = 1;

    /// <summary>Largest accepted refresh interval, in seconds (1 hour).</summary>
    public const int MaxIntervalSeconds = 3600;

    /// <summary>
    /// Breather between two grabs of the same camera when one grab overran a
    /// whole interval. Without it a wedged camera would spin on the network
    /// with no delay at all.
    /// </summary>
    public static readonly TimeSpan MinGrabGap = TimeSpan.FromMilliseconds(100);

    /// <summary>Clamp a stored / typed interval into the supported range.</summary>
    public static int ClampInterval(int seconds) => seconds switch
    {
        < MinIntervalSeconds => MinIntervalSeconds,
        > MaxIntervalSeconds => MaxIntervalSeconds,
        _ => seconds,
    };

    /// <summary>
    /// Parse the editable interval box. Out-of-range numbers are clamped rather
    /// than rejected, so "0" becomes 1 and "99999" becomes 3600.
    /// </summary>
    public static bool TryParseInterval(string? text, out int seconds)
    {
        seconds = 0;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
            return false;
        seconds = ClampInterval(raw);
        return true;
    }

    /// <summary>
    /// Start offset in milliseconds for camera <paramref name="index"/> in a
    /// wall of <paramref name="count"/> cameras. Returns 0 for the first camera
    /// and for a single-camera wall. Callers pass 0 for <paramref name="count"/>
    /// (or simply skip this) when staggering is off.
    /// </summary>
    public static double PhaseMs(int index, int count, int intervalSeconds)
    {
        if (count <= 1 || index <= 0) return 0;
        var periodMs = (double)ClampInterval(intervalSeconds) * 1000;
        var i = Math.Min(index, count - 1);
        return periodMs * i / count;
    }
}
