using FrameFlow.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Coverage for what <see langword="null"/> capabilities mean at the decoder (#181).
/// </summary>
/// <remarks>
/// <para>
/// <c>VideoDecoder.Open</c>, <c>PlaybackController.Create</c> and
/// <c>PlaybackController.CreatePlaylist</c> all documented <see langword="null"/> as
/// "re-probes". The decoder replaced it with <see cref="HardwareDecodeCapabilities.Empty"/>
/// instead, which is the set that forces software decode, so a caller passing
/// <see cref="HardwareDecodeMode.Auto"/> and no capabilities never got hardware.
/// </para>
/// <para>
/// The discriminating assertion compares a decoder opened with <see langword="null"/>
/// against one opened with the process probe's own result. On a host where a backend binds
/// the two differ exactly when <see langword="null"/> is ignored. On a host with no usable
/// backend both are software and the comparison cannot fail, which is the correct outcome
/// there, not a false pass: there is nothing for <see langword="null"/> to have lost.
/// </para>
/// </remarks>
public sealed class HardwareDecodeNullCapabilitiesTests : IClassFixture<FfmpegBootstrapFixture>
{
    // H.264 rather than the VP9 fixture HardwareDecodeRequiredTests uses: it is the codec
    // with the widest hardware decode support, so the comparison discriminates on the most
    // hosts.
    private const string VideoFixture = "test-video-h264-yuv420p.mp4";

    private static string RequireFixture()
    {
        var file = TestEnvironment.GetCorpusFile(VideoFixture);
        Assert.True(
            file is not null,
            $"Corpus is present but {VideoFixture} is missing. Re-run scripts/generate-test-corpus.cs."
        );
        return file!;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Auto_WithNullCapabilities_BindsWhatTheProcessProbeBinds()
    {
        var file = RequireFixture();
        var probed = HardwareDecodeProbe.GetOrRun(NullLogger.Instance, probeUncatalogued: false, out _);

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;
        var videoIdx = demux.MediaInfo.VideoStreams[0].StreamIndex;
        var auto = new HardwareDecodeOptions { Mode = HardwareDecodeMode.Auto };

        HardwareDecodeBackendKind? withProbe;
        await using (var decoder = VideoDecoder.Open(demux.FormatContextPtr, videoIdx, auto, probed, loggerFactory: null))
            withProbe = decoder.HardwareBackend;

        HardwareDecodeBackendKind? withNull;
        await using (var decoder = VideoDecoder.Open(demux.FormatContextPtr, videoIdx, auto, capabilities: null, loggerFactory: null))
            withNull = decoder.HardwareBackend;

        Assert.Equal(withProbe, withNull);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task Disabled_WithNullCapabilities_DecodesInSoftware()
    {
        var file = RequireFixture();

        var factory = new DemuxSessionFactory();
        await using var session = await factory.OpenAsync(MediaSource.FromFile(file));
        var demux = (DemuxSession)session;
        var videoIdx = demux.MediaInfo.VideoStreams[0].StreamIndex;

        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            videoIdx,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            capabilities: null,
            loggerFactory: null
        );

        Assert.Null(decoder.HardwareBackend);
    }
}
