using System.Runtime.InteropServices;
using FrameFlow.Avalonia.Windows.Core;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The D3D12 presenter's root constants: the frame's part of the decode texture, and the sample
/// levels that make studio-range BT.709 for NV12 and P010 (#429).
/// </summary>
public sealed class D3D12PresentConstantsTests
{
    [Fact]
    public void ItIsTheRootSignaturesEightValues()
    {
        Assert.Equal(D3D12PresentConstants.Count * sizeof(float), Marshal.SizeOf<D3D12PresentConstants>());
    }

    [Fact]
    public void AFrameSizedTexture_IsSampledWhole_AndChromaStopsAtItsLastSample()
    {
        var c = D3D12PresentConstants.Create(YuvSampleFormat.Nv12, 320, 240, 320, 240);

        Assert.Equal(1f, c.UScale, 1e-6f);
        Assert.Equal(1f, c.VScale, 1e-6f);
        Assert.Equal(159.5f / 160, c.ChromaUMax, 1e-6f);
        Assert.Equal(119.5f / 120, c.ChromaVMax, 1e-6f);
    }

    [Fact]
    public void AnAlignedTexture_IsSampledOnlyWhereTheFrameIs()
    {
        // 1080p decodes into 1088 rows: the last eight rows and four chroma rows are padding.
        var c = D3D12PresentConstants.Create(YuvSampleFormat.Nv12, 1920, 1088, 1920, 1080);

        Assert.Equal(1f, c.UScale, 1e-6f);
        Assert.Equal(1080f / 1088, c.VScale, 1e-6f);
        Assert.Equal(539.5f / 544, c.ChromaVMax, 1e-6f);
    }

    [Fact]
    public void AnOddFrame_KeepsItsHalfChromaSample()
    {
        var c = D3D12PresentConstants.Create(YuvSampleFormat.Nv12, 322, 242, 321, 241);

        // 321 columns have 161 chroma samples, the last covering one luma column.
        Assert.Equal(160.5f / 161, c.ChromaUMax, 1e-6f);
        Assert.Equal(120.5f / 121, c.ChromaVMax, 1e-6f);
    }

    [Theory]
    [InlineData(false, 255.0, 16, 235, 128, 240)]
    [InlineData(true, 65535.0 / 64, 64, 940, 512, 960)]
    public void StudioLevels_MapToBlackWhiteNeutralAndFullChroma(
        bool p010, double codesPerUnit, int black, int white, int neutral, int chromaTop)
    {
        var samples = p010 ? YuvSampleFormat.P010 : YuvSampleFormat.Nv12;
        var c = D3D12PresentConstants.Create(samples, 64, 64, 64, 64);

        // What the shader reads for a code, through the texture's UNORM view.
        float Read(int code) => (float)(code / codesPerUnit);

        Assert.Equal(0f, (Read(black) - c.YOffset) * c.YScale, 1e-6f);
        Assert.Equal(1f, (Read(white) - c.YOffset) * c.YScale, 1e-6f);
        Assert.Equal(0f, (Read(neutral) - c.COffset) * c.CScale, 1e-6f);
        Assert.Equal(0.5f, (Read(chromaTop) - c.COffset) * c.CScale, 1e-6f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLevels_AreTheOnesTheD3D11ConverterTakes(bool p010)
    {
        var samples = p010 ? YuvSampleFormat.P010 : YuvSampleFormat.Nv12;
        var c = D3D12PresentConstants.Create(samples, 64, 64, 64, 64);

        Assert.Equal(YuvLevels.For(samples), new YuvLevels(c.YOffset, c.YScale, c.COffset, c.CScale));
    }

    [Fact]
    public void TheLevels_FillOneConstantBufferRegister()
    {
        // D3D11 sizes a constant buffer in 16-byte registers.
        Assert.Equal(16, Marshal.SizeOf<YuvLevels>());
    }

    [Fact]
    public void ATextureSmallerThanTheFrame_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => D3D12PresentConstants.Create(YuvSampleFormat.Nv12, 1920, 1080, 1920, 1088));
    }

    [Fact]
    public void BridgeFenceValues_AlternateBetweenTheDevices_AndOnlyRise()
    {
        Assert.Equal(new BridgeFenceValues(0, 1, 2), BridgeFenceValues.For(1));
        Assert.Equal(new BridgeFenceValues(2, 3, 4), BridgeFenceValues.For(2));

        // Each draw waits for the copy before it, and every value is above the last one signalled.
        for (ulong frame = 2; frame < 100; frame++)
        {
            var previous = BridgeFenceValues.For(frame - 1);
            var current = BridgeFenceValues.For(frame);
            Assert.Equal(previous.Copied, current.CopiedBefore);
            Assert.True(previous.Copied < current.Drawn && current.Drawn < current.Copied);
        }
    }

    [Fact]
    public void BridgeFenceValues_CountFramesFromOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BridgeFenceValues.For(0));
    }
}
