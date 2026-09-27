using System.Runtime.InteropServices;
using FrameFlow.Inference.Core;
using FrameFlow.Inference.D3D12.Core;

namespace FrameFlow.Inference.D3D12.Tests;

/// <summary>
/// The shader's constant block: its layout against the HLSL <c>cbuffer</c>, and the values the
/// plan and the options put in it, and which YUV conversion it carries. The conversion's own
/// arithmetic is pinned by <c>YuvToRgbTests</c>. Pure; no GPU.
/// </summary>
public sealed class KernelConstantsTests
{
    [Fact]
    public void TheBlock_IsFortyValuesWithTheColourVectorsOnRegisterBoundaries()
    {
        Assert.Equal(KernelConstants.Count * 4, Marshal.SizeOf<KernelConstants>());
        // HLSL starts a float4 on a 16-byte register; these are the registers the shader reads.
        Assert.Equal(80, Offset(nameof(KernelConstants.ScaleRed)));
        Assert.Equal(96, Offset(nameof(KernelConstants.OffsetRed)));
        Assert.Equal(112, Offset(nameof(KernelConstants.PadRed)));
        Assert.Equal(128, Offset(nameof(KernelConstants.YOffset)));
        Assert.Equal(144, Offset(nameof(KernelConstants.RedFromCr)));
    }

    [Fact]
    public void TheColourBlock_TakesTheOptionsMatrixAndRange()
    {
        var k = Create(new ImageToTensorOptions(8, 8) { YuvMatrix = YuvMatrix.Bt709, YuvRange = YuvRange.Full });

        // BT.709's red-from-Cr, and full range's untouched luma. BT.601 limited would give 1.402 and 16/255.
        Assert.Equal(1.5748f, k.RedFromCr, 1e-5f);
        Assert.Equal(0f, k.YOffset);
    }

    [Fact]
    public void TheColourBlock_ReadsP010AtTenBits()
    {
        var k = Create(new ImageToTensorOptions(8, 8), YuvSamples.P010);

        Assert.Equal(0f, (P010(64) - k.YOffset) * k.YScale, 1e-5f);
        Assert.Equal(1f, (P010(940) - k.YOffset) * k.YScale, 1e-5f);
        Assert.Equal(0.5f, (P010(960) - k.COffset) * k.CScale, 1e-5f);
    }

    [Fact]
    public void ThePlanAndOptions_LandWhereTheShaderReadsThem()
    {
        var options = new ImageToTensorOptions(64, 32)
        {
            Fit = ImageFit.Letterbox,
            Sampling = ImageSampling.Bilinear,
            Layout = TensorLayout.Nhwc,
            ChannelOrder = TensorChannelOrder.Bgr,
            Normalization = TensorNormalization.MeanStd((0.5f, 0.25f, 0f), (0.25f, 0.5f, 1f)),
            PadValue = 51,
        };
        var plan = ImageToTensorPlan.Create(new RotatedRect(100, 50, 80, 60, 0.3f), 64, 32, ImageFit.Letterbox);

        var k = KernelConstants.Create(plan, options, YuvSamples.Nv12, 320, 240);

        Assert.Equal(((float)plan.A, (float)plan.B, (float)plan.C), (k.A, k.B, k.C));
        Assert.Equal(((float)plan.D, (float)plan.E, (float)plan.F), (k.D, k.E, k.F));
        Assert.Equal(((float)plan.FitLeft, (float)plan.FitRight), (k.FitLeft, k.FitRight));
        Assert.Equal(((float)plan.FitTop, (float)plan.FitBottom), (k.FitTop, k.FitBottom));
        Assert.Equal((64u, 32u, 320u, 240u), (k.TensorWidth, k.TensorHeight, k.FrameWidth, k.FrameHeight));
        Assert.Equal((1u, 1u), (k.Bilinear, k.Nhwc));
        // BGR: red last, blue first.
        Assert.Equal((2u, 1u, 0u), (k.RedIndex, k.GreenIndex, k.BlueIndex));
        Assert.Equal(options.Normalization.Red, (k.ScaleRed, k.OffsetRed));
        Assert.Equal(options.Normalization.Blue, (k.ScaleBlue, k.OffsetBlue));
        // A bar is the pad sample, normalized: (51 / 255 - 0.5) / 0.25 = -1.2 for red.
        Assert.Equal(-1.2f, k.PadRed, 1e-5f);
        Assert.Equal(0.2f, k.PadBlue, 1e-5f);
    }

    [Fact]
    public void TheDefaults_AreRgbNchwBilinear()
    {
        var k = Create(new ImageToTensorOptions(8, 8));

        Assert.Equal((0u, 1u, 2u), (k.RedIndex, k.GreenIndex, k.BlueIndex));
        Assert.Equal((1u, 0u), (k.Bilinear, k.Nhwc));
    }

    private static KernelConstants Create(ImageToTensorOptions options, YuvSamples samples = YuvSamples.Nv12) =>
        KernelConstants.Create(
            ImageToTensorPlan.Create(RotatedRect.FromBounds(0, 0, 8, 8), options.Width, options.Height, options.Fit),
            options, samples, 8, 8);

    private static int Offset(string field) => (int)Marshal.OffsetOf<KernelConstants>(field);

    /// <summary>A 10-bit code as the shader reads a P010 sample: shifted into the high bits, over 65535.</summary>
    private static float P010(int code) => code * 64f / 65535;
}
