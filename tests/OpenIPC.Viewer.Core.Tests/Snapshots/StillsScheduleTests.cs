using System;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.Core.Tests.Snapshots;

public class StillsScheduleTests
{
    [Fact]
    public void ClampInterval_KeepsInRangeValues()
    {
        Assert.Equal(StillsSchedule.MinIntervalSeconds, StillsSchedule.ClampInterval(0));
        Assert.Equal(StillsSchedule.MinIntervalSeconds, StillsSchedule.ClampInterval(-5));
        Assert.Equal(StillsSchedule.MinIntervalSeconds, StillsSchedule.ClampInterval(1));
        Assert.Equal(10, StillsSchedule.ClampInterval(10));
        Assert.Equal(StillsSchedule.MaxIntervalSeconds, StillsSchedule.ClampInterval(3600));
        Assert.Equal(StillsSchedule.MaxIntervalSeconds, StillsSchedule.ClampInterval(99999));
        Assert.Equal(StillsSchedule.MaxIntervalSeconds, StillsSchedule.ClampInterval(int.MaxValue));
    }

    [Fact]
    public void TryParseInterval_AcceptsIntegersAndClampsOutOfRange()
    {
        Assert.True(StillsSchedule.TryParseInterval("15", out var n));
        Assert.Equal(15, n);

        Assert.True(StillsSchedule.TryParseInterval(" 7 ", out n));
        Assert.Equal(7, n);

        Assert.True(StillsSchedule.TryParseInterval("0", out n));
        Assert.Equal(1, n);

        Assert.True(StillsSchedule.TryParseInterval("99999", out n));
        Assert.Equal(StillsSchedule.MaxIntervalSeconds, n);

        Assert.False(StillsSchedule.TryParseInterval("", out _));
        Assert.False(StillsSchedule.TryParseInterval(null, out _));
        Assert.False(StillsSchedule.TryParseInterval("abc", out _));
        Assert.False(StillsSchedule.TryParseInterval("2.5", out _));
    }

    [Fact]
    public void PhaseMs_FirstCameraAndSingleCameraStartImmediately()
    {
        Assert.Equal(0d, StillsSchedule.PhaseMs(0, 9, 10));
        Assert.Equal(0d, StillsSchedule.PhaseMs(0, 1, 10));
        Assert.Equal(0d, StillsSchedule.PhaseMs(3, 1, 10));
        Assert.Equal(0d, StillsSchedule.PhaseMs(3, 0, 10));
        Assert.Equal(0d, StillsSchedule.PhaseMs(-1, 9, 10));
    }

    [Fact]
    public void PhaseMs_SpreadsGrabsEvenlyAcrossOneInterval()
    {
        // Nine cameras on a 10 s interval: one grab every 1.111 s, and the last
        // start is still inside the first cycle.
        Assert.Equal(10_000d / 9, StillsSchedule.PhaseMs(1, 9, 10), 6);
        Assert.Equal(2 * 10_000d / 9, StillsSchedule.PhaseMs(2, 9, 10), 6);
        Assert.Equal(8 * 10_000d / 9, StillsSchedule.PhaseMs(8, 9, 10), 6);
        Assert.True(StillsSchedule.PhaseMs(8, 9, 10) < 10_000);

        for (var i = 1; i < 9; i++)
            Assert.Equal(10_000d / 9,
                StillsSchedule.PhaseMs(i, 9, 10) - StillsSchedule.PhaseMs(i - 1, 9, 10), 6);
    }

    [Fact]
    public void PhaseMs_ShortIntervalDoesNotLengthenTheCycle()
    {
        // The 2 s / 9-camera case: slots are only ~222 ms, so slow grabs may
        // overlap, but the last phase is still < 2 s — no camera is pushed
        // into the next cycle by waiting on the previous one.
        Assert.Equal(2000d / 9, StillsSchedule.PhaseMs(1, 9, 2), 6);
        Assert.True(StillsSchedule.PhaseMs(8, 9, 2) < 2000);
        Assert.Equal(1000d, StillsSchedule.PhaseMs(1, 2, 2), 6);
    }

    [Fact]
    public void PhaseMs_ClampsIndexAndUsesClampedInterval()
    {
        Assert.Equal(StillsSchedule.PhaseMs(8, 9, 10), StillsSchedule.PhaseMs(100, 9, 10));
        Assert.Equal(500d, StillsSchedule.PhaseMs(1, 2, 0), 6);
        // Out-of-range interval is clamped before the phase is computed.
        Assert.Equal(StillsSchedule.PhaseMs(1, 2, StillsSchedule.MaxIntervalSeconds),
            StillsSchedule.PhaseMs(1, 2, int.MaxValue));
    }

    [Fact]
    public void MinGrabGap_IsShortComparedToTheSmallestInterval()
    {
        Assert.True(StillsSchedule.MinGrabGap < TimeSpan.FromSeconds(StillsSchedule.MinIntervalSeconds));
    }
}
