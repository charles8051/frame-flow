using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Native.Interop;
using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// AV1 decodes on hardware (#417). <c>avcodec_find_decoder</c> returns <c>libdav1d</c> for AV1, which
/// has no hardware configs, so no backend was ever a candidate. FFmpeg's own <c>av1</c> decoder has
/// them, and no software path: without a hardware config it fails its first packet. The decoder
/// looks past the default for one that can run on hardware, and keeps <c>libdav1d</c> as the
/// software candidate.
/// </summary>
[Collection(DecodePoolCollection.Name)]
public sealed class Av1HardwareTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    private const int Av1 = (int)AVCodecID.AV_CODEC_ID_AV1;

    // ── Which decoders a codec's hardware candidates come from (FFmpeg only) ──

    [RequiresFfmpegFact]
    public void Av1HardwareCandidatesComeFromTheNativeDecoderAndNotLibdav1d()
    {
        var names = VideoDecoder.HardwareDecoderNames(Av1);

        Assert.Contains("av1", names);
        Assert.DoesNotContain("libdav1d", names);
        Assert.DoesNotContain("libaom-av1", names);
    }

    [RequiresFfmpegFact]
    public void ACodecWhoseDefaultDecoderHasHardwareConfigsKeepsOnlyThatOne()
    {
        // The default already has configs, so no other registered decoder is looked at.
        Assert.Equal(["h264"], VideoDecoder.HardwareDecoderNames((int)AVCodecID.AV_CODEC_ID_H264));
        Assert.Equal(["vp9"], VideoDecoder.HardwareDecoderNames((int)AVCodecID.AV_CODEC_ID_VP9));
        Assert.Equal(["mjpeg"], VideoDecoder.HardwareDecoderNames((int)AVCodecID.AV_CODEC_ID_MJPEG));
        Assert.Equal(
            ["mpeg2video"],
            VideoDecoder.HardwareDecoderNames((int)AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
        );
    }

    [RequiresFfmpegFact]
    public void ACodecNoRegisteredDecoderCanRunOnHardwareHasNone() =>
        Assert.Empty(VideoDecoder.HardwareDecoderNames((int)AVCodecID.AV_CODEC_ID_UTVIDEO));

    [RequiresFfmpegFact]
    public void TheVendorWrappersAreNotHardwareCandidates()
    {
        // h264_cuvid and its kind are wrappers around a vendor decoder, which ADR-0033 rejected.
        var all = new List<string>();
        foreach (int id in new[] { Av1, (int)AVCodecID.AV_CODEC_ID_H264, (int)AVCodecID.AV_CODEC_ID_HEVC })
            all.AddRange(VideoDecoder.HardwareDecoderNames(id));

        Assert.DoesNotContain(all, name => name.EndsWith("_cuvid") || name.EndsWith("_qsv") || name.EndsWith("_amf"));
    }

    // ── Decoding it ──────────────────────────────────────────────────────────

    [RequiresAv1SoftwareFact]
    public async Task Disabled_DecodesTheClipWithTheSoftwareDecoder()
    {
        var run = await DecodeAsync(Av1Fixtures.Clip420, HardwareDecodeMode.Disabled);

        Assert.Equal(Av1Fixtures.Clip420Frames, run.Frames);
        Assert.Null(run.Bound);
    }

    [RequiresAv1HardwareFact]
    public async Task Auto_DecodesAv1OnHardware()
    {
        var software = await DecodeAsync(Av1Fixtures.Clip420, HardwareDecodeMode.Disabled);

        var run = await DecodeAsync(Av1Fixtures.Clip420, HardwareDecodeMode.Auto);

        Assert.Equal(Av1Fixtures.Clip420Frames, run.Frames);
        Assert.NotNull(run.Bound);
        Assert.NotNull(run.Engaged);
        Assert.True(
            run.MeanDifference(software) < 3,
            $"hardware differs from software by {run.MeanDifference(software):F2} levels on average"
        );
    }

    [RequiresAv1HardwareFact]
    public async Task Required_BindsTheBackendThatDecodesIt()
    {
        var backend = Av1Hardware.WorkingBackends[0];

        var run = await DecodeAsync(
            Av1Fixtures.Clip420,
            HardwareDecodeMode.Required,
            preferred: [backend],
            only: backend
        );

        Assert.Equal(backend, run.Bound);
        Assert.Equal(backend, run.Engaged);
        Assert.Equal(Av1Fixtures.Clip420Frames, run.Frames);
    }

    [RequiresAv1HardwareFact]
    public async Task EveryBackendThatDecodesAv1DoesSoWithBothFrameKinds()
    {
        foreach (var backend in Av1Hardware.WorkingBackends)
        {
            foreach (bool gpuFrames in new[] { false, true })
            {
                var run = await DecodeAsync(
                    Av1Fixtures.Clip420,
                    HardwareDecodeMode.Required,
                    preferred: [backend],
                    only: backend,
                    yieldHardwareFrames: gpuFrames
                );

                string where = $"{backend}, hardware frames {gpuFrames}";
                Assert.True(run.Frames == Av1Fixtures.Clip420Frames, $"{where}: {run.Frames} frames");
                Assert.True(run.Engaged == backend, $"{where}: engaged {run.Engaged}");
                Assert.True(
                    run.GpuFrames == (gpuFrames ? Av1Fixtures.Clip420Frames : 0),
                    $"{where}: {run.GpuFrames} GPU frames"
                );
            }
        }
    }

    [RequiresAv1HardwareFact]
    public async Task Auto_KeepsAv1OnHardwareFramesWhenAsked()
    {
        var backend = Av1Hardware.WorkingBackends[0];

        var run = await DecodeAsync(
            Av1Fixtures.Clip420,
            HardwareDecodeMode.Auto,
            preferred: [backend],
            only: backend,
            yieldHardwareFrames: true
        );

        Assert.Equal(Av1Fixtures.Clip420Frames, run.Frames);
        Assert.Equal(Av1Fixtures.Clip420Frames, run.GpuFrames);
    }

    [RequiresAv1HardwareFact]
    public async Task Auto_ExcludingAv1DecodesItInSoftware()
    {
        var run = await DecodeAsync(
            Av1Fixtures.Clip420,
            HardwareDecodeMode.Auto,
            excluded: ["av1"]
        );

        Assert.Null(run.Bound);
        Assert.Equal(Av1Fixtures.Clip420Frames, run.Frames);
    }

    [RequiresAv1HardwareFact]
    public async Task Required_ExcludingAv1FailsAsUnbound()
    {
        using var file = TempFile.With(Av1Fixtures.Clip420, ".ivf");
        await using var demux = await OpenAsync(file.Path);

        var ex = Assert.Throws<HardwareDecodeUnavailableException>(() =>
            Open(demux, HardwareDecodeMode.Required, null, ["av1"], null)
        );

        Assert.Contains("ExcludedCodecs", ex.Message);
    }

    // ── A stream the hardware refuses ────────────────────────────────────────

    [RequiresAv1HardwareRefusalFact]
    public async Task Auto_Av1TheHardwareRefuses_DecodesWithLibdav1d()
    {
        var backend = Av1Hardware.WorkingBackends[0];

        var run = await DecodeAsync(
            Av1Fixtures.Clip444,
            HardwareDecodeMode.Auto,
            preferred: [backend],
            only: backend
        );

        Assert.Equal(Av1Fixtures.Clip444Frames, run.Frames);
        Assert.Equal(backend, run.Bound);
        Assert.Null(run.Engaged);
        Assert.Null(run.BoundAfter);
    }

    [RequiresAv1HardwareRefusalFact]
    public async Task Required_Av1TheHardwareRefuses_Faults()
    {
        var backend = Av1Hardware.WorkingBackends[0];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DecodeAsync(
                Av1Fixtures.Clip444,
                HardwareDecodeMode.Required,
                preferred: [backend],
                only: backend
            )
        );

        Assert.Contains("avcodec_send_packet", ex.Message);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private sealed record Run(
        int Frames,
        int GpuFrames,
        HardwareDecodeBackendKind? Bound,
        HardwareDecodeBackendKind? BoundAfter,
        HardwareDecodeBackendKind? Engaged,
        byte[] First
    )
    {
        public double MeanDifference(Run other)
        {
            if (First.Length != other.First.Length)
                return double.MaxValue;
            long sum = 0;
            for (int i = 0; i < First.Length; i++)
                sum += Math.Abs(First[i] - other.First[i]);
            return (double)sum / First.Length;
        }
    }

    private async Task<Run> DecodeAsync(
        byte[] clip,
        HardwareDecodeMode mode,
        IReadOnlyList<HardwareDecodeBackendKind>? preferred = null,
        HardwareDecodeBackendKind? only = null,
        IReadOnlyList<string>? excluded = null,
        bool yieldHardwareFrames = false
    )
    {
        using var file = TempFile.With(clip, ".ivf");
        await using var demux = await OpenAsync(file.Path);
        await using var decoder = Open(demux, mode, preferred, excluded, only);
        decoder.YieldHardwareFrames = yieldHardwareFrames;
        var bound = decoder.BoundBackend;

        int frames = 0,
            gpu = 0;
        byte[] first = [];
        await QueueAllAsync(demux, decoder);
        await foreach (var frame in decoder.DecodeAsync())
        {
            // Released as they arrive: holding every frame of a hardware pool starves the decoder.
            using (frame)
            {
                if (frames == 0 && frame is CpuVideoFrame cpu && cpu.AsCpu() is { } data)
                    first = data.PlaneY.Span[..(data.StrideY * data.Height)].ToArray();
                if (frame is GpuVideoFrame)
                    gpu++;
                frames++;
            }
        }

        return new Run(frames, gpu, bound, decoder.BoundBackend, decoder.HardwareBackend, first);
    }

    private VideoDecoder Open(
        DemuxSession demux,
        HardwareDecodeMode mode,
        IReadOnlyList<HardwareDecodeBackendKind>? preferred,
        IReadOnlyList<string>? excluded,
        HardwareDecodeBackendKind? only
    )
    {
        var capabilities = only is { } backend
            ? new HardwareDecodeCapabilities(
                fixture.Capabilities.Available
                    .Where(b => b.Kind == backend && b.Initialized)
                    .ToList()
            )
            : fixture.Capabilities;

        return VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions
            {
                Mode = mode,
                PreferredBackends = preferred ?? [],
                ExcludedCodecs = excluded ?? [],
            },
            capabilities,
            loggerFactory: null
        );
    }

    /// <summary>Queues every video packet and completes the queue.</summary>
    private static async Task QueueAllAsync(DemuxSession demux, VideoDecoder decoder)
    {
        int streamIndex = demux.MediaInfo.VideoStreams[0].StreamIndex;
        nint read = FFAvCodec.av_packet_alloc();
        try
        {
            while (FFAvFormat.av_read_frame(demux.FormatContextPtr, read) >= 0)
            {
                if (new AvPacketAccessor(read).StreamIndex == streamIndex)
                {
                    nint clone = FFAvCodec.av_packet_alloc();
                    FFAvCodec.av_packet_ref(clone, read);
                    await decoder.SendPacketAsync(clone);
                }

                FFAvCodec.av_packet_unref(read);
            }
        }
        finally
        {
            FFAvCodec.av_packet_free(ref read);
        }

        decoder.CompletePacketQueue();
    }
}

/// <summary>Which backends decode the AV1 clips on this host, found by decoding them once.</summary>
internal static class Av1Hardware
{
    private static readonly Lazy<(IReadOnlyList<HardwareDecodeBackendKind> Works, bool RefusesFourFourFour)> Probed =
        new(Probe);

    /// <summary>Backends that decode the 4:2:0 clip on hardware here, in the platform order.</summary>
    public static IReadOnlyList<HardwareDecodeBackendKind> WorkingBackends => Probed.Value.Works;

    /// <summary>Whether the first working backend refuses the 4:4:4 clip, which is what the fallback test needs.</summary>
    public static bool RefusesFourFourFour => Probed.Value.RefusesFourFourFour;

    /// <summary>The reason nothing here decodes AV1 on hardware, or null when something does.</summary>
    public static string? WhyNot()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
            return "FFmpeg shared libraries not available.";
        return WorkingBackends.Count == 0
            ? "No initialised backend decodes the AV1 clip on hardware here."
            : null;
    }

    private static (IReadOnlyList<HardwareDecodeBackendKind>, bool) Probe()
    {
        var caps = FfmpegBootstrapFixture.ReadCapabilities();
        var works = new List<HardwareDecodeBackendKind>();
        foreach (var backend in caps.Available.Where(b => b.Initialized).Select(b => b.Kind).Distinct())
        {
            if (Task.Run(() => TryDecode(Av1Fixtures.Clip420, backend, caps)).GetAwaiter().GetResult() is true)
                works.Add(backend);
        }

        bool refuses =
            works.Count > 0
            && Task.Run(() => TryDecode(Av1Fixtures.Clip444, works[0], caps)).GetAwaiter().GetResult() is not true;
        return (works, refuses);
    }

    // True when Required on one backend decodes the clip on hardware.
    private static async Task<bool?> TryDecode(
        byte[] clip,
        HardwareDecodeBackendKind backend,
        HardwareDecodeCapabilities caps
    )
    {
        using var file = TempFile.With(clip, ".ivf");
        try
        {
            await using var demux = await OpenAsync(file.Path);
            await using var decoder = VideoDecoder.Open(
                demux.FormatContextPtr,
                demux.MediaInfo.VideoStreams[0].StreamIndex,
                new HardwareDecodeOptions
                {
                    Mode = HardwareDecodeMode.Required,
                    PreferredBackends = [backend],
                },
                new HardwareDecodeCapabilities(
                    caps.Available.Where(b => b.Kind == backend && b.Initialized).ToList()
                ),
                loggerFactory: null
            );
            using var frames = await DecodeAllAsync(demux, decoder);
            return frames.Count > 0 && decoder.HardwareBackend == backend;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Skipped unless FFmpeg has a software AV1 decoder other than the native one.</summary>
internal sealed class RequiresAv1SoftwareFactAttribute : FactAttribute
{
    public RequiresAv1SoftwareFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        _ = FfmpegBootstrapFixture.ReadCapabilities();
        nint codec = FFAvCodec.avcodec_find_decoder((int)AVCodecID.AV_CODEC_ID_AV1);
        if (codec == nint.Zero || FFAvCodec.avcodec_find_decoder_by_name("libdav1d") == nint.Zero)
            Skip = "This FFmpeg build has no libdav1d, so AV1 has no software decoder.";
    }
}

/// <summary>Skipped unless a backend here decodes the AV1 clip on hardware, and a software AV1 decoder exists.</summary>
internal sealed class RequiresAv1HardwareFactAttribute : FactAttribute
{
    public RequiresAv1HardwareFactAttribute()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
        {
            Skip = "FFmpeg shared libraries not available.";
            return;
        }

        Skip = Av1Hardware.WhyNot();
        if (Skip is null && FFAvCodec.avcodec_find_decoder_by_name("libdav1d") == nint.Zero)
            Skip = "This FFmpeg build has no libdav1d, so AV1 has no software decoder to compare with.";
    }
}

/// <summary>As the hardware fact, and skipped where the working backend decodes the 4:4:4 clip too.</summary>
internal sealed class RequiresAv1HardwareRefusalFactAttribute : FactAttribute
{
    public RequiresAv1HardwareRefusalFactAttribute()
    {
        Skip = new RequiresAv1HardwareFactAttribute().Skip;
        if (Skip is null && !Av1Hardware.RefusesFourFourFour)
            Skip = "The backend decodes 4:4:4 AV1 on hardware here, so there is no refusal to fall back from.";
    }
}
