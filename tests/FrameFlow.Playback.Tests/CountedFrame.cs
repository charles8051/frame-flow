namespace FrameFlow.Playback.Tests;

/// <summary>
/// A frame that counts its references, for a test that asserts who released it (#701).
/// </summary>
/// <remarks>
/// A release past the last reference is counted rather than absorbed: the count goes below zero,
/// so a frame released twice fails an assertion that it was released once, where a frame that
/// only records "disposed" would pass it.
/// </remarks>
internal sealed class CountedFrame(TimeSpan pts = default, int width = 320, int height = 240) : IVideoFrame
{
    private int _references = 1;

    /// <summary>
    /// The references still held: 1 as created, 0 once released exactly once, and below 0 for
    /// each release past the last.
    /// </summary>
    public int References => Volatile.Read(ref _references);

    public int Width => width;
    public int Height => height;
    public TimeSpan Pts => pts;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
    public PixelFormat Format => PixelFormat.Bgra32;
    public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

    public IVideoFrame AddRef()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public void Dispose() => Interlocked.Decrement(ref _references);

    public CpuFrameData? AsCpu() => null;

    public CpuFrameData ToCpu() => throw new NotSupportedException();
}
