using FrameFlow.Media;
using FrameFlow.Native;

namespace FrameFlow.Native.Tests;

public sealed class FrameFlowBootstrapResultTests
{
    [Fact]
    public void Record_RoundTripsAllProperties()
    {
        var result = new FrameFlowBootstrapResult(
            IsSuccess: true,
            ResolvedPath: "/usr/lib",
            BinarySource: FfmpegBinarySource.System,
            Message: "OK"
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("/usr/lib", result.ResolvedPath);
        Assert.Equal(FfmpegBinarySource.System, result.BinarySource);
        Assert.Equal("OK", result.Message);
    }

    /// <summary>
    /// `ResolvedPath = null` is the explicit "bundled / no path" sentinel
    /// — pins the nullability contract.
    /// </summary>
    [Fact]
    public void Record_ResolvedPath_CanBeNull()
    {
        var result = new FrameFlowBootstrapResult(
            IsSuccess: true,
            ResolvedPath: null,
            BinarySource: FfmpegBinarySource.Bundled,
            Message: "Bundled"
        );

        Assert.Null(result.ResolvedPath);
    }

    /// <summary>
    /// Compiler-generated record equality. One representative test
    /// covers both equal and unequal cases.
    /// </summary>
    [Fact]
    public void Record_HasValueEquality()
    {
        var a = new FrameFlowBootstrapResult(true, "/path", FfmpegBinarySource.CustomPath, "msg");
        var b = new FrameFlowBootstrapResult(true, "/path", FfmpegBinarySource.CustomPath, "msg");
        var different = new FrameFlowBootstrapResult(false, "/path", FfmpegBinarySource.CustomPath, "msg");

        Assert.Equal(a, b);
        Assert.NotEqual(a, different);
    }

    /// <summary>
    /// Member-count check guards against accidental additions / removals.
    /// </summary>
    [Fact]
    public void FfmpegBinarySource_HasExpectedMemberCount()
    {
        var values = Enum.GetValues<FfmpegBinarySource>();
        Assert.Equal(4, values.Length);
    }
}
