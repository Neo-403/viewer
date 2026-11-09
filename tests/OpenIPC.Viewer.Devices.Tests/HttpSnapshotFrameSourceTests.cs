using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Devices.Snapshots;

namespace OpenIPC.Viewer.Devices.Tests;

// Regression for the grid "stills" mode appearing to do nothing: Supports() used
// to be gated on Camera.IsMajestic, a flag only an explicit probe ever writes.
// Every hand-added / imported camera then silently kept its live RTSP session,
// and the refresh interval was never read at all.
public sealed class HttpSnapshotFrameSourceTests
{
    [Fact]
    public void Unprobed_camera_is_still_eligible_for_stills()
    {
        // Supports() must not touch the injected services; null! keeps this test
        // focused on the capability gate itself.
        var source = new HttpSnapshotFrameSource(null!, null!, null!);

        var camera = new Camera(
            CameraId.New(), null, "Cam", "10.0.0.5", null, 80,
            new Uri("rtsp://10.0.0.5/main"), null, null, null,
            false, null, null, null, true, false,
            false, // IsMajestic — never probed
            0, DateTime.UtcNow, DateTime.UtcNow);

        Assert.True(source.Supports(camera));
    }
}
