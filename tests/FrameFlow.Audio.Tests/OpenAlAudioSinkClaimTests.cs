// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Audio.OpenAL;
using FrameFlow.Audio.TestKit;
using FrameFlow.Media;

namespace FrameFlow.Audio.Tests;

/// <summary>
/// Pins ADR-0085 decision 6 on <see cref="OpenAlAudioSink"/>: it is an
/// <see cref="IClaimableSink"/> that one claimant holds at a time, so two sessions never activate
/// one device or master one clock at once, and it takes no claim once disposed. The device is a
/// fake, and nothing is played.
/// </summary>
public sealed class OpenAlAudioSinkClaimTests
{
    [Fact]
    public async Task OneClaimantAtATime_AndNoClaimOnceDisposed()
    {
        var sink = FakeOpenAlSink.Create(new FakeOpenAlDevice());
        IClaimableSink claimable = sink;

        var lease = claimable.TryClaim("a player");
        Assert.NotNull(lease);
        Assert.Null(claimable.TryClaim("another player"));
        Assert.Equal("a player", claimable.Claimant);
        lease!.Dispose();
        Assert.Null(claimable.Claimant);

        await sink.DisposeAsync();

        var error = Assert.Throws<ObjectDisposedException>(() => claimable.TryClaim("another player"));
        Assert.Equal(typeof(OpenAlAudioSink).FullName, error.ObjectName);
    }
}
