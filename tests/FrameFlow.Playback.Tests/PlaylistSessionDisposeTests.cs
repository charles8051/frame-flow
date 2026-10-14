// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Pins ADR-0086 decision 3 on the playlist session's dispose: a later dispose ends as the first
/// did, its exception included. The session's dispose throws only when it releases the item's
/// runtime itself, which it does when its reader has stopped before handling the dispose. Here a
/// controller callback that throws stops the reader, with the item's runtime still held, and that
/// runtime's dispose fails.
/// </summary>
public sealed class PlaylistSessionDisposeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ASecondDispose_ThrowsWhatTheFirstThrew()
    {
        // Not disposed at the end: its dispose is the subject, and it throws.
        var rig = await PlaylistSessionRig.LoadedAsync(RepeatMode.Off, "a");
        rig.FatalCallbackThrows = new InvalidOperationException("the controller's callback failed");
        var disposeFailed = new InvalidOperationException("the item's dispose failed");
        rig.Fail("a", ItemOp.Dispose, disposeFailed);
        rig.Runtime("a#1").RaiseFault(new InvalidOperationException("decoder lost"));
        await rig.SettleAsync();

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.DisposeAsync().AsTask().WaitAsync(Bound)
        );
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Session.DisposeAsync().AsTask().WaitAsync(Bound)
        );

        Assert.Same(disposeFailed, first);
        Assert.Same(first, second);
        Assert.Single(rig.TakeLog(), line => line == "a#1.Dispose");
    }
}
