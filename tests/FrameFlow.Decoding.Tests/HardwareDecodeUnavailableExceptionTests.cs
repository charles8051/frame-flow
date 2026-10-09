namespace FrameFlow.Decoding.Tests;

/// <summary>What <see cref="HardwareDecodeUnavailableException"/> says for each reason it is raised.</summary>
public sealed class HardwareDecodeUnavailableExceptionTests
{
    [Fact]
    public void NoAttemptsSaysNoBackendIsAvailable()
    {
        var ex = new HardwareDecodeUnavailableException(27, "h264", []);

        Assert.Contains("no hardware-decode backend is available for codec 'h264'", ex.Message);
    }

    [Fact]
    public void AttemptsAreNamedWithTheirReasons()
    {
        var ex = new HardwareDecodeUnavailableException(
            27,
            "h264",
            [new HardwareDecodeAttempt(HardwareDecodeBackendKind.Cuda, "av_hwdevice_ctx_create returned -1")]
        );

        Assert.Contains("Cuda -> av_hwdevice_ctx_create returned -1", ex.Message);
    }

    [Fact]
    public void AGivenReasonReplacesTheGenericMessageAndKeepsTheFields()
    {
        var attempts = new[] { new HardwareDecodeAttempt(HardwareDecodeBackendKind.Cuda, "x") };

        var ex = new HardwareDecodeUnavailableException(
            1,
            "av1",
            attempts,
            reason: "codec 'av1' is in HardwareDecodeOptions.ExcludedCodecs"
        );

        Assert.Equal(
            "HardwareDecodeMode.Required: codec 'av1' is in HardwareDecodeOptions.ExcludedCodecs",
            ex.Message
        );
        Assert.Equal(1, ex.CodecId);
        Assert.Equal("av1", ex.CodecName);
        Assert.Same(attempts, ex.Attempts);
    }
}
