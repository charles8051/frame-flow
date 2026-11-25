using System.Runtime.InteropServices;
using FrameFlow.Decoding;
using FrameFlow.Native;
using FrameFlow.Native.Interop;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// A pinned <c>h264_nvenc</c> writes a short MP4 that reopens as H.264 (#865). Skipped unless
/// the loaded FFmpeg carries the encoder, a CUDA device initialised here, and NVENC's own
/// driver library loads, so it runs only on a host with an NVIDIA GPU and its drivers.
/// </summary>
/// <remarks>
/// The default resolution order never picks NVENC, so without a pin nothing exercises it. A
/// GPU without an NVENC engine passes the gate and fails the test, which is the answer such a
/// host should get for a pinned hardware encoder.
/// </remarks>
public sealed class NvencMp4Tests : IClassFixture<FfmpegBootstrapFixture>
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 30;
    private const int FrameCount = 30;

    public NvencMp4Tests(FfmpegBootstrapFixture _) { }

    [RequiresNvencFact]
    public async Task PinnedNvenc_WritesAnH264Mp4()
    {
        var path = Path.Combine(Path.GetTempPath(), $"frameflow-nvenc-{Guid.NewGuid():N}.mp4");
        try
        {
            var options = new H264EncoderOptions
            {
                EncoderName = "h264_nvenc",
                FrameRateNumerator = Fps,
                FrameRateDenominator = 1,
                BitRate = 1_000_000,
                GopSize = Fps,
            };

            await using (var writer = Mp4VideoWriter.Create(path, options))
            {
                for (int i = 0; i < FrameCount; i++)
                    await writer.WriteAsync(SyntheticFrames.CreateBgra(Width, Height, i, Fps));
                await writer.CompleteAsync();

                Assert.Equal("h264_nvenc", writer.EncoderInfo.CodecName);
            }

            Assert.True(new FileInfo(path).Length > 0, "Output MP4 should be non-empty.");

            await using var session = await new DemuxSessionFactory().OpenAsync(MediaSource.FromFile(path));
            var video = Assert.Single(session.MediaInfo.VideoStreams);
            Assert.Equal("h264", video.CodecName);
            Assert.Equal(Width, video.Width);
            Assert.Equal(Height, video.Height);

            int packets = 0;
            DemuxPacket? first = null;
            DemuxPacket? packet;
            while ((packet = await session.ReadPacketAsync()) is not null)
            {
                if (packet.StreamIndex != video.StreamIndex)
                    continue;
                first ??= packet;
                packets++;
            }

            Assert.True(packets > 0, "Expected at least one video packet.");
            Assert.True(first!.IsKeyFrame, "The first video packet should be a keyframe.");
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}

/// <summary>
/// Skipped unless <c>h264_nvenc</c> can plausibly open here: FFmpeg is loaded and carries it, a
/// CUDA device initialised, and NVENC's driver library loads. Off NVIDIA hardware it skips.
/// </summary>
internal sealed class RequiresNvencFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(WhyNvencCannotRun);

    public RequiresNvencFactAttribute() => Skip = Reason.Value;

    private static string? WhyNvencCannotRun()
    {
        if (!TestEnvironment.HasFfmpegSharedLibraries)
            return "FFmpeg shared libraries not available.";

        if (OperatingSystem.IsMacOS())
            return "NVENC does not exist on macOS.";

        if (!new FfmpegBootstrapFixture().IsBootstrapped)
            return "FFmpeg did not bootstrap.";

        if (FFAvCodec.avcodec_find_encoder_by_name("h264_nvenc") == nint.Zero)
            return "The loaded FFmpeg build has no h264_nvenc encoder.";

        var capabilities = HardwareDecodeProbe.GetOrRun(logger: null, probeUncatalogued: false, out _);
        if (!capabilities.Available.Any(b => b.Kind == HardwareDecodeBackendKind.Cuda && b.Initialized))
            return "No CUDA device initialised on this machine.";

        // nv-codec-headers binds NVENC from its own driver library, separate from libcuda.
        string library = OperatingSystem.IsWindows()
            ? (Environment.Is64BitProcess ? "nvEncodeAPI64.dll" : "nvEncodeAPI.dll")
            : "libnvidia-encode.so.1";
        if (!NativeLibrary.TryLoad(library, out var handle))
            return $"NVENC's driver library '{library}' is not loadable on this host.";
        NativeLibrary.Free(handle);

        return null;
    }
}
