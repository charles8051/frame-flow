using System.Runtime.InteropServices;
using FrameFlow.Inference.Core;
using FrameFlow.Inference.D3D12.Core;

namespace FrameFlow.Inference.D3D12.Tests;

/// <summary>
/// The shader's constant block: its layout against the HLSL <c>cbuffer</c>, and the values the
/// plan, the options and the YUV conversion put in it. Pure; no GPU.
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

    [Theory]
    [InlineData(YuvMatrix.Bt601, 1.402f, 0.344136f, 0.714136f, 1.772f)]
    [InlineData(YuvMatrix.Bt709, 1.5748f, 0.187324f, 0.468124f, 1.8556f)]
    public void TheMatrix_HasItsStandardsCoefficients(
        YuvMatrix matrix, float redFromCr, float greenFromCb, float greenFromCr, float blueFromCb)
    {
        var k = Create(new ImageToTensorOptions(8, 8), matrix, YuvRange.Limited);

        Assert.Equal(redFromCr, k.RedFromCr, 1e-5f);
        Assert.Equal(greenFromCb, k.GreenFromCb, 1e-5f);
        Assert.Equal(greenFromCr, k.GreenFromCr, 1e-5f);
        Assert.Equal(blueFromCb, k.BlueFromCb, 1e-5f);
    }

    [Fact]
    public void LimitedRange_StretchesLumaFrom16To235AndChromaFrom16To240()
    {
        var k = Create(new ImageToTensorOptions(8, 8), YuvMatrix.Bt601, YuvRange.Limited);

        Assert.Equal(0f, (16f / 255 - k.YOffset) * k.YScale, 1e-6f);
        Assert.Equal(1f, (235f / 255 - k.YOffset) * k.YScale, 1e-6f);
        Assert.Equal(-0.5f, (16f / 255 - k.COffset) * k.CScale, 1e-6f);
        Assert.Equal(0.5f, (240f / 255 - k.COffset) * k.CScale, 1e-6f);
    }

    [Fact]
    public void FullRange_TakesSamplesAsTheyAre()
    {
        var k = Create(new ImageToTensorOptions(8, 8), YuvMatrix.Bt709, YuvRange.Full);

        Assert.Equal(0f, k.YOffset);
        Assert.Equal(1f, k.YScale);
        Assert.Equal(128f / 255, k.COffset);
        Assert.Equal(1f, k.CScale);
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

        var k = KernelConstants.Create(plan, options, YuvMatrix.Bt601, YuvRange.Limited, 320, 240);

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
        var k = Create(new ImageToTensorOptions(8, 8), YuvMatrix.Bt601, YuvRange.Limited);

        Assert.Equal((0u, 1u, 2u), (k.RedIndex, k.GreenIndex, k.BlueIndex));
        Assert.Equal((1u, 0u), (k.Bilinear, k.Nhwc));
    }

    private static KernelConstants Create(ImageToTensorOptions options, YuvMatrix matrix, YuvRange range) =>
        KernelConstants.Create(
            ImageToTensorPlan.Create(RotatedRect.FromBounds(0, 0, 8, 8), options.Width, options.Height, options.Fit),
            options, matrix, range, 8, 8);

    private static int Offset(string field) => (int)Marshal.OffsetOf<KernelConstants>(field);
}
