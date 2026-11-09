using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Grabs a single still frame (JPEG bytes) for a camera as cheaply as possible —
/// an HTTP snapshot endpoint, never a video decoder. Powers the grid "stills"
/// mode (periodic image refresh instead of a live RTSP session) and the private
/// timelapse archive. Returns null when no cheap source is available for the
/// camera (caller shows a placeholder / keeps the last frame).
/// </summary>
public interface ISnapshotFrameSource
{
    /// <summary>
    /// Whether a still can be attempted for this camera. Implementations should
    /// return true for every camera and let <see cref="GrabAsync"/> report a
    /// miss: gating on a discovery flag (e.g. IsMajestic) makes the grid
    /// silently fall back to a live RTSP session for hand-added cameras, which
    /// defeats the whole stills mode and its refresh interval. When no source
    /// answers, callers fall back to a one-frame RTSP grab — never to a live
    /// stream.
    /// </summary>
    bool Supports(Camera camera);

    Task<byte[]?> GrabAsync(Camera camera, CancellationToken ct);
}
