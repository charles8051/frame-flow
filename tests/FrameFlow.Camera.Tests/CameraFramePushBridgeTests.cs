using System.Threading.Channels;
using FrameFlow.Camera.Tests.Fakes;
using Xunit;

namespace FrameFlow.Camera.Tests;

/// <summary>
/// Behavioural tests for <see cref="CameraFramePushBridge"/>. Focus is the
/// refcount discipline at the push-to-pull boundary: every <see cref="ICameraFrame.AddRef"/>
/// the bridge issues must be matched by a <see cref="System.IDisposable.Dispose"/>
/// in exactly one of three places — downstream of the source node (the
/// normal path), the channel's <c>itemDropped</c> callback (drop-policy
/// overflow), or <see cref="CameraFramePushBridge.Push"/> itself
/// (Wait-mode rejection). The bridge's behaviour is observable via the
/// fake frame's <see cref="FakeCameraFrame.RefCount"/> property.
/// </summary>
public class CameraFramePushBridgeTests
{
    // ── Constructor validation ────────────────────────────────────────

    [Fact]
    public void Constructor_CapacityLessThanOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CameraFramePushBridge(capacity: 0));
    }

    // ── Push semantics ────────────────────────────────────────────────

    [Fact]
    public void Push_AddRefsCallerFrame()
    {
        using var bridge = new CameraFramePushBridge();
        var frame = new FakeCameraFrame();

        Assert.Equal(1, frame.RefCount);
        Assert.True(bridge.Push(frame));
        // Caller still owns 1; bridge took +1 via AddRef.
        Assert.Equal(2, frame.RefCount);
    }

    [Fact]
    public void Push_NullFrame_Throws()
    {
        using var bridge = new CameraFramePushBridge();
        Assert.Throws<ArgumentNullException>(() => bridge.Push(null!));
    }

    [Fact]
    public void Push_DropOldestEviction_DisposesEvicted()
    {
        using var bridge = new CameraFramePushBridge(
            capacity: 1,
            overflowMode: BoundedChannelFullMode.DropOldest);
        var first = new FakeCameraFrame();
        var second = new FakeCameraFrame();

        Assert.True(bridge.Push(first));   // fills the slot
        Assert.True(bridge.Push(second));  // evicts first via ItemDropped; takes the slot

        // First's bridge-side ref was disposed by ItemDropped; only the caller's ref remains.
        Assert.Equal(1, first.RefCount);
        // Second's bridge-side ref is still queued; caller + bridge.
        Assert.Equal(2, second.RefCount);
    }

    [Fact]
    public void Push_WaitMode_OnFull_RejectsAndDisposesAddRef()
    {
        using var bridge = new CameraFramePushBridge(
            capacity: 1,
            overflowMode: BoundedChannelFullMode.Wait);
        var first = new FakeCameraFrame();
        var second = new FakeCameraFrame();

        Assert.True(bridge.Push(first));    // accepted
        Assert.False(bridge.Push(second));  // rejected — channel full, Wait mode

        // First: caller's 1 + bridge's 1.
        Assert.Equal(2, first.RefCount);
        // Second: bridge disposed its AddRef'd copy, caller's ref untouched.
        Assert.Equal(1, second.RefCount);
    }

    // ── Single-source enforcement ─────────────────────────────────────

    [Fact]
    public void AsSourceNode_CalledTwice_Throws()
    {
        using var bridge = new CameraFramePushBridge();

        _ = bridge.AsSourceNode("first");
        Assert.Throws<InvalidOperationException>(() => bridge.AsSourceNode("second"));
    }

    [Fact]
    public void AsSourceNode_ThenAsVideoFrameSourceNode_Throws()
    {
        using var bridge = new CameraFramePushBridge();

        _ = bridge.AsSourceNode();
        Assert.Throws<InvalidOperationException>(() => bridge.AsVideoFrameSourceNode());
    }

    [Fact]
    public void AsVideoFrameSourceNode_ThenAsSourceNode_Throws()
    {
        using var bridge = new CameraFramePushBridge();

        _ = bridge.AsVideoFrameSourceNode();
        Assert.Throws<InvalidOperationException>(() => bridge.AsSourceNode());
    }

    // ── Dispose semantics ─────────────────────────────────────────────

    [Fact]
    public void Dispose_DrainsQueuedFrames()
    {
        var bridge = new CameraFramePushBridge(
            capacity: 3,
            overflowMode: BoundedChannelFullMode.DropOldest);
        var frames = new[]
        {
            new FakeCameraFrame(),
            new FakeCameraFrame(),
            new FakeCameraFrame(),
        };

        foreach (var f in frames)
            Assert.True(bridge.Push(f));

        // All three queued; each has caller(1) + bridge(1) = 2.
        foreach (var f in frames)
            Assert.Equal(2, f.RefCount);

        bridge.Dispose();

        // Bridge's drain disposed each queued ref; only caller's remains.
        foreach (var f in frames)
            Assert.Equal(1, f.RefCount);
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var bridge = new CameraFramePushBridge();
        bridge.Dispose();
        bridge.Dispose(); // must not throw
    }

    [Fact]
    public void Push_AfterDispose_ReturnsFalseWithoutAddRef()
    {
        var bridge = new CameraFramePushBridge();
        bridge.Dispose();

        var frame = new FakeCameraFrame();
        Assert.False(bridge.Push(frame));
        Assert.Equal(1, frame.RefCount); // no AddRef happened
    }

    [Fact]
    public void AsSourceNode_AfterDispose_Throws()
    {
        var bridge = new CameraFramePushBridge();
        bridge.Dispose();

        Assert.Throws<ObjectDisposedException>(() => bridge.AsSourceNode());
        Assert.Throws<ObjectDisposedException>(() => bridge.AsVideoFrameSourceNode());
    }
}
