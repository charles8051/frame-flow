using FrameFlow.Native;
using FrameFlow.Native.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Native.Tests;

/// <summary>
/// Lock-in tests for <see cref="FFAvUtil.av_rescale_q"/> — verifies that the
/// struct-by-value binding is ABI-correct on x64 and returns valid results, not
/// <see cref="FFAvUtil.AvNoPtsValue"/> (which was the symptom of the broken
/// 4-int binding that preceded this fix).
/// </summary>
public sealed class AvRescaleQTests
{
    private static bool TryBootstrap()
    {
        var ffmpegDir = TestEnvironment.FindFfmpegLibraryDirectory();
        if (ffmpegDir is null)
            return false;
        var opts = new FrameFlowNativeOptions { CustomFfmpegPath = ffmpegDir };
        var bootstrapper = new FrameFlowBootstrapper(opts, NullLoggerFactory.Instance);
        return bootstrapper.Initialize().IsSuccess;
    }

    /// <summary>
    /// av_rescale_q(1, {1,30}, {1,15360}) = 1 * 1 * 15360 / (30 * 1) = 512.
    /// The old 4-int binding returned AV_NOPTS_VALUE here due to ABI mismatch.
    /// </summary>
    [RequiresFfmpegFact]
    public void AvRescaleQ_FrameToAudioTimeBase_ReturnsCorrectValue()
    {
        if (!TryBootstrap())
            return;

        long result = FFAvUtil.av_rescale_q(
            a: 1,
            bq: new AvRational(1, 30),       // source: 30 fps
            cq: new AvRational(1, 15360)     // dest: 15360 Hz (common audio sample rate multiple)
        );

        Assert.NotEqual(FFAvUtil.AvNoPtsValue, result);
        Assert.Equal(512L, result); // 15360 / 30 = 512
    }

    /// <summary>
    /// av_rescale_q(90000, {1,90000}, {1,1000}) = 90000 * 1000 / 90000 = 1000.
    /// Exercises a different magnitude to rule out coincidental correctness.
    /// </summary>
    [RequiresFfmpegFact]
    public void AvRescaleQ_PtsRescaleToMilliseconds_ReturnsCorrectValue()
    {
        if (!TryBootstrap())
            return;

        long result = FFAvUtil.av_rescale_q(
            a: 90000,
            bq: new AvRational(1, 90000),    // source: 90 kHz MPEG time base
            cq: new AvRational(1, 1000)      // dest: milliseconds
        );

        Assert.NotEqual(FFAvUtil.AvNoPtsValue, result);
        Assert.Equal(1000L, result); // 90000 * 1000 / 90000 = 1000
    }
}
