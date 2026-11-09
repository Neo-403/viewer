using OpenIPC.Viewer.Video.Recording;

namespace OpenIPC.Viewer.Video.Tests;

// Regression for the "Conversion failed! / Could not find tag for codec
// pcm_alaw" bug: a plain "-c copy" cannot put a G.711/PCM camera track into
// MP4, so ffmpeg aborted at write_header and no segment was ever recorded.
public sealed class FfmpegRecordingArgumentsTests
{
    private static IReadOnlyList<string> Args(string timeoutFlag = "-timeout") =>
        FfmpegRecordingSession.BuildArguments(
            new Uri("rtsp://admin:admin@10.10.1.211/"),
            "cam_%Y%m%d_%H%M%S.mp4",
            TimeSpan.FromMinutes(10),
            timeoutFlag);

    private static string? After(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    [Fact]
    public void G711_audio_is_transcoded_to_aac_instead_of_stream_copied()
    {
        var args = Args();
        Assert.Equal("copy", After(args, "-c:v"));
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("64k", After(args, "-b:a"));
        Assert.DoesNotContain("-c", args);
    }

    [Fact]
    public void Segment_and_fragmented_mp4_options_are_preserved()
    {
        var args = Args();
        Assert.Equal("segment", After(args, "-f"));
        Assert.Equal("600", After(args, "-segment_time"));
        Assert.Equal("1", After(args, "-reset_timestamps"));
        Assert.Equal("1", After(args, "-strftime"));
        Assert.Equal("+frag_keyframe+empty_moov+default_base_moof", After(args, "-movflags"));
        Assert.Equal("cam_%Y%m%d_%H%M%S.mp4", args[args.Count - 1]);
    }

    [Fact]
    public void Rtsp_transport_and_resolved_timeout_flag_are_kept()
    {
        Assert.Equal("tcp", After(Args(), "-rtsp_transport"));
        Assert.Equal("10000000", After(Args(), "-timeout"));
        Assert.Equal("10000000", After(Args("-stimeout"), "-stimeout"));
    }

    [Fact]
    public void Credentials_in_the_rtsp_url_reach_ffmpeg() =>
        Assert.Equal("rtsp://admin:admin@10.10.1.211/", After(Args(), "-i"));
}
