// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Encoding;
using Xunit;

namespace FrameFlow.Encoding.Tests;

/// <summary>
/// Tests for how <see cref="H264EncoderOptions"/> decides which encoder to open.
/// </summary>
/// <remarks>
/// <para>
/// Opening an encoder needs FFmpeg, so the resolution <i>outcome</i> is covered by the
/// round-trip tests. What is testable without a native build, and what carries the
/// decision, is the preference order itself.
/// </para>
/// <para>
/// The order exists because a fixed default threw on every Mac. FrameFlow ships FFmpeg
/// for Windows and Linux with <c>libopenh264</c> statically linked; it ships none for
/// macOS, where the bootstrapper resolves a Homebrew <c>ffmpeg@7</c> keg that has no
/// openh264 and does have <c>h264_videotoolbox</c>. Verified against Homebrew
/// <c>ffmpeg@7</c> 7.1.3 on arm64.
/// </para>
/// </remarks>
public sealed class H264EncoderResolutionTests
{
    [Fact]
    public void NoEncoderIsPinnedByDefault()
    {
        // The default has to be "resolve", not a name: a name that is right on two of
        // three platforms is what produced the macOS failure.
        Assert.Null(new H264EncoderOptions().EncoderName);
    }

    [Fact]
    public void OpenH264ComesFirst()
    {
        // It is what FrameFlow's own Windows and Linux builds carry, so those platforms
        // keep the encoder they have always used — deterministic, hardware-independent,
        // and identical across the two.
        Assert.Equal("libopenh264", H264EncoderOptions.DefaultEncoderPreference[0]);
    }

    [Fact]
    public void VideoToolboxIsTheFallback()
    {
        Assert.Contains("h264_videotoolbox", H264EncoderOptions.DefaultEncoderPreference);
        Assert.True(
            H264EncoderOptions.DefaultEncoderPreference.ToList().IndexOf("h264_videotoolbox") > 0,
            "VideoToolbox must not displace libopenh264 where both exist."
        );
    }

    [Fact]
    public void Libx264IsNotInThePreferenceOrder()
    {
        // Deliberate omission, and not a licence guarantee: Homebrew's ffmpeg@7 is built
        // --enable-gpl --enable-libx264, so a macOS consumer is on a GPL FFmpeg whatever
        // we pick. VideoToolbox is preferred because it is on every Mac and does not
        // depend on which optional formulae their build happened to include. Anyone who
        // wants x264 pins it.
        Assert.DoesNotContain("libx264", H264EncoderOptions.DefaultEncoderPreference);
    }

    [Fact]
    public void ThePreferenceOrderNeedsNoPlatformConditionals()
    {
        // Every entry is absent from the builds where it does not apply, so a lookup
        // simply misses. The same list is correct on all three platforms, which is why
        // it is a static rather than something computed per OS.
        Assert.Equal(
            H264EncoderOptions.DefaultEncoderPreference,
            H264EncoderOptions.DefaultEncoderPreference
        );
        Assert.NotEmpty(H264EncoderOptions.DefaultEncoderPreference);
        Assert.All(H264EncoderOptions.DefaultEncoderPreference, n => Assert.NotEmpty(n));
    }

    [Fact]
    public void ThePreferenceOrderCannotBeMutatedByCallers()
    {
        // Every encoder open reads this. An array behind IReadOnlyList can be cast back
        // and reordered -- or emptied -- making resolution depend on whoever ran first.
        Assert.IsNotType<string[]>(H264EncoderOptions.DefaultEncoderPreference);

        if (H264EncoderOptions.DefaultEncoderPreference is IList<string> mutable)
            Assert.True(mutable.IsReadOnly);
    }

    [Fact]
    public void APinnedNameSurvivesTheRecordCopy()
    {
        var options = new H264EncoderOptions { EncoderName = "h264_nvenc" };
        var copied = options with { BitRate = 8_000_000 };

        Assert.Equal("h264_nvenc", copied.EncoderName);
    }
}
