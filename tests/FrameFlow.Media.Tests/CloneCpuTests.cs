using FrameFlow.Graph;

namespace FrameFlow.Media.Tests;

/// <summary>
/// <see cref="VideoFrameExtensions.CloneCpu"/> copies every plane, so a planar frame survives a
/// clone byte for byte (#379).
/// </summary>
public sealed class CloneCpuTests
{
    [Theory]
    [InlineData(PixelFormat.Bgra32, 5, 3)]
    [InlineData(PixelFormat.Yuv420P, 5, 3)]
    [InlineData(PixelFormat.Nv12, 5, 3)]
    [InlineData(PixelFormat.Yuyv422, 5, 3)]
    public void AClone_MatchesItsSource_PlaneForPlane(PixelFormat format, int width, int height)
    {
        using var source = CpuVideoFrame.Create(
            format,
            width,
            height,
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(20),
            0,
            static (planes, _) =>
            {
                Pattern(planes.Y, 1);
                Pattern(planes.U, 101);
                Pattern(planes.V, 201);
            }
        );

        using var clone = source.CloneCpu();

        Assert.NotSame(source, clone);
        Assert.Equal(format, clone.Format);
        Assert.Equal(width, clone.Width);
        Assert.Equal(height, clone.Height);
        Assert.Equal(source.Pts, clone.Pts);
        Assert.Equal(source.Duration, clone.Duration);

        var expected = source.ToCpu();
        var actual = clone.ToCpu();
        Assert.Equal(expected.PlaneY.ToArray(), actual.PlaneY.ToArray());
        Assert.Equal(expected.PlaneU.ToArray(), actual.PlaneU.ToArray());
        Assert.Equal(expected.PlaneV.ToArray(), actual.PlaneV.ToArray());
        Assert.Equal(expected.StrideU, actual.StrideU);
    }

    [Fact]
    public void AClone_OutlivesItsSource()
    {
        var source = CpuVideoFrame.Create(
            PixelFormat.Nv12,
            4,
            2,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            static (planes, _) =>
            {
                Pattern(planes.Y, 1);
                Pattern(planes.U, 101);
            }
        );
        byte[] y = source.ToCpu().PlaneY.ToArray();
        byte[] uv = source.ToCpu().PlaneU.ToArray();

        using var clone = source.CloneCpu();
        source.Dispose();

        Assert.Equal(y, clone.ToCpu().PlaneY.ToArray());
        Assert.Equal(uv, clone.ToCpu().PlaneU.ToArray());
    }

    [Fact]
    public void ASourceWithPaddedRows_ClonesToTightRows()
    {
        // 2 x 2 NV12 whose rows carry 3 bytes of padding in each plane.
        var source = new ViewFrame(
            PixelFormat.Nv12,
            width: 2,
            height: 2,
            new CpuFrameData(
                PlaneY: new byte[] { 1, 2, 0, 0, 0, 3, 4 },
                PlaneU: new byte[] { 5, 6 },
                PlaneV: ReadOnlyMemory<byte>.Empty,
                StrideY: 5,
                StrideU: 5,
                StrideV: 0,
                Width: 2,
                Height: 2
            )
        );

        using var clone = source.CloneCpu();

        var cpu = clone.ToCpu();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, cpu.PlaneY.ToArray());
        Assert.Equal(2, cpu.StrideY);
        Assert.Equal(new byte[] { 5, 6 }, cpu.PlaneU.ToArray());
    }

    [Fact]
    public void ASourceMissingAPlane_Throws()
    {
        // Yuv420P needs a U and a V plane; this view has only Y.
        var source = new ViewFrame(
            PixelFormat.Yuv420P,
            width: 2,
            height: 2,
            new CpuFrameData(
                PlaneY: new byte[4],
                PlaneU: ReadOnlyMemory<byte>.Empty,
                PlaneV: ReadOnlyMemory<byte>.Empty,
                StrideY: 2,
                StrideU: 0,
                StrideV: 0,
                Width: 2,
                Height: 2
            )
        );

        Assert.Throws<InvalidOperationException>(() => source.CloneCpu());
    }

    [Fact]
    public void ASourceWithNoCpuView_Throws()
    {
        var source = new ViewFrame(PixelFormat.Bgra32, 2, 2, view: null);

        Assert.Throws<InvalidOperationException>(() => source.CloneCpu());
    }

    private static void Pattern(Span<byte> plane, int seed)
    {
        for (int i = 0; i < plane.Length; i++)
            plane[i] = (byte)(seed + i);
    }

    /// <summary>A frame that reports a fixed CPU view, padding and all.</summary>
    private sealed class ViewFrame(PixelFormat format, int width, int height, CpuFrameData? view)
        : IVideoFrame
    {
        public int Width => width;
        public int Height => height;
        public TimeSpan Pts => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public PixelFormat Format => format;
        public FrameMemoryDomain MemoryDomain => FrameMemoryDomain.Cpu;

        public IVideoFrame AddRef() => this;

        public void Dispose() { }

        public CpuFrameData? AsCpu() => view;

        public CpuFrameData ToCpu() => view ?? throw new NotSupportedException();
    }
}
