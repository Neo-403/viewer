using System;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Captures a still and indexes it. Always prefers an HD source: an
/// already-running mainstream session, else the Majestic HTTP snapshot, else a
/// briefly-opened mainstream — only falling back to a running substream when
/// none of those are available. See <see cref="SnapshotService"/>.
/// </summary>
public interface ISnapshotService
{
    /// <summary>
    /// Captures, saves, thumbnails and DB-indexes a snapshot for
    /// <paramref name="camera"/>. <paramref name="liveSession"/> /
    /// <paramref name="liveQuality"/> describe the caller's currently-open
    /// session (if any) so a live mainstream frame can be grabbed for free.
    /// </summary>
    Task<Snapshot> CaptureAsync(
        Camera camera,
        IVideoSession? liveSession,
        StreamQuality? liveQuality,
        CancellationToken ct);

    /// <summary>
    /// Grabs one JPEG frame without saving, thumbnailing or DB-indexing it.
    /// Powers the grid's stills loop, which refreshes every N seconds and must
    /// not fill the snapshot library. Unlike <see cref="CaptureAsync"/> this is
    /// deliberately cheap: a resized HTTP still, or one frame off the
    /// SUBSTREAM — a grid cell cannot show the difference, while a mainstream
    /// session costs an order of magnitude more bandwidth.
    /// <paramref name="maxWidth"/> is the tile width (0 = native). Returns null
    /// when no source answered, so the caller keeps the previous frame.
    /// </summary>
    Task<byte[]?> GrabFrameAsync(Camera camera, int maxWidth, CancellationToken ct);

    /// <summary>
    /// Renders <paramref name="edit"/> over <paramref name="source"/> and saves
    /// the result as a new <c>*_edited.jpg</c> copy (the original is untouched),
    /// generating a thumbnail and a DB row. Returns the new snapshot.
    /// </summary>
    /// <summary>
    /// Saves an already-encoded JPEG frame (e.g. from the archive player) as a
    /// snapshot of <paramref name="cameraId"/> taken at <paramref name="takenAtUtc"/>.
    /// </summary>
    Task<Snapshot> SaveFrameAsync(CameraId cameraId, byte[] jpeg, DateTime takenAtUtc, CancellationToken ct);

    Task<Snapshot> SaveEditAsync(Snapshot source, SnapshotEdit edit, CancellationToken ct);
}
