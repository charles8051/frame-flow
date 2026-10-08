namespace FrameFlow.Decoding.Tests;

/// <summary>
/// <see cref="VideoDecoder.BackendOf"/> maps the raw <c>_boundBackend</c> value to a backend
/// (#579). <c>BoundBackend</c> reads the field once and passes it here, because the first-packet
/// fallback can clear it after <c>Open</c> and a property that compared and then cast would see
/// the sentinel as a backend.
/// </summary>
/// <remarks>
/// The race itself is not reproducible without a seam between two reads. What is pinned is the
/// mapping the single read feeds: the sentinel is null and no sentinel value reaches the cast.
/// </remarks>
public sealed class BoundBackendTests
{
    private const int NoHardwareBackend = -1;

    [Fact]
    public void TheSoftwareSentinelIsNoBackend() =>
        Assert.Null(VideoDecoder.BackendOf(NoHardwareBackend));

    [Fact]
    public void EveryBackendMapsToItself()
    {
        foreach (var kind in Enum.GetValues<HardwareDecodeBackendKind>())
            Assert.Equal(kind, VideoDecoder.BackendOf((int)kind));
    }

    [Fact]
    public void NoBackendIsNumberedLikeTheSentinel()
    {
        Assert.DoesNotContain(
            Enum.GetValues<HardwareDecodeBackendKind>(),
            kind => (int)kind == NoHardwareBackend
        );
    }
}
