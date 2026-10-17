using FrameFlow.Avalonia.Windows.Core;
using FrameFlow.Decoding;
using FrameFlow.Inference.D3D12.Tests;
using FrameFlow.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace FrameFlow.Avalonia.Windows.Tests;

/// <summary>
/// The CPU conversion and the D3D11 presenter's shader agree on a BT.709 frame (#796). Both read
/// the same decoded samples: the shader from the decode texture, the CPU path from the same frame
/// read back. Before #796 the CPU path converted it with BT.601, tens of levels off on the
/// saturated bars.
/// </summary>
/// <remarks>
/// Only the bars' interiors are compared. The shader filters chroma bilinearly and swscale takes
/// each chroma sample as it is, so the two differ by design at a bar's edge.
/// </remarks>
[Collection(FrameCopyCountsCollection.Name)]
public sealed class D3D11CpuConversionAgreementTests(ITestOutputHelper output)
{
    private const string Bars = "test-video-vp9-bt709-bars.webm";

    [RequiresHardwareDecodeFact(HardwareDecodeBackendKind.D3D11Va, Bars)]
    public async Task ABt709Frame_ConvertsAlikeOnTheCpuAndInTheShader()
    {
        // One frame: VP9 on D3D11VA hangs when the test holds more of its pool.
        var frames = await Decode.FramesAsync(Bars, HardwareDecodeBackendKind.D3D11Va, yieldHardware: true, count: 1);
        try
        {
            var frame = (GpuVideoFrame)frames[0];
            Assert.Equal(PixelFormat.Nv12, frame.Format);
            Assert.True(frame.TryGetD3D11Texture(out nint texture, out int slice, out _));
            using var converter = new D3D11Nv12SharedConverter(
                texture, frame.Width, frame.Height, PresenterOutput.Shader(frame.Width, frame.Height), NullLogger.Instance);
            using var compositor = new D3D11CompositorSide(texture);
            Assert.True(converter.ConvertInto(0, texture, slice));
            var shader = compositor.Read(converter.GetSharedHandle(0), frame.Width, frame.Height);

            using var cpuFrame = frame.ReadbackToCpuBgra32();
            var cpu = cpuFrame.AsCpu()!.Value;
            Assert.Equal(PixelFormat.Bgra32, cpuFrame.Format);

            const int barCount = 8;
            int barWidth = frame.Width / barCount;
            int worst = 0;
            for (int row = 8; row < frame.Height - 8; row += 4)
            {
                for (int bar = 0; bar < barCount; bar++)
                {
                    for (int column = bar * barWidth + 8; column < (bar + 1) * barWidth - 8; column += 4)
                    {
                        for (int channel = 0; channel < 3; channel++)
                        {
                            int expected = cpu.PlaneY.Span[row * cpu.StrideY + column * 4 + channel];
                            int actual = shader[(row * frame.Width + column) * 4 + channel];
                            worst = Math.Max(worst, Math.Abs(expected - actual));
                        }
                    }
                }
            }

            // The shader is exact to half a level. swscale's limited-range converter is up to 3
            // levels off the exact result on these bars, with SWS_BILINEAR and no SWS_ACCURATE_RND.
            output.WriteLine($"Worst difference inside the bars: {worst} levels.");
            Assert.True(worst <= 3, $"The shader and the CPU path differ by up to {worst} levels inside the bars.");
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }
}
