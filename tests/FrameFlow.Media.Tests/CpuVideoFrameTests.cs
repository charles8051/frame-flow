using System.Runtime.CompilerServices;
using FrameFlow.Media.Tests.Doubles;

namespace FrameFlow.Media.Tests;

/// <summary>
/// <see cref="CpuVideoFrame.Create{TState}"/>: the one way to make a CPU frame (ADR-0080,
/// decision 5). It lays the planes out from the format, runs the fill once, and publishes a frame
/// nothing can write to afterwards.
/// </summary>
public sealed class CpuVideoFrameTests
{
    private static CpuVideoFrame Bgra(
        int width = 4,
        int height = 2,
        TimeSpan pts = default,
        TimeSpan duration = default,
        CountingArrayPool<byte>? pool = null
    ) =>
        CpuVideoFrame.Create(
            PixelFormat.Bgra32,
            width,
            height,
            pts,
            duration,
            0,
            static (planes, _) =>
            {
                for (int i = 0; i < planes.Y.Length; i++)
                    planes.Y[i] = (byte)i;
            },
            pool
        );

    // ── Metadata ──────────────────────────────────────────────────────

    [Fact]
    public void Create_StoresTheFramesMetadata()
    {
        using var frame = Bgra(
            width: 6,
            height: 3,
            pts: TimeSpan.FromSeconds(3.5),
            duration: TimeSpan.FromMilliseconds(40)
        );

        Assert.Equal(6, frame.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(PixelFormat.Bgra32, frame.Format);
        Assert.Equal(TimeSpan.FromSeconds(3.5), frame.PresentationTime);
        Assert.Equal(TimeSpan.FromSeconds(3.5), frame.Pts);
        Assert.Equal(TimeSpan.FromMilliseconds(40), frame.Duration);
        Assert.Equal(24, frame.Stride);
    }

    [Fact]
    public void Create_AcceptsANegativePresentationTime()
    {
        using var frame = Bgra(pts: TimeSpan.FromMilliseconds(-100));
        Assert.Equal(TimeSpan.FromMilliseconds(-100), frame.PresentationTime);
    }

    [Fact]
    public void Create_AcceptsAZeroSizedFrame()
    {
        using var frame = Bgra(width: 0, height: 0);

        var cpu = frame.ToCpu();
        Assert.True(cpu.PlaneY.IsEmpty);
    }

    // ── Layout ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PixelFormat.Bgra32, 5, 3, 20, 0, 0, 0, 0)]
    [InlineData(PixelFormat.Rgba32, 5, 3, 20, 0, 0, 0, 0)]
    [InlineData(PixelFormat.Yuyv422, 5, 3, 12, 0, 0, 0, 0)]
    [InlineData(PixelFormat.Uyvy422, 4, 3, 8, 0, 0, 0, 0)]
    [InlineData(PixelFormat.Yuv420P, 5, 3, 5, 3, 2, 3, 2)]
    [InlineData(PixelFormat.Nv12, 5, 3, 5, 6, 2, 0, 0)]
    public void Create_LaysOutEachPlaneForTheFormat(
        PixelFormat format,
        int width,
        int height,
        int strideY,
        int strideU,
        int rowsU,
        int strideV,
        int rowsV
    )
    {
        using var frame = CpuVideoFrame.Create(
            format,
            width,
            height,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            static (_, _) => { }
        );

        var cpu = frame.ToCpu();
        Assert.Equal(strideY, cpu.StrideY);
        Assert.Equal(strideY * height, cpu.PlaneY.Length);
        Assert.Equal(strideU, cpu.StrideU);
        Assert.Equal(strideU * rowsU, cpu.PlaneU.Length);
        Assert.Equal(strideV, cpu.StrideV);
        Assert.Equal(strideV * rowsV, cpu.PlaneV.Length);
        Assert.Equal(width, cpu.Width);
        Assert.Equal(height, cpu.Height);
    }

    [Fact]
    public void AsCpu_ReturnsWhatTheFillWroteToEachPlane()
    {
        using var frame = CpuVideoFrame.Create(
            PixelFormat.Yuv420P,
            4,
            2,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            static (planes, _) =>
            {
                planes.Y.Fill(1);
                planes.U.Fill(2);
                planes.V.Fill(3);
            }
        );

        var cpu = frame.ToCpu();
        Assert.All(cpu.PlaneY.ToArray(), b => Assert.Equal(1, b));
        Assert.All(cpu.PlaneU.ToArray(), b => Assert.Equal(2, b));
        Assert.All(cpu.PlaneV.ToArray(), b => Assert.Equal(3, b));
    }

    [Fact]
    public void Create_GivesTheFillTheFramesShape()
    {
        var seen = new StrongBox<(int, int, PixelFormat, int)>();
        using var frame = CpuVideoFrame.Create(
            PixelFormat.Nv12,
            6,
            4,
            TimeSpan.Zero,
            TimeSpan.Zero,
            seen,
            static (planes, box) =>
                box.Value = (planes.Width, planes.Height, planes.Format, planes.StrideY)
        );

        Assert.Equal((6, 4, PixelFormat.Nv12, 6), seen.Value);
    }

    [Fact]
    public void Create_RejectsNegativeDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Bgra(width: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bgra(height: -1));
    }

    [Fact]
    public void Create_RejectsAnUnknownFormat()
    {
        Assert.Throws<ArgumentException>(() =>
            CpuVideoFrame.Create(
                (PixelFormat)999,
                2,
                2,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                static (_, _) => { }
            )
        );
    }

    [Fact]
    public void Create_RejectsAFrameTooLargeForOneArray()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Bgra(width: 65_536, height: 65_536));
    }

    // ── Storage ───────────────────────────────────────────────────────

    [Fact]
    public void TheFinalRelease_ReturnsTheStorageOnce()
    {
        var pool = new CountingArrayPool<byte>();
        var frame = Bgra(pool: pool);
        var shared = frame.AddRef();

        frame.Dispose();
        Assert.Equal(0, pool.Returns);

        shared.Dispose();
        Assert.Equal(1, pool.Rents);
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void AfterTheFinalRelease_AsCpuIsNullAndToCpuThrows()
    {
        var frame = Bgra();

        frame.Dispose();

        Assert.Null(frame.AsCpu());
        Assert.Throws<ObjectDisposedException>(() => frame.ToCpu());
    }

    [Fact]
    public void AFillThatThrows_ReturnsTheStorage_AndPropagatesItsException()
    {
        var pool = new CountingArrayPool<byte>();
        var thrown = new InvalidOperationException("fill failed");

        var caught = Assert.Throws<InvalidOperationException>(() =>
            CpuVideoFrame.Create(
                PixelFormat.Bgra32,
                2,
                2,
                TimeSpan.Zero,
                TimeSpan.Zero,
                thrown,
                static (_, ex) => throw ex,
                pool
            )
        );

        Assert.Same(thrown, caught);
        Assert.Equal(1, pool.Rents);
        Assert.Equal(1, pool.Returns);
    }

#if DEBUG
    [Fact]
    public void ReleasedStorage_ReadsAsTheReleaseFill_ThroughAViewKeptPastTheRelease()
    {
        var pool = new CountingArrayPool<byte>();
        var frame = Bgra(pool: pool);
        var kept = frame.ToCpu().PlaneY;
        Assert.Equal(1, kept.Span[1]);

        frame.Dispose();

        Assert.All(kept.ToArray(), b => Assert.Equal(CpuVideoFrame.ReleasedFill, b));
    }
#endif
}
