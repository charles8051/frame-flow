using FrameFlow.Decoding;
using FrameFlow.Inference.Core;
using FrameFlow.Inference.D3D12.Core;
using FrameFlow.Media;
using Vortice.Direct3D;
using Vortice.Direct3D12;

namespace FrameFlow.Inference.D3D12.Tests;

/// <summary>
/// What <see cref="D3D12ImageToTensor"/> writes for D3D12VA frames. Each tensor is checked value
/// by value against <see cref="Reference"/>, which does the shader's arithmetic on the CPU from
/// the frame's own NV12 samples, and against the CPU <see cref="ImageToTensor"/> stage.
/// </summary>
public sealed class D3D12ImageToTensorTests
{
    // 320x240 H.264, 72 frames.
    private const string Clip = "test-video-h264-yuv420p.mp4";

    /// <summary>
    /// Each configuration exercises a different part of the shader. The nearest ones use scales
    /// that never place a sample exactly on a pixel boundary, where float rounding could pick
    /// either neighbour.
    /// </summary>
    private static readonly Dictionary<string, (ImageToTensorOptions Options, RotatedRect Crop)> Configurations = new()
    {
        ["stretch, nearest, NCHW RGB, BT.601 limited"] = (
            new ImageToTensorOptions(64, 48) { Sampling = ImageSampling.Nearest },
            RotatedRect.FromBounds(0, 0, 320, 240)),
        ["letterbox, bilinear, NHWC BGR, mean/std, pad"] = (
            new ImageToTensorOptions(64, 64)
            {
                Fit = ImageFit.Letterbox,
                Layout = TensorLayout.Nhwc,
                ChannelOrder = TensorChannelOrder.Bgr,
                Normalization = TensorNormalization.MeanStd((0.485f, 0.456f, 0.406f), (0.229f, 0.224f, 0.225f)),
                PadValue = 114,
            },
            RotatedRect.FromBounds(0, 0, 320, 240)),
        ["rotated crop, bilinear, [-1, 1], BT.709 full"] = (
            new ImageToTensorOptions(48, 48)
            {
                Normalization = TensorNormalization.MinusOneToOne,
                YuvMatrix = YuvMatrix.Bt709,
                YuvRange = YuvRange.Full,
            },
            new RotatedRect(160, 120, 150, 100, 0.3f)),
        ["crop past the edges, nearest"] = (
            new ImageToTensorOptions(48, 36) { Sampling = ImageSampling.Nearest },
            RotatedRect.FromBounds(-20, -10, 120, 90)),
    };

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task EveryConfiguration_WritesTheReferenceTensor()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 3);
        try
        {
            using var gpu = new DeviceAndQueue((GpuVideoFrame)frames[0]);
            foreach (var (name, (options, crop)) in Configurations)
            {
                using var stage = gpu.Stage(options);
                foreach (var frame in frames.Cast<GpuVideoFrame>())
                {
                    stage.Write(frame, crop);
                    float[] written = stage.ReadBack();
                    float[] expected = Reference(Nv12Image.Read(frame), Constants(options, crop, frame), options.ElementCount);
                    AssertClose(expected, written, 1e-3f, name);
                }
            }
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task ItMatchesTheCpuStage_AndReturnsItsTransform()
    {
        var gpuFrames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 5);
        var cpuFrames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: false, count: 5);
        try
        {
            var options = new ImageToTensorOptions(64, 48) { Sampling = ImageSampling.Nearest };
            var crop = RotatedRect.FromBounds(0, 0, 320, 240);
            using var gpu = new DeviceAndQueue((GpuVideoFrame)gpuFrames[0]);
            using var stage = gpu.Stage(options);
            var cpuTensor = new float[options.ElementCount];
            var differences = new List<float>();
            for (int i = 0; i < gpuFrames.Count; i++)
            {
                var onGpu = stage.Write((GpuVideoFrame)gpuFrames[i], crop);
                float[] written = stage.ReadBack();
                var onCpu = ImageToTensor.Write(cpuFrames[i], crop, options, cpuTensor);

                Assert.Equal(onCpu.TensorToFrame, onGpu.TensorToFrame);
                for (int j = 0; j < written.Length; j++)
                    differences.Add(MathF.Abs(written[j] - cpuTensor[j]));
            }

            // The CPU stage reads swscale's BGRA, quantised to 8 bits, and its chroma upsampling
            // differs from the shader's nearest block on colour edges: about 2% of values here,
            // which is why the tail is not checked. Measured on this clip: mean 1.7/255 and p95
            // 2.0/255; with the wrong matrix (BT.709) they are 9.4/255 and 38.6/255.
            differences.Sort();
            float mean = differences.Average();
            float p95 = differences[(int)(0.95 * (differences.Count - 1))];
            Assert.True(mean < 4f / 255, $"mean |difference| {mean * 255:F2}/255");
            Assert.True(p95 < 8f / 255, $"p95 |difference| {p95 * 255:F2}/255");
        }
        finally
        {
            foreach (var frame in gpuFrames.Concat(cpuFrames))
                frame.Dispose();
        }
    }

    /// <summary>
    /// More writes than the stage keeps in flight, each frame disposed as soon as it is written:
    /// the stage holds each frame until the GPU has read it, and the tensor is the last frame's.
    /// </summary>
    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D12Va, Clip)]
    public async Task WritesPastTheInFlightLimit_WithFramesDisposedAtOnce_LeaveTheLastFramesTensor()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D12Va, yieldHardware: true, count: 10);
        var options = new ImageToTensorOptions(64, 48) { Sampling = ImageSampling.Nearest };
        var crop = RotatedRect.FromBounds(0, 0, 320, 240);
        var last = (GpuVideoFrame)frames[^1];
        var expected = Reference(Nv12Image.Read(last), Constants(options, crop, last), options.ElementCount);

        using var gpu = new DeviceAndQueue((GpuVideoFrame)frames[0]);
        using var stage = gpu.Stage(options);
        foreach (var frame in frames.Cast<GpuVideoFrame>())
        {
            stage.Write(frame, crop);
            frame.Dispose();
        }

        stage.WaitForCompletion();
        Assert.Equal((ulong)frames.Count, stage.CompletionValue);
        AssertClose(expected, stage.ReadBack(), 1e-3f, "the last frame");
    }

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D11Va, Clip)]
    public async Task AFrameWithoutAD3D12Texture_IsRefused()
    {
        var frames = await Decode.FramesAsync(Clip, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
        try
        {
            Vortice.Direct3D12.D3D12.D3D12CreateDevice(null, FeatureLevel.Level_11_0, out ID3D12Device? device).CheckError();
            using (device)
            using (var queue = device!.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute)))
            using (var stage = new D3D12ImageToTensor(device.NativePointer, queue.NativePointer, new ImageToTensorOptions(8, 8)))
            {
                var error = Assert.Throws<ArgumentException>(
                    () => stage.Write((GpuVideoFrame)frames[0], RotatedRect.FromBounds(0, 0, 320, 240)));
                Assert.Contains("D3D11Va", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            frames[0].Dispose();
        }
    }

    internal static KernelConstants Constants(
        ImageToTensorOptions options, RotatedRect crop, IVideoFrame frame) =>
        KernelConstants.Create(
            ImageToTensorPlan.Create(crop, options.Width, options.Height, options.Fit),
            options, YuvSamples.Nv12, frame.Width, frame.Height);

    /// <summary>
    /// The shader's arithmetic on the CPU, from the frame's NV12 samples and the same constants.
    /// </summary>
    internal static float[] Reference(Nv12Image image, KernelConstants k, int elementCount)
    {
        var output = new float[elementCount];
        int width = (int)k.TensorWidth, height = (int)k.TensorHeight, plane = width * height;
        bool bilinear = k.Bilinear != 0;
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                float px = tx + 0.5f, py = ty + 0.5f;
                float r, g, b;
                if (px < k.FitLeft || px >= k.FitRight || py < k.FitTop || py >= k.FitBottom)
                {
                    (r, g, b) = (k.PadRed, k.PadGreen, k.PadBlue);
                }
                else
                {
                    float x = k.A * px + k.B * py + k.C, y = k.D * px + k.E * py + k.F;
                    float luma = bilinear ? BilinearLuma(image, x, y) : NearestLuma(image, x, y);
                    var (cb, cr) = bilinear ? BilinearChroma(image, x, y) : NearestChroma(image, x, y);
                    float yl = (luma - k.YOffset) * k.YScale;
                    float u = (cb - k.COffset) * k.CScale, v = (cr - k.COffset) * k.CScale;
                    r = Saturate(yl + k.RedFromCr * v) * 255f * k.ScaleRed + k.OffsetRed;
                    g = Saturate(yl - k.GreenFromCb * u - k.GreenFromCr * v) * 255f * k.ScaleGreen + k.OffsetGreen;
                    b = Saturate(yl + k.BlueFromCb * u) * 255f * k.ScaleBlue + k.OffsetBlue;
                }

                int pixel = ty * width + tx;
                if (k.Nhwc != 0)
                {
                    output[pixel * 3 + (int)k.RedIndex] = r;
                    output[pixel * 3 + (int)k.GreenIndex] = g;
                    output[pixel * 3 + (int)k.BlueIndex] = b;
                }
                else
                {
                    output[(int)k.RedIndex * plane + pixel] = r;
                    output[(int)k.GreenIndex * plane + pixel] = g;
                    output[(int)k.BlueIndex * plane + pixel] = b;
                }
            }
        }

        return output;
    }

    private static int Clamp(float position, int last) =>
        Math.Clamp((int)MathF.Floor(position), 0, last);

    private static float Luma(Nv12Image image, int x, int y) => image.Luma[y * image.Width + x] / 255f;

    private static (float Cb, float Cr) Chroma(Nv12Image image, int x, int y)
    {
        int i = (y * image.ChromaWidth + x) * 2;
        return (image.Chroma[i] / 255f, image.Chroma[i + 1] / 255f);
    }

    private static float NearestLuma(Nv12Image image, float x, float y) =>
        Luma(image, Clamp(x, image.Width - 1), Clamp(y, image.Height - 1));

    private static (float, float) NearestChroma(Nv12Image image, float x, float y) =>
        Chroma(image,
            Math.Min(Clamp(x, 2 * image.ChromaWidth - 1) / 2, image.ChromaWidth - 1),
            Math.Min(Clamp(y, 2 * image.ChromaHeight - 1) / 2, image.ChromaHeight - 1));

    private static float BilinearLuma(Nv12Image image, float x, float y)
    {
        var (x0, x1, wx) = Straddle(x, image.Width - 1);
        var (y0, y1, wy) = Straddle(y, image.Height - 1);
        float top = Luma(image, x0, y0) + (Luma(image, x1, y0) - Luma(image, x0, y0)) * wx;
        float bottom = Luma(image, x0, y1) + (Luma(image, x1, y1) - Luma(image, x0, y1)) * wx;
        return top + (bottom - top) * wy;
    }

    private static (float, float) BilinearChroma(Nv12Image image, float x, float y)
    {
        var (x0, x1, wx) = Straddle(x / 2, image.ChromaWidth - 1);
        var (y0, y1, wy) = Straddle(y / 2, image.ChromaHeight - 1);
        var (a, c) = (Chroma(image, x0, y0), Chroma(image, x1, y0));
        var (d, e) = (Chroma(image, x0, y1), Chroma(image, x1, y1));
        float Lerp2(float p00, float p01, float p10, float p11)
        {
            float top = p00 + (p01 - p00) * wx;
            float bottom = p10 + (p11 - p10) * wx;
            return top + (bottom - top) * wy;
        }
        return (Lerp2(a.Cb, c.Cb, d.Cb, e.Cb), Lerp2(a.Cr, c.Cr, d.Cr, e.Cr));
    }

    /// <summary>The two pixels whose centres straddle a position, and the second's weight.</summary>
    private static (int I0, int I1, float Weight) Straddle(float position, int last)
    {
        float centred = position - 0.5f;
        float floor = MathF.Floor(centred);
        return (Math.Clamp((int)floor, 0, last), Math.Clamp((int)floor + 1, 0, last), centred - floor);
    }

    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);

    internal static void AssertClose(float[] expected, float[] actual, float tolerance, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        int worst = 0;
        for (int i = 1; i < expected.Length; i++)
        {
            if (MathF.Abs(expected[i] - actual[i]) > MathF.Abs(expected[worst] - actual[worst]))
                worst = i;
        }

        float difference = MathF.Abs(expected[worst] - actual[worst]);
        Assert.True(difference <= tolerance,
            $"{label}: value {worst} is {actual[worst]}, expected {expected[worst]} (|difference| {difference})");
    }
}
