using FrameFlow.Integration.Tests.Harness;
using FrameFlow.Media;
using FrameFlow.Playback;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Integration.Tests;

/// <summary>
/// A fault inside the lateness-recovery walk, over real corpus media through
/// <see cref="PlaybackController.Create"/>. The walk is an optimisation, so its fault is reported
/// on <see cref="IPlaybackController.ErrorOccurred"/> and playback carries on.
/// </summary>
/// <remarks>
/// <para>
/// The walk takes no dependency a test can substitute except its logger, so the fault is thrown
/// from the logger when the walk logs its first settle window.
/// </para>
/// <para>
/// The video sink holds its first frame until the fault has been reported. That frame counts as
/// presented, so the walk can read lateness, and the clip cannot reach <c>Ended</c> before the
/// walk faults. These tests wait on real playback, which is why they are in this suite
/// (ADR-0072 rule 6). Each wait completes on a signal; the bound only stops a failing run.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LatenessRecoveryFaultTests : IClassFixture<FfmpegBootstrapFixture>
{
    private const string Clip = "test-video-h264-yuv420p.mp4";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public LatenessRecoveryFaultTests(FfmpegBootstrapFixture fixture)
    {
        _ = fixture;
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task FaultInTheWalk_IsReported_AndPlaybackCarriesOn()
    {
        var path = IntegrationTestEnvironment.GetCorpusFile(Clip);
        Assert.NotNull(path);

        var sink = new HeldVideoSink();
        var logs = new FaultingLoggerFactory();
        await using var controller = PlaybackController.Create(
            videoSink: sink,
            hardwareDecodeMode: HardwareDecodeMode.Disabled,
            loggerFactory: logs,
            latenessRecovery: new LatenessRecoveryOptions
            {
                Enabled = true,
                SettleWindow = TimeSpan.FromMilliseconds(20),
            }
        );

        var gate = new Lock();
        var errors = new List<(PlaybackError Error, PlaybackState State)>();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource<PlaybackState>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var errorSubscription = controller.ErrorOccurred.Subscribe(
            new ActionObserver<PlaybackError>(e =>
            {
                lock (gate)
                    errors.Add((e, controller.State));
                reported.TrySetResult();
            })
        );
        using var stateSubscription = controller.PlaybackStateChanged.Subscribe(
            new ActionObserver<StateTransition<PlaybackState>>(t =>
            {
                if (t.Current is PlaybackState.Ended or PlaybackState.Error)
                    settled.TrySetResult(t.Current);
            })
        );

        var load = await controller.LoadAsync(MediaSource.FromFile(path!));
        Assert.True(load.IsSuccess, $"Load failed: {load.Error?.Message}");
        var play = await controller.PlayAsync();
        Assert.True(play.IsSuccess, $"Play failed: {play.Error?.Message}");

        // Armed only once playing, so a walk that logs while paused cannot fault early.
        logs.Arm();
        await reported.Task.WaitAsync(Bound);
        sink.Release();

        // Ended comes after the fault, so every error the fault caused has been raised by then.
        var final = await settled.Task.WaitAsync(Bound);

        (PlaybackError Error, PlaybackState State)[] seen;
        lock (gate)
            seen = [.. errors];
        var (error, stateWhenReported) = Assert.Single(seen);
        Assert.Equal(PlaybackState.Playing, stateWhenReported);
        Assert.Equal(PlaybackState.Ended, final);
        Assert.Equal(ErrorCategory.System, error.Category);
        Assert.IsType<InjectedFault>(error.Inner);
    }

    /// <summary>
    /// A video sink that holds the first frame it is given until <see cref="Release"/>, and
    /// discards every frame.
    /// </summary>
    private sealed class HeldVideoSink : IVideoSink
    {
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public IFramePool FramePool => null!;

        public void Release() => _released.TrySetResult();

        public async ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
        {
            // A cancelled wait leaves the frame to the caller, which disposes it.
            await _released.Task.WaitAsync(ct);
            frame.Dispose();
        }

        public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Discards every log entry. Once armed, throws <see cref="InjectedFault"/> from any entry
    /// whose template starts with "Lateness recovery", which only the walk writes.
    /// </summary>
    private sealed class FaultingLoggerFactory : ILoggerFactory, ILogger
    {
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider) { }

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
            if (Volatile.Read(ref _armed) == 0)
                return;
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                return;
            foreach (var (key, value) in values)
            {
                if (
                    key == "{OriginalFormat}"
                    && value is string template
                    && template.StartsWith("Lateness recovery", StringComparison.Ordinal)
                )
                {
                    throw new InjectedFault();
                }
            }
        }

        public void Dispose() { }
    }

    private sealed class InjectedFault() : Exception("Injected fault in the lateness-recovery walk.");
}
