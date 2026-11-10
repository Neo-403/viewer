using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;

namespace OpenIPC.Viewer.Devices.Snapshots;

/// <summary>
/// Grabs a still over HTTP without a decoder. Majestic exposes <c>/image.jpg</c>
/// and can resize it server-side (<c>?width=</c>), which is the cheapest possible
/// still for a small grid tile. The IsMajestic flag is only written by an
/// explicit probe, so it is NOT a gate: a hand-added or imported OpenIPC camera
/// would otherwise be declared unsupported and the grid would silently keep
/// playing live RTSP. We always try the endpoint (short timeout when unprobed)
/// and let the caller fall back to a one-frame substream grab when it isn't there.
/// </summary>
public sealed class HttpSnapshotFrameSource : ISnapshotFrameSource
{
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeGrabTimeout = TimeSpan.FromSeconds(2);

    // A few firmware builds ignore or reject ?width= and answer with HTML / an
    // error instead of a JPEG. Remember those cameras so we don't pay for a
    // failed resize probe on every single refresh. Network failures are NOT
    // recorded — an unreachable camera must be retried normally next tick.
    private readonly ConcurrentDictionary<CameraId, byte> _resizeUnsupported = new();

    private readonly IMajesticClient _majestic;
    private readonly ICameraCredentialsProvider _credentials;
    private readonly ILogger<HttpSnapshotFrameSource> _logger;

    public HttpSnapshotFrameSource(
        IMajesticClient majestic,
        ICameraCredentialsProvider credentials,
        ILogger<HttpSnapshotFrameSource> logger)
    {
        _majestic = majestic;
        _credentials = credentials;
        _logger = logger;
    }

    // Any camera can be *tried*: the HTTP probe is cheap and, when it answers,
    // it is the lightest possible still. Returning false here used to make the
    // grid silently fall back to a live RTSP session for every unprobed camera,
    // so the stills toggle and its refresh interval appeared to do nothing.
    public bool Supports(Camera _) => true;

    public async Task<byte[]?> GrabAsync(Camera camera, int maxWidth, CancellationToken ct)
    {
        try
        {
            var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
            var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, creds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(camera.IsMajestic ? GrabTimeout : ProbeGrabTimeout);

            var options = MajesticSnapshotOptions.ForGrid(maxWidth);
            if (options.Width is not null && !_resizeUnsupported.ContainsKey(camera.Id))
            {
                var scaled = await _majestic
                    .SnapshotJpegAsync(endpoint, options, cts.Token)
                    .ConfigureAwait(false);
                if (IsJpeg(scaled)) return scaled;

                // Non-JPEG body (login page / 404 for the query form): fall
                // back to the native endpoint once, then remember the camera.
                _resizeUnsupported[camera.Id] = 1;
            }

            var bytes = await _majestic
                .SnapshotJpegAsync(endpoint, new MajesticSnapshotOptions(), cts.Token)
                .ConfigureAwait(false);
            // A non-Majestic web server can answer 200 with its login page.
            // Only accept a real JPEG (SOI marker), otherwise fall through to
            // the one-frame RTSP grab.
            return IsJpeg(bytes) ? bytes : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Majestic snapshot grab failed for {Camera}", camera.Name);
        }

        // Caller falls back to a one-frame substream grab (SnapshotService). A
        // genuine ONVIF GetSnapshotUri path can be added here later.
        return null;
    }

    private static bool IsJpeg(byte[]? bytes) =>
        bytes is { Length: > 2 } && bytes[0] == 0xFF && bytes[1] == 0xD8;
}
