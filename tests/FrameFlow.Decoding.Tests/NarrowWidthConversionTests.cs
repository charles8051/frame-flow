using System.Buffers;
using static FrameFlow.Decoding.Tests.JpegHarness;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// A software decode to Bgra32 writes every pixel of every row (#724). Bgra32 rows are tightly
/// packed, and swscale's x86 YUV-to-RGB converter left the last pixels of each row unwritten at
/// some widths, so they kept whatever the pooled storage held before. <c>PackedConversionTests</c>
/// covers the widths.
/// </summary>
public sealed class NarrowWidthConversionTests(FfmpegBootstrapFixture fixture)
    : IClassFixture<FfmpegBootstrapFixture>
{
    [RequiresFfmpegFact]
    public async Task TheFrameDoesNotDependOnWhatItsStorageHeldBefore()
    {
        var overZeros = await DecodeOverAsync(0x00);
        var overOnes = await DecodeOverAsync(0xFF);

        Assert.Equal(overZeros.Pixels, overOnes.Pixels);
    }

    [RequiresFfmpegFact]
    public async Task EveryColumnOfAnOpaqueSourceIsOpaque()
    {
        var frame = await DecodeOverAsync(0x00);

        var transparentColumns = Enumerable
            .Range(0, frame.Pixels.Length / 4)
            .Where(pixel => frame.Pixels[pixel * 4 + 3] != 0xFF)
            .Select(pixel => pixel % frame.Width)
            .Distinct()
            .Order();

        Assert.Empty(transparentColumns);
    }

    private async Task<(byte[] Pixels, int Width)> DecodeOverAsync(byte fill)
    {
        using var file = TempFile.With(Convert.FromBase64String(NarrowVp9Base64), ".webm");
        await using var demux = await OpenAsync(file.Path);
        await using var decoder = VideoDecoder.Open(
            demux.FormatContextPtr,
            demux.MediaInfo.VideoStreams[0].StreamIndex,
            new HardwareDecodeOptions { Mode = HardwareDecodeMode.Disabled },
            fixture.Capabilities,
            loggerFactory: null
        );
        decoder.FrameStoragePool = new FilledPool(fill);

        using var frames = await DecodeAllAsync(demux, decoder);

        var cpu = Assert.Single(frames).AsCpu()!.Value;
        // The width is what left the rows short: 52 is 4 pixels past a whole number of 16.
        Assert.Equal(52, cpu.Width);
        Assert.Equal(cpu.Width * 4, cpu.StrideY);
        return (cpu.PlaneY.ToArray(), cpu.Width);
    }

    /// <summary>
    /// One 52x32 testsrc2 frame, encoded by libvpx-vp9 as yuv420p in WebM. swscale converts
    /// yuv420p to Bgra32 with its x86 YUV-to-RGB converter.
    /// </summary>
    private const string NarrowVp9Base64 =
            "GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQJChYECGFOAZwEAAAAAAAXwEU2bdLpNu4tTq4QVSalmU6yBoU27i1Or" +
            "hBZUrmtTrIHWTbuMU6uEElTDZ1OsggEyTbuMU6uEHFO7a1OsggXa7AEAAAAAAABZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmsCrX" +
            "sYMPQkBNgIxMYXZmNjMuMS4xMDFXQYxMYXZmNjMuMS4xMDFEiYhAQIAAAAAAABZUrmvXrgEAAAAAAABO14EBc8WIcW0s42HY" +
            "HyycgQAitZyDdW5kiIEAhoVWX1ZQOYOBASPjg4QB/KBV4JCwgTS6gSCagQJVsIRVuYEBVe6BAOwBAAAAAAAAAgAAElTDZ/5z" +
            "c59jwIBnyJlFo4dFTkNPREVSRIeMTGF2ZjYzLjEuMTAxc3PZY8CLY8WIcW0s42HYHyxnyKRFo4dFTkNPREVSRIeXTGF2YzYz" +
            "LjEuMTAxIGxpYnZweC12cDlnyKFFo4hEVVJBVElPTkSHkzAwOjAwOjAwLjAzMzAwMDAwMAAfQ7Z1RB/ngQCjRBmBAACAgkmD" +
            "QgADMAH2BjgkHBhCAAPwUGIf8f30/kfw+v9f03snjfVe+l0P6n1Pyv26N9aMFXz/PedWW93vV7s9R0/30/q/Vbr+T+z43/s/" +
            "e993gAAAefSmu/5kCUlCQyk31eAPHOCn6TyUWaadFklGG6mqbCZBPif4umSHnhzUbrJgqKN+WnUYxv6I7QTtu4aUF+6RKb+r" +
            "iXrI5/fp4ge+Zf+7FgYCIaeT1m/MRRTjKACP4FpON+17EpV/DH/p6TH/4ooO3V/jzG4OT4R1AzvxG5JGJAAx2RP/9aviWNlG" +
            "xnO8B0T/E5+1YIgHE714rerhGXIYYkUP7NCRrHN+QdgZbF8kdJv8Lf82Wu/0IrcAE/NSJ7eieL313+nveLwkuClqF/zmV83Z" +
            "HsfDwtU6ujTSn3/qvusvrEQCf/DifIaRnaYozcAn4OzoSXliuh1pj41/6V2bT/8Qbe8cJhOlG6CV07uyr4+OS2swFMGymIOb" +
            "Xrc25wn1quQxb6WjzEmfepp7AmJQXKkX9kAiI6foJQijEAwLl3pXmHCmzQgFNUw13PP3P9/Md6duU6OhE+pRn9Kd7rTKkE2a" +
            "XoaVte4OYXt9UXCLajcWHl+c7jd9dXf4kfurZ1zOLs4fw1Tr4RKed47vFhRIhvkqSNrID7680TWapEp1kS2tfvZQ4q1xDCUU" +
            "DG3pf2O8L95akYUvjos4qfOQVjo5yjFdxBntY1+EyplrYYLqeNTYGuwPCqC5J3dMWXXoKEzCrFRGRPXutjNllXiPsL6pKb9M" +
            "uik/U5Oq/hy+NvdAfLwD3TNuCB4ZQf3BKBjdmH64fOQwJzycvMbC+I/h93ode5BUw05tS/TUc4t77xjpPNAm6lVtNixY6ZUd" +
            "Jd73O/g5Ocn14rPDFq4sCHyhuFPVvKYI4+mdHns+jgrV7BxmGTW9Xb0ZZNBOJfXdsETpwFutoLp1LBFAVosrsbzuDfmTiVrM" +
            "1lbO6kchG7DWK/Cqw4lLa+vlK3rA28UNsnQLocf4+cit/rDolaMSeMBp06C1eUlmrVXdkBKAng6Iw/EDCU3SWgC/sPW2OWww" +
            "Z48UcZ0Q24arn6fJzuM9KL9U7C+bjqbPhTIxHgMVNOJz4mzIYohtjZqO6eGdNSyHbbJVVmLO3xRhEL//htWYMGDm80fDk2D+" +
            "h3x8ZvcSRjiEjOSaov3orMN34ZgjFccwnLkeLjM8ITpSgPkP8tURHy3opCv/sdQjq5X73CHVeenW/6WHaCL2UxZ/mx7tX5ZO" +
            "I6oltH0UjaR/+Kphf2kVeJhmT192QG+vqtHnWc2LWT9xuAfDYOHm+cS9U3gjXaeFzCkRrd5GzVpteQNA44mAOJGHwz+57s1F" +
            "IxgSaAyEMUWVrQS/+0nekUI7/DcVQ0ZNBHm8OKygxgFoABxTu2uRu4+zgQC3iveBAfGCAbXwgQM=";

    /// <summary>Hands out new storage holding <c>fill</c> in every byte, and keeps nothing returned.</summary>
    private sealed class FilledPool(byte fill) : ArrayPool<byte>
    {
        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength];
            array.AsSpan().Fill(fill);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false) { }
    }
}
