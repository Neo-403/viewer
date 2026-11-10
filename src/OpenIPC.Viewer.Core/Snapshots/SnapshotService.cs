using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Platform;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Core.Snapshots;

/// <summary>
/// Phase 14.1 — the one place that captures a still. Centralises the HD-source
/// priority so the grid tile, the single-camera page and any future caller all
/// behave identically and never persist an SD frame when an HD source exists.
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    // Gallery thumbnails: longest side. Big enough to look crisp on a HiDPI
    // tile, small enough that thousands decode cheaply.
    private const int ThumbMaxDim = 320;

    private static readonly TimeSpan MajesticTimeout = TimeSpan.FromSeconds(5);
    // The grid retries HTTP once before paying for an RTSP session. Keep it
    // short: the primary HTTP probe already ran in HttpSnapshotFrameSource.
    private static readonly TimeSpan GridHttpTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FreshOpenFrameTimeout = TimeSpan.FromSeconds(8);

    private readonly IMajesticClient _majestic;
    private readonly LiveStreamCoordinator _coordinator;
    private readonly ICameraCredentialsProvider _credentials;
    private readonly ISnapshotRepository _repo;
    private readonly IFileSystem _fs;
    private readonly IThumbnailGenerator _thumbs;
    private readonly IImageEditor _editor;

    public SnapshotService(
        IMajesticClient majestic,
        LiveStreamCoordinator coordinator,
        ICameraCredentialsProvider credentials,
        ISnapshotRepository repo,
        IFileSystem fs,
        IThumbnailGenerator thumbs,
        IImageEditor editor)
    {
        _majestic = majestic;
        _coordinator = coordinator;
        _credentials = credentials;
        _repo = repo;
        _fs = fs;
        _thumbs = thumbs;
        _editor = editor;
    }

    public async Task<Snapshot> SaveEditAsync(Snapshot source, SnapshotEdit edit, CancellationToken ct)
    {
        var id = SnapshotId.New();
        var dir = Path.GetDirectoryName(source.Path) ?? _fs.SnapshotsDir.FullName;
        var stem = Path.GetFileNameWithoutExtension(source.Path);
        var outPath = EnsureUnique(Path.Combine(dir, stem + "_edited.jpg"));

        var size = await _editor.RenderAsync(source.Path, edit, outPath, ct).ConfigureAwait(false);

        var thumbDir = Path.Combine(_fs.SnapshotsDir.FullName, ".thumbs");
        Directory.CreateDirectory(thumbDir);
        var thumbPath = Path.Combine(thumbDir, id.ToString() + ".jpg");
        string? savedThumb = thumbPath;
        try
        {
            var bytes = await ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
            await _thumbs.GenerateAsync(bytes, thumbPath, ThumbMaxDim, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            savedThumb = null;
        }

        var snapshot = new Snapshot(
            id, source.CameraId, DateTime.UtcNow, outPath, savedThumb,
            size.Width, size.Height, SnapshotSource.Edited, SnapshotKind.Manual);
        await _repo.AddAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<Snapshot> CaptureAsync(
        Camera camera, IVideoSession? liveSession, StreamQuality? liveQuality, CancellationToken ct)
    {
        var (jpeg, source) = await GrabAsync(camera, liveSession, liveQuality, ct).ConfigureAwait(false);
        return await SaveAsync(camera.Id, jpeg, source, SnapshotKind.Manual, DateTime.UtcNow, ct).ConfigureAwait(false);
    }

    public Task<Snapshot> SaveFrameAsync(CameraId cameraId, byte[] jpeg, DateTime takenAtUtc, CancellationToken ct) =>
        SaveAsync(cameraId, jpeg, SnapshotSource.Recording, SnapshotKind.Manual, takenAtUtc, ct);

    // Stills-mode fallback for cameras with no HTTP snapshot endpoint: briefly
    // open the SUBSTREAM, take one JPEG, release. Unlike CaptureAsync this
    // never writes to disk or adds a row — the grid calls it every few seconds.
    //
    // Saved snapshots must stay HD, so this deliberately does NOT share
    // GrabAsync's mainstream-first priority. A grid cell is a few hundred
    // pixels wide: it cannot show the difference, while a mainstream session
    // costs ~8x the bandwidth and used to be re-opened for every camera on
    // every refresh — the multi-MB/s spikes on a stills wall.
    public async Task<byte[]?> GrabFrameAsync(Camera camera, int maxWidth, CancellationToken ct)
    {
        try
        {
            // The tile already tried the HTTP endpoint once; retry briefly so a
            // transient miss doesn't force a whole RTSP session open.
            var http = await TryGridHttpAsync(camera, maxWidth, ct).ConfigureAwait(false);
            if (http is not null) return http;

            return await GrabFromFreshStreamAsync(camera, preferSubstream: true, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A transient miss must leave the previous still on screen rather
            // than blanking the tile.
            return null;
        }
    }

    private async Task<byte[]?> TryGridHttpAsync(Camera camera, int maxWidth, CancellationToken ct)
    {
        try
        {
            var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
            var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, creds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(GridHttpTimeout);
            // Ask the camera to resize: full-res JPEGs are the other half of
            // the bandwidth problem. 0 (single-camera still) means "native".
            var b = await _majestic
                .SnapshotJpegAsync(endpoint, MajesticSnapshotOptions.ForGrid(maxWidth), cts.Token)
                .ConfigureAwait(false);
            // A non-Majestic web server can answer 200 with its login page.
            return IsJpeg(b) ? b : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsJpeg(byte[]? bytes) =>
        bytes is { Length: > 2 } && bytes[0] == 0xFF && bytes[1] == 0xD8;

    private async Task<(byte[] Jpeg, SnapshotSource Source)> GrabAsync(
        Camera camera, IVideoSession? liveSession, StreamQuality? liveQuality, CancellationToken ct)
    {
        // 1. Already-running mainstream — grab the last decoded frame for free.
        if (liveSession is not null && liveQuality == StreamQuality.Main)
        {
            var b = await TryGrabAsync(liveSession, ct).ConfigureAwait(false);
            if (b is not null) return (b, SnapshotSource.LiveMain);
        }

        // 2. Majestic HTTP /image.jpg — always full-resolution, ~50–100 ms, and
        //    far cheaper than opening a fresh RTSP session.
        if (camera.IsMajestic)
        {
            try
            {
                var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
                var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, creds);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(MajesticTimeout);
                var b = await _majestic.SnapshotJpegAsync(endpoint, new MajesticSnapshotOptions(), cts.Token)
                    .ConfigureAwait(false);
                if (b.Length > 0) return (b, SnapshotSource.HttpSnapshot);
            }
            catch (Exception)
            {
                // Majestic unreachable / returned a non-image — fall through.
            }
        }

        // 3. Briefly open the mainstream, grab a keyframe, release.
        try
        {
            var b = await GrabFromFreshMainAsync(camera, ct).ConfigureAwait(false);
            if (b is not null) return (b, SnapshotSource.OpenedStream);
        }
        catch (Exception)
        {
            // Couldn't open the mainstream — fall through to the SD fallback.
        }

        // 4. Last resort: a running substream (SD) is better than no snapshot.
        if (liveSession is not null && liveQuality == StreamQuality.Sub)
        {
            var b = await TryGrabAsync(liveSession, ct).ConfigureAwait(false);
            if (b is not null) return (b, SnapshotSource.LiveSub);
        }

        throw new InvalidOperationException($"No snapshot source available for camera {camera.Id}");
    }

    private async Task<byte[]?> TryGrabAsync(IVideoSession session, CancellationToken ct)
    {
        try
        {
            var b = await session.SnapshotAsync(SnapshotFormat.Jpeg, ct).ConfigureAwait(false);
            return b.Length > 0 ? b : null;
        }
        catch (Exception)
        {
            // Most commonly "No frame available yet" — let the caller fall through.
            return null;
        }
    }

    // HD capture path — never downgraded, so saved snapshots stay full quality.
    private Task<byte[]?> GrabFromFreshMainAsync(Camera camera, CancellationToken ct) =>
        GrabFromFreshStreamAsync(camera, preferSubstream: false, ct);

    private async Task<byte[]?> GrabFromFreshStreamAsync(Camera camera, bool preferSubstream, CancellationToken ct)
    {
        var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
        // Grid stills use the substream when the camera has one; it is already
        // encoded at a resolution that matches a small tile, at a fraction of
        // the mainstream bitrate. CaptureAsync passes false and keeps HD.
        var useSub = preferSubstream && camera.RtspSubUri is not null;
        var quality = useSub ? StreamQuality.Sub : StreamQuality.Main;
        var uri = useSub ? camera.RtspSubUri! : camera.RtspMainUri;

        // No auto-reconnect: this is a one-shot grab, we don't want a lingering
        // reconnect loop after we release.
        var options = VideoSessionOptions.Default(uri, creds) with { AutoReconnect = false };

        // Acquire is ref-counted: if that stream is already up (another viewer),
        // we share it and grab immediately; otherwise we own a fresh one.
        var session = _coordinator.Acquire(camera.Id, quality, options);
        try
        {
            if (session.State == SessionState.Idle)
                await session.StartAsync(ct).ConfigureAwait(false);

            // Poll until the first keyframe decodes (SnapshotAsync returns an
            // empty array until then). Avoids an Rx dependency in Core just to
            // await one frame.
            var deadline = DateTime.UtcNow + FreshOpenFrameTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var b = await TryGrabAsync(session, ct).ConfigureAwait(false);
                if (b is not null) return b;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            return null;
        }
        finally
        {
            await _coordinator.ReleaseAsync(camera.Id, quality).ConfigureAwait(false);
        }
    }

    private async Task<Snapshot> SaveAsync(
        CameraId cameraId, byte[] jpeg, SnapshotSource source, SnapshotKind kind, DateTime takenAt, CancellationToken ct)
    {
        var id = SnapshotId.New();

        var dir = Path.Combine(_fs.SnapshotsDir.FullName, cameraId.ToString());
        Directory.CreateDirectory(dir);
        var fileName = takenAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".jpg";
        var path = EnsureUnique(Path.Combine(dir, fileName));
        await WriteAllBytesAsync(path, jpeg, ct).ConfigureAwait(false);

        var thumbDir = Path.Combine(_fs.SnapshotsDir.FullName, ".thumbs");
        Directory.CreateDirectory(thumbDir);
        var thumbPath = Path.Combine(thumbDir, id.ToString() + ".jpg");

        ImageSize size = default;
        string? savedThumb = thumbPath;
        try
        {
            size = await _thumbs.GenerateAsync(jpeg, thumbPath, ThumbMaxDim, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A missing thumbnail just means the gallery decodes the full image;
            // never let it sink the capture.
            savedThumb = null;
        }

        var snapshot = new Snapshot(id, cameraId, takenAt, path, savedThumb, size.Width, size.Height, source, kind);
        await _repo.AddAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        // File.WriteAllBytesAsync isn't on netstandard2.1; stream it instead.
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await fs.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var buffer = new byte[fs.Length];
        var offset = 0;
        int read;
        while (offset < buffer.Length &&
               (read = await fs.ReadAsync(buffer, offset, buffer.Length - offset, ct).ConfigureAwait(false)) > 0)
            offset += read;
        return buffer;
    }
}
