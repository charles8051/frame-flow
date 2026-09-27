using FrameFlow.Inference.Dml.Interop;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>Which C API version the device-bound session asks a runtime for. Pure.</summary>
public sealed class OrtDmlApiTests
{
    [Theory]
    [InlineData("1.24.4", 24u)]
    [InlineData("1.30.0", 30u)]
    public void TheApiVersion_IsTheRuntimesOwnMinor(string runtime, uint expected) =>
        Assert.Equal(expected, OrtDmlApi.ApiVersionOf(runtime));

    [Theory]
    [InlineData("1.20.1")]
    [InlineData("2.0.0")]
    [InlineData("1")]
    [InlineData("one.two")]
    [InlineData("")]
    public void ARuntimeItCannotUse_IsRefusedByName(string runtime)
    {
        var error = Assert.Throws<NotSupportedException>(() => OrtDmlApi.ApiVersionOf(runtime));
        Assert.Contains(runtime, error.Message);
    }
}
