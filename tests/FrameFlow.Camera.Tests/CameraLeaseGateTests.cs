using FrameFlow.Graph;
using FrameFlow.Camera.Internal;
using FrameFlow.Camera.Tests.Fakes;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FrameFlow.Camera.Tests;

/// <summary>
/// The camera source's guard (#385): the graph gets zero-copy leases up to its share of the
/// session's <c>BufferCount</c>, and a CPU copy past it, with the lease returned at once.
/// </summary>
public sealed class CameraLeaseGateTests
{
    private static readonly byte[] Pixel = [10, 20, 30, 255];

    [Fact]
    public void UnderTheLimit_TheGraphGetsTheLeaseItself()
    {
        var gate = new CameraLeaseGate(limit: 2, new RecordingLogger(), "camera");
        var lease = new FakeCameraFrame(Pixel);

        var frame = gate.HandOff(lease);

        Assert.IsType<CameraVideoFrame>(frame);
        Assert.Equal(1, lease.RefCount);
        Assert.Equal(1, gate.Outstanding);

        frame.Dispose();
        Assert.Equal(0, lease.RefCount);
        Assert.Equal(0, gate.Outstanding);
    }

    [Fact]
    public void AtTheLimit_TheGraphGetsACopy_AndTheLeaseGoesBackAtOnce()
    {
        var logger = new RecordingLogger();
        var gate = new CameraLeaseGate(limit: 1, logger, "camera");
        var held = new FakeCameraFrame(Pixel);
        var next = new FakeCameraFrame(Pixel);

        var first = gate.HandOff(held);
        var copy = gate.HandOff(next);

        var cpu = Assert.IsType<CpuVideoFrame>(copy);
        Assert.Equal(PixelFormat.Bgra32, cpu.Format);
        Assert.Equal(Pixel, cpu.ToCpu().PlaneY.ToArray());
        Assert.Equal(0, next.RefCount);
        Assert.Equal(1, gate.Outstanding);
        Assert.Equal(1, gate.FramesCopied);
        Assert.Equal(1, logger.Informations);

        copy.Dispose();
        first.Dispose();
        Assert.Equal(0, held.RefCount);
    }

    [Fact]
    public void ALeaseSharedDownstream_CountsOnce_AndFreesItsPlaceOnTheLastRelease()
    {
        var gate = new CameraLeaseGate(limit: 1, new RecordingLogger(), "camera");
        var lease = new FakeCameraFrame(Pixel);
        var frame = gate.HandOff(lease);
        var shared = frame.AddRef();

        frame.Dispose();
        Assert.Equal(1, gate.Outstanding);

        shared.Dispose();
        Assert.Equal(0, gate.Outstanding);
        Assert.Equal(0, lease.RefCount);

        var again = gate.HandOff(new FakeCameraFrame(Pixel));
        Assert.IsType<CameraVideoFrame>(again);
        again.Dispose();
    }

    [Fact]
    public void OnlyTheFirstCopy_IsLogged()
    {
        var logger = new RecordingLogger();
        var gate = new CameraLeaseGate(limit: 0, logger, "camera");

        for (int i = 0; i < 3; i++)
            gate.HandOff(new FakeCameraFrame(Pixel)).Dispose();

        Assert.Equal(3, gate.FramesCopied);
        Assert.Equal(1, logger.Informations);
    }

    [Fact]
    public async Task ThePushSource_HandsTheGraphItsShareOfBufferCount_AndCopiesPastIt()
    {
        // A BufferCount of 5 less a bridge of 3 leaves the graph 2 leases.
        using var bridge = new CameraFramePushBridge(capacity: 3);
        var (source, gate) = CameraPushSource.GuardedSource(
            bridge,
            bufferCount: 5,
            capacity: 3,
            new RecordingLogger(),
            "camera"
        );
        var leases = new[]
        {
            new FakeCameraFrame(Pixel),
            new FakeCameraFrame(Pixel),
            new FakeCameraFrame(Pixel),
        };
        foreach (var lease in leases)
        {
            Assert.True(bridge.Push(lease));
            lease.Dispose(); // the pump's own ref; the bridge keeps one
        }

        var frames = new List<IVideoFrame>();
        for (int i = 0; i < leases.Length; i++)
            frames.Add((await source.Body(CancellationToken.None))!);

        Assert.Equal(2, gate.Limit);
        Assert.IsType<CameraVideoFrame>(frames[0]);
        Assert.IsType<CameraVideoFrame>(frames[1]);
        Assert.IsType<CpuVideoFrame>(frames[2]);
        Assert.Equal([1, 1, 0], leases.Select(l => l.RefCount));

        foreach (var frame in frames)
            frame.Dispose();
        await source.Cleanup!();

        Assert.All(leases, l => Assert.Equal(0, l.RefCount));
        Assert.Equal(0, gate.Outstanding);
    }

    [Fact]
    public void ABudgetOverTheLimit_IsLoggedOnce_AndOnlyFramesPastTheLimitAreCopied()
    {
        var logger = new RecordingLogger();
        var gate = new CameraLeaseGate(limit: 2, logger, "camera");

        gate.ApplyBudget(FrameBudget.Of(3));
        gate.ApplyBudget(FrameBudget.Of(3));
        Assert.Equal(1, logger.Informations);

        var first = gate.HandOff(new FakeCameraFrame(Pixel));
        var second = gate.HandOff(new FakeCameraFrame(Pixel));
        var third = gate.HandOff(new FakeCameraFrame(Pixel));

        Assert.IsType<CameraVideoFrame>(first);
        Assert.IsType<CameraVideoFrame>(second);
        Assert.IsType<CpuVideoFrame>(third);
        first.Dispose();
        second.Dispose();
        third.Dispose();
    }

    [Fact]
    public void ABudgetThatFits_IsNotLogged()
    {
        var logger = new RecordingLogger();
        var gate = new CameraLeaseGate(limit: 3, logger, "camera");

        gate.ApplyBudget(FrameBudget.Of(3));

        Assert.Equal(0, logger.Informations);
    }

    [Fact]
    public async Task ThePushSource_IsToldItsGraphsBudget_BeforeTheRun()
    {
        // A BufferCount of 3 less a bridge of 1 leaves the graph 2. The sink declares nothing, so
        // the budget is unbounded, which is logged before the first frame arrives.
        using var bridge = new CameraFramePushBridge(capacity: 1);
        var logger = new RecordingLogger();
        var (source, gate) = CameraPushSource.GuardedSource(
            bridge,
            bufferCount: 3,
            capacity: 1,
            logger,
            "camera"
        );
        int loggedBeforeTheFrame = -1;
        var received = new TaskCompletionSource<IVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = new FrameFlow.Graph.Graph();
        graph.Pipeline(source).To(new SinkNode<IVideoFrame>("sink", (frame, _) =>
        {
            loggedBeforeTheFrame = logger.Informations;
            received.TrySetResult(frame.AddRef());
            return ValueTask.CompletedTask;
        }));

        var run = graph.RunAsync(CancellationToken.None);
        var lease = new FakeCameraFrame(Pixel);
        Assert.True(bridge.Push(lease));
        lease.Dispose();
        var only = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        bridge.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, loggedBeforeTheFrame);
        // Under its share, the frame is the lease itself.
        Assert.IsType<CameraVideoFrame>(only);
        Assert.Equal(1, gate.Outstanding);
        only.Dispose();
        Assert.Equal(0, lease.RefCount);
    }

    /// <summary>Counts information-level entries.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private int _informations;

        public int Informations => Volatile.Read(ref _informations);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Information)
                Interlocked.Increment(ref _informations);
        }
    }
}
