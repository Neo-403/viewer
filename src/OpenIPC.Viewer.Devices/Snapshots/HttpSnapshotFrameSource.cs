using System;
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
/// (full-res, ~50–100 ms) — the primary source for OpenIPC cameras. The
/// IsMajestic flag is only written by an explicit probe, so it is NOT a gate:
/// a hand-added or imported OpenIPC camera would otherwise be declared
/// unsupported and the grid would silently keep playing live RTSP. We always
/// try the endpoint (short timeout when unprobed) and let the caller fall back
/// to a one-frame RTSP grab when it isn't there.
/// </summary>
public sealed class HttpSnapshotFrameSource : ISnapshotFrameSource
{
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeGrabTimeout = TimeSpan.FromSeconds(2);

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

    public async Task<byte[]?> GrabAsync(Camera camera, CancellationToken ct)
    {
        try
        {
            var creds = await _credentials.GetCredentialsAsync(camera.Id, ct).ConfigureAwait(false);
            var endpoint = new MajesticEndpoint(camera.Host, camera.HttpPort, creds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(camera.IsMajestic ? GrabTimeout : ProbeGrabTimeout);
            var bytes = await _majestic
                .SnapshotJpegAsync(endpoint, new MajesticSnapshotOptions(), cts.Token)
                .ConfigureAwait(false);
            // A non-Majestic web server can answer 200 with its login page.
            // Only accept a real JPEG (SOI marker), otherwise fall through to
            // the one-frame RTSP grab.
            if (bytes is { Length: > 2 } && bytes[0] == 0xFF && bytes[1] == 0xD8)
                return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Majestic snapshot grab failed for {Camera}", camera.Name);
        }

        // Caller falls back to a one-frame RTSP grab (SnapshotService). A
        // genuine ONVIF GetSnapshotUri path can be added here later.
        return null;
    }
}
