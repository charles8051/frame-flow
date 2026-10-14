// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media.Tests.Doubles;

namespace FrameFlow.Media.Tests;

/// <summary>
/// Pins <see cref="DelegatingVideoSink"/> (ADR-0085, decision 8): it hands each frame to its
/// delegate, declares what it is told to, claims nothing, and once disposed disposes what it is
/// given without calling the delegate (decision 4).
/// </summary>
public sealed class DelegatingVideoSinkTests
{
    [Fact]
    public async Task EachFrame_GoesToTheDelegate_WithItsToken_AndTheDelegateOwnsIt()
    {
        var given = new List<(IVideoFrame Frame, CancellationToken Token)>();
        var sink = new DelegatingVideoSink((frame, ct) =>
        {
            given.Add((frame, ct));
            return ValueTask.CompletedTask;
        });
        var frame = new CountedFrame();
        using var cts = new CancellationTokenSource();

        await sink.PresentAsync(frame, cts.Token);

        var (handed, token) = Assert.Single(given);
        Assert.Same(frame, handed);
        Assert.Equal(cts.Token, token);
        Assert.Equal(1, frame.References); // the delegate's, which it has not released
    }

    [Fact]
    public void Undeclared_ItReadsAsAnySinkThatSaysNothing()
    {
        IVideoSink sink = new DelegatingVideoSink((frame, _) => Release(frame));

        Assert.Null(sink.MaxHeldFrames);
        Assert.Equal(FrameMemoryDomains.Cpu, sink.AcceptedDomains);
        Assert.Empty(sink.PreferredBackends);
    }

    [Fact]
    public void Declared_ItReadsAsDeclared()
    {
        IVideoSink sink = new DelegatingVideoSink((frame, _) => Release(frame))
        {
            MaxHeldFrames = 3,
            AcceptedDomains = FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11,
            PreferredBackends = [HardwareDecodeBackendKind.D3D11Va],
        };

        Assert.Equal(3, sink.MaxHeldFrames);
        Assert.Equal(FrameMemoryDomains.Cpu | FrameMemoryDomains.D3D11, sink.AcceptedDomains);
        Assert.Equal([HardwareDecodeBackendKind.D3D11Va], sink.PreferredBackends);
    }

    [Fact]
    public void WhatCannotBeTrue_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new DelegatingVideoSink(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DelegatingVideoSink((frame, _) => Release(frame)) { MaxHeldFrames = -1 }
        );
        Assert.Throws<ArgumentNullException>(() =>
            new DelegatingVideoSink((frame, _) => Release(frame)) { PreferredBackends = null! }
        );
    }

    [Fact]
    public async Task AFormatChange_DoesNothing_AndItClaimsNothing()
    {
        var calls = 0;
        var sink = new DelegatingVideoSink((frame, _) =>
        {
            calls++;
            return Release(frame);
        });

        await sink.OnFormatChangedAsync(new VideoFormatInfo(4, 4, PixelFormat.Bgra32), CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.IsNotAssignableFrom<IClaimableSink>(sink);
    }

    [Fact]
    public async Task OnceDisposed_AFrameIsDisposed_AndTheDelegateIsNotCalled()
    {
        var calls = 0;
        var sink = new DelegatingVideoSink((frame, _) =>
        {
            calls++;
            return Release(frame);
        });
        var frame = new CountedFrame();

        await sink.DisposeAsync();
        await sink.DisposeAsync(); // a second dispose does nothing
        await sink.PresentAsync(frame, CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Equal(0, frame.References);
        Assert.Equal(0, frame.OverReleases);
    }

    private static ValueTask Release(IVideoFrame frame)
    {
        frame.Dispose();
        return ValueTask.CompletedTask;
    }
}
