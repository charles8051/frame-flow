// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Reflection;
using FrameFlow.Player.Core;

namespace FrameFlow.Player.Tests;

/// <summary>
/// <see cref="MediaTransportExtensions.WaitForEndAsync"/> over a transport the test scripts
/// (#610), and over a real player whose audio sink holds its drain (#609).
/// </summary>
/// <remarks>
/// <para>
/// The scripted transport raises its events synchronously on the test thread, so the state of the
/// wait read straight after an event is the state that event left.
/// </para>
/// <para>
/// It is a <see cref="DispatchProxy"/> rather than a class implementing the interface, so it
/// answers only the three members the wait reads and goes on compiling as the transport grows.
/// </para>
/// </remarks>
public sealed class WaitForEndTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly PlaybackError Fatal = new(ErrorCategory.Decode, "The decoder gave up.");

    // ── Over a scripted transport ───────────────────────────────────────────

    [Fact]
    public async Task CompletesWithSuccess_WhenPlaybackEnds()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Paused);

        var wait = transport.WaitForEndAsync();
        script.Move(PlaybackState.Playing);
        Assert.False(wait.IsCompleted);

        script.Move(PlaybackState.Ended);

        Assert.True((await wait.WaitAsync(Bound)).IsSuccess);
    }

    [Fact]
    public async Task CompletesAtOnce_WhenPlaybackHasAlreadyEnded()
    {
        var (transport, _) = ScriptedTransport.Create(PlaybackState.Ended);

        var wait = transport.WaitForEndAsync();

        Assert.True(wait.IsCompleted);
        Assert.True((await wait.WaitAsync(Bound)).IsSuccess);
    }

    [Fact]
    public async Task FailsWithThePlayersError_WhenItEntersError()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        var wait = transport.WaitForEndAsync();

        script.Move(PlaybackState.Error);
        Assert.False(wait.IsCompleted, "The wait gave up before the player said why it failed.");
        script.Raise(Fatal);

        var result = await wait.WaitAsync(Bound);
        Assert.False(result.IsSuccess);
        Assert.Same(Fatal, result.Error);
    }

    /// <summary>
    /// An item failure raised while playing does not end the wait. It is also the sequence a last
    /// item that faults mid-play produces under <see cref="RepeatMode.Off"/>: the error while
    /// <see cref="PlaybackState.Playing"/>, then <see cref="PlaybackState.Ended"/>. The wait
    /// reports success for it, as its remarks say.
    /// </summary>
    [Fact]
    public async Task KeepsWaiting_ThroughAnItemFailureThePlayerCarriesOnFrom()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        var wait = transport.WaitForEndAsync();

        script.Raise(new PlaybackError(ErrorCategory.Io, "One item would not open."));
        Assert.False(wait.IsCompleted);

        script.Move(PlaybackState.Ended);
        Assert.True((await wait.WaitAsync(Bound)).IsSuccess);
    }

    [Fact]
    public async Task FailsAtOnce_WhenThePlayerIsAlreadyInError()
    {
        var (transport, _) = ScriptedTransport.Create(PlaybackState.Error);

        var result = await transport.WaitForEndAsync().WaitAsync(Bound);

        Assert.Same(PlaybackEndWatch.AlreadyFailed, result.Error);
    }

    /// <summary>
    /// A player that enters Error while the wait is subscribing, after it began listening for
    /// errors and before it began listening for state changes, raises its error to the wait. The
    /// wait reports that error, not the generic one for a player that was already in Error.
    /// </summary>
    [Fact]
    public async Task FailsWithThePlayersError_WhenItEntersErrorWhileTheWaitSubscribes()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        script.States.BeforeNextSubscribe = () => script.Move(PlaybackState.Error);

        var wait = transport.WaitForEndAsync();
        Assert.False(wait.IsCompleted, "The wait gave up before the player said why it failed.");
        script.Raise(Fatal);

        var result = await wait.WaitAsync(Bound);
        Assert.Same(Fatal, result.Error);
    }

    /// <summary>
    /// Playback that ends while the wait is subscribing, after it began listening for errors and
    /// before it began listening for state changes, still ends the wait.
    /// </summary>
    [Fact]
    public async Task CompletesWithSuccess_WhenPlaybackEndsWhileTheWaitSubscribes()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        script.States.BeforeNextSubscribe = () => script.Move(PlaybackState.Ended);

        var wait = transport.WaitForEndAsync();

        Assert.True(wait.IsCompleted, "Playback ended while the wait subscribed, and the wait missed it.");
        Assert.True((await wait.WaitAsync(Bound)).IsSuccess);
    }

    [Fact]
    public async Task Fails_WhenThePlayerIsDisposedWhileWaiting()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        var wait = transport.WaitForEndAsync();

        script.Dispose();

        var result = await wait.WaitAsync(Bound);
        Assert.Same(PlaybackEndWatch.Disposed, result.Error);
    }

    [Fact]
    public async Task Fails_WhenThePlayerWasDisposedBeforeTheCall()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        script.Dispose();

        var result = await transport.WaitForEndAsync().WaitAsync(Bound);

        Assert.Same(PlaybackEndWatch.Disposed, result.Error);
    }

    [Fact]
    public async Task Cancellation_StopsTheWait_AndLetsGoOfThePlayer()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        using var cts = new CancellationTokenSource();
        var wait = transport.WaitForEndAsync(cts.Token);
        Assert.Equal(2, script.Subscribers);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Bound));
        Assert.Equal(0, script.Subscribers);
    }

    [Fact]
    public async Task AnEndedWait_LetsGoOfThePlayer()
    {
        var (transport, script) = ScriptedTransport.Create(PlaybackState.Playing);
        var wait = transport.WaitForEndAsync();

        script.Move(PlaybackState.Ended);
        await wait.WaitAsync(Bound);

        Assert.Equal(0, script.Subscribers);
    }

    // ── Over a real player ──────────────────────────────────────────────────

    /// <summary>
    /// Ended means heard. An item with no video has no pacer to hold its end back, so the
    /// player reported Ended as soon as the audio sink had been handed the last buffer, with up
    /// to a second still queued on the device (#609). It now waits for the sink's drain.
    /// </summary>
    [RequiresFfmpegAndCorpusFact]
    public async Task AnAudioOnlyItem_EndsOnlyOnceTheAudioSinkHasDrained()
    {
        var sink = new DrainGateAudioSink();
        await using var player = await BuildAudioOnlyPlayerAsync(sink);

        var ended = player.WaitForEndAsync();
        Assert.True((await player.PlayAsync()).IsSuccess);

        var first = await Task.WhenAny(sink.DrainStarted, ended).WaitAsync(Bound);
        Assert.True(first == sink.DrainStarted, $"Playback ended without draining the audio sink: {State(ended)}.");

        // The end of stream waits on the drain the sink has not finished.
        Assert.False(ended.IsCompleted, "The wait ended while the audio sink was still draining.");
        Assert.NotEqual(PlaybackState.Ended, player.State);

        sink.ReleaseDrain();

        var result = await ended.WaitAsync(Bound);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(PlaybackState.Ended, player.State);
    }

    [RequiresFfmpegAndCorpusFact]
    public async Task ARealPlayerDisposedWhileDraining_FailsTheWait()
    {
        var sink = new DrainGateAudioSink();
        var player = await BuildAudioOnlyPlayerAsync(sink);

        var ended = player.WaitForEndAsync();
        Assert.True((await player.PlayAsync()).IsSuccess);
        var first = await Task.WhenAny(sink.DrainStarted, ended).WaitAsync(Bound);
        Assert.True(first == sink.DrainStarted, $"Playback ended without draining the audio sink: {State(ended)}.");

        await player.DisposeAsync();

        var result = await ended.WaitAsync(Bound);
        Assert.Same(PlaybackEndWatch.Disposed, result.Error);
    }

    private static async Task<IMediaPlayer> BuildAudioOnlyPlayerAsync(IAudioSink sink)
    {
        var path = TestEnvironment.GetCorpusFile("test-audio-aac.m4a");
        Assert.NotNull(path);
        return await FrameFlowPlayer
            .Create()
            .WithMedia(path!)
            .WithAudioSink(sink)
            .WithHardwareDecode(HardwareDecodeMode.Disabled)
            .BuildPlayerAsync();
    }

    private static string State(Task<Result> wait) =>
        wait.IsCompletedSuccessfully
            ? wait.Result.IsSuccess ? "it succeeded" : wait.Result.Error!.Message
            : wait.Status.ToString();

    /// <summary>Takes everything at once, and holds its drain until the test releases it.</summary>
    private sealed class DrainGateAudioSink : IAudioSink
    {
        private readonly TaskCompletionSource _drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drainReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DrainStarted => _drainStarted.Task;

        public void ReleaseDrain() => _drainReleased.TrySetResult();

        public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
        {
            buffer.Dispose();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
        {
            _drainStarted.TrySetResult();
            await _drainReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The test's handle on a scripted transport.</summary>
    public sealed class Script
    {
        internal PlaybackState State;
        internal readonly ScriptedStream<PlaybackState> States = new();
        internal readonly ScriptedStream<PlaybackError> Errors = new();

        public int Subscribers => States.Count + Errors.Count;

        /// <summary>Moves the transport to <paramref name="state"/> and announces it, as the controller does.</summary>
        public void Move(PlaybackState state)
        {
            State = state;
            States.Next(state);
        }

        public void Raise(PlaybackError error) => Errors.Next(error);

        /// <summary>Completes the streams and refuses later subscriptions, as a disposed player does.</summary>
        public void Dispose()
        {
            States.Complete();
            Errors.Complete();
        }
    }

    /// <summary>An <see cref="IMediaTransport"/> that answers State, StateChanged and ErrorOccurred.</summary>
    public class ScriptedTransport : DispatchProxy
    {
        private Script _script = null!;

        public static (IMediaTransport Transport, Script Script) Create(PlaybackState initial)
        {
            var transport = Create<IMediaTransport, ScriptedTransport>();
            var script = new Script { State = initial };
            ((ScriptedTransport)(object)transport)._script = script;
            return (transport, script);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_State" => _script.State,
                "get_StateChanged" => _script.States,
                "get_ErrorOccurred" => _script.Errors,
                var name => throw new NotSupportedException($"The scripted transport does not answer {name}."),
            };
    }

    /// <summary>
    /// A stream that raises synchronously, and once completed refuses subscriptions with
    /// <see cref="ObjectDisposedException"/>, as the player's streams do.
    /// </summary>
    internal sealed class ScriptedStream<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = new();
        private bool _completed;

        /// <summary>
        /// Runs once, at the start of the next <see cref="Subscribe"/>, before the observer is added:
        /// what the transport does while a subscriber is still on its way.
        /// </summary>
        public Action? BeforeNextSubscribe;

        public int Count
        {
            get
            {
                lock (_observers)
                    return _observers.Count;
            }
        }

        public IDisposable Subscribe(IObserver<T> observer)
        {
            Interlocked.Exchange(ref BeforeNextSubscribe, null)?.Invoke();
            lock (_observers)
            {
                ObjectDisposedException.ThrowIf(_completed, this);
                _observers.Add(observer);
            }
            return new Unsubscriber(this, observer);
        }

        public void Next(T value)
        {
            foreach (var observer in Snapshot())
                observer.OnNext(value);
        }

        public void Complete()
        {
            IObserver<T>[] observers;
            lock (_observers)
            {
                _completed = true;
                observers = [.. _observers];
                _observers.Clear();
            }
            foreach (var observer in observers)
                observer.OnCompleted();
        }

        private IObserver<T>[] Snapshot()
        {
            lock (_observers)
                return [.. _observers];
        }

        private sealed class Unsubscriber(ScriptedStream<T> stream, IObserver<T> observer) : IDisposable
        {
            public void Dispose()
            {
                lock (stream._observers)
                    stream._observers.Remove(observer);
            }
        }
    }
}
