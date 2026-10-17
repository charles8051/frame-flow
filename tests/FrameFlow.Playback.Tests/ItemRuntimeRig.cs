// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using FrameFlow.Decoding.Diagnostics;
using FrameFlow.Playback.Core;
using Microsoft.Extensions.Logging;
using Step = FrameFlow.Playback.SubstrateSession.Step;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// A <see cref="SubstrateSession"/> over <see cref="FakeItemMedia"/> (ADR-0086, decision 6): the
/// session's own sequencing, its scope and its clocks, with media that records each native call
/// and holds each run's work until the test lets it end.
/// </summary>
/// <remarks>
/// Every wait a test makes is on a signal the session or the fake raises: a piece of work starting
/// or ending, a step the session takes, a callback. <see cref="Bound"/> only limits a failure.
/// </remarks>
internal sealed class ItemRuntimeRig : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<int> _endsOfStream = new();
    private readonly ConcurrentQueue<Exception> _workerFaults = new();
    private readonly ConcurrentQueue<PlaybackError> _recoverableErrors = new();
    private readonly ConcurrentQueue<Step> _steps = new();
    private readonly Lock _gate = new();
    private readonly List<(Step Step, TaskCompletionSource Taken)> _awaitedSteps = [];
    private readonly List<(int Count, TaskCompletionSource Raised)> _awaitedEnds = [];

    private readonly TaskCompletionSource<Exception> _workerFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<PlaybackError> _recoverableError = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _letGo = [];
    private readonly List<(string Work, int Count, TaskCompletionSource Done)> _awaitedLetGo = [];
    private readonly ConcurrentQueue<bool> _endsOfStreamInStep = new();
    private readonly bool _latenessRecovery;

    private ItemRuntimeRig(MediaShape shape, bool detached, bool latenessRecovery, TimeProvider? time, TimeProvider? clockTime)
    {
        _latenessRecovery = latenessRecovery;
        Log = new RigLog();
        Media = new FakeItemMedia(shape, Log);
        Opener = new FakeMediaOpener(Media, Log);
        Audio = new RecordingAudioSink(Log);
        Clock = new RigClock(clockTime);
        Session = new SubstrateSession(
            videoSink: null,
            audioSink: shape.Audio ? Audio : null,
            Clock,
            new SessionCallbacks(
                OnEndOfStream: OnEndOfStream,
                OnWorkerFaulted: fault =>
                {
                    _workerFaults.Enqueue(fault);
                    _workerFault.TrySetResult(fault);
                    if (WorkerFaultedThrows)
                        throw new InvalidOperationException("The owner's fault callback threw.");
                },
                OnBufferReady: static () => { },
                OnBufferUnderrun: static () => { },
                OnRecoverableError: error =>
                {
                    _recoverableErrors.Enqueue(error);
                    _recoverableError.TrySetResult(error);
                },
                OnCurrentItemChanged: static _ => { },
                OnLoopRestarted: static (_, _, _) => { },
                OnItemFailed: static (_, _, _) => { }
            ),
            HardwareDecodeMode.Disabled,
            loggerFactory: Warnings,
            wallclockTime: clockTime
        )
        {
            MediaOpener = Opener,
            TimeProvider = time ?? TimeProvider.System,
            Detached = detached,
            LatenessRecovery = latenessRecovery ? new LatenessRecoveryOptions { Enabled = true } : null,
        };
        Session.OnStep = OnStep;
        Session.OnWorkLetGo = OnWorkLetGo;
    }

    public RigLog Log { get; }

    public FakeItemMedia Media { get; }

    public FakeMediaOpener Opener { get; }

    public RecordingAudioSink Audio { get; }

    public RigClock Clock { get; }

    /// <summary>Whether the owner's OnWorkerFaulted callback throws once it has recorded the fault.</summary>
    public bool WorkerFaultedThrows { get; set; }

    /// <summary>Whether the test expects the session's dispose to throw, so the rig's own dispose does not fail on it.</summary>
    public bool DisposeThrows { get; set; }

    /// <summary>What the session logged at Warning and above.</summary>
    public WarningLog Warnings { get; } = new();

    /// <summary>Completes with the first fault the session reports on OnWorkerFaulted.</summary>
    public Task<Exception> WorkerFault => _workerFault.Task;

    /// <summary>Completes with the first error the session reports on OnRecoverableError.</summary>
    public Task<PlaybackError> RecoverableError => _recoverableError.Task;

    public SubstrateSession Session { get; }

    /// <summary>The run number the session read out, inside each end-of-stream callback.</summary>
    public IReadOnlyCollection<int> EndsOfStream => _endsOfStream;

    /// <summary>For each end of stream, whether the session raised it while it performed a step.</summary>
    public IReadOnlyCollection<bool> EndsOfStreamInStep => _endsOfStreamInStep;

    public IReadOnlyCollection<Exception> WorkerFaults => _workerFaults;

    public IReadOnlyCollection<PlaybackError> RecoverableErrors => _recoverableErrors;

    /// <summary>The steps the session has taken, in order.</summary>
    public Step[] Steps => [.. _steps];

    /// <summary>A rig whose session is not yet opened.</summary>
    /// <param name="shape">What the fake media holds.</param>
    /// <param name="detached">Whether the session is prepared ahead.</param>
    /// <param name="latenessRecovery">Whether each run has a lateness-recovery walk.</param>
    /// <param name="time">Whose timers time a stop's warning. Null is the system's.</param>
    /// <param name="clockTime">
    /// Whose time the position clock and the session's own wallclock read. Null is the system's.
    /// </param>
    public static ItemRuntimeRig Create(
        MediaShape? shape = null,
        bool detached = false,
        bool latenessRecovery = false,
        TimeProvider? time = null,
        TimeProvider? clockTime = null
    ) => new(shape ?? MediaShape.AudioAndVideo, detached, latenessRecovery, time, clockTime);

    /// <summary>Opens the session, positioned at <paramref name="start"/>: no run starts.</summary>
    public async Task<ItemRuntimeRig> OpenAsync(TimeSpan start = default, TimeSpan? end = null)
    {
        await Session.InitializeAsync(new FakeSource(), start, end, default).AsTask().WaitAsync(Bound);
        return this;
    }

    /// <summary>
    /// Opens, warms up and plays the session: its first run is going, and each piece of its work
    /// has started.
    /// </summary>
    public async Task<ItemRuntimeRig> PlayingAsync()
    {
        await OpenAsync();
        await Session.WarmUpAsync().AsTask().WaitAsync(Bound);
        await Session.PlayAsync().AsTask().WaitAsync(Bound);

        // The warm-up waits for the graph's first frame; the pump and the walk start on the pool
        // and may not have yet.
        await Media.Started(ItemWork.Pump).WaitAsync(Bound);
        if (_latenessRecovery)
            await Media.Started(ItemWork.Recovery).WaitAsync(Bound);
        return this;
    }

    /// <summary>
    /// Completes once the session's run scopes have let <paramref name="count"/> pieces of the
    /// work named <paramref name="work"/> go: the session has been told how each ended.
    /// </summary>
    public Task LetGo(string work, int count = 1)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_letGo.Count(w => w == work) >= count)
                done.TrySetResult();
            else
                _awaitedLetGo.Add((work, count, done));
        }
        return done.Task;
    }

    private void OnWorkLetGo(string work)
    {
        List<TaskCompletionSource> done = [];
        lock (_gate)
        {
            _letGo.Add(work);
            var seen = _letGo.Count(w => w == work);
            _awaitedLetGo.RemoveAll(a =>
            {
                if (a.Work != work || seen < a.Count)
                    return false;
                done.Add(a.Done);
                return true;
            });
        }
        foreach (var d in done)
            d.TrySetResult();
    }

    /// <summary>Completes the next time the session takes <paramref name="step"/>.</summary>
    public Task Next(Step step)
    {
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _awaitedSteps.Add((step, taken));
        return taken.Task;
    }

    /// <summary>Completes once the session has raised <paramref name="count"/> ends of stream.</summary>
    public Task EndOfStream(int count = 1)
    {
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_endsOfStream.Count >= count)
                raised.TrySetResult();
            else
                _awaitedEnds.Add((count, raised));
        }
        return raised.Task;
    }

    /// <summary>
    /// Seeks to <paramref name="target"/>, and cancels the seek once it waits for the run to end.
    /// </summary>
    /// <remarks>
    /// The run has to be held by work that ignores its cancellation
    /// (<see cref="FakeItemMedia.IgnoresCancellation"/>). The seek cancels the run as it arrives,
    /// and work that ends on that cancellation lets the seek run to its end before the cancel below
    /// reaches it (#776). The cancel waits until the run's cancellation has reached held work, so
    /// the seek is still waiting when it comes.
    /// </remarks>
    public async Task SeekCutShortAsync(TimeSpan target)
    {
        using var cancel = new CancellationTokenSource();
        var held = Media.HeldThroughCancellation();
        var waiting = Next(Step.WaitingForRun);
        var seek = Session.SeekAsync(target, cancel.Token).AsTask();

        var first = await Task.WhenAny(waiting, seek).WaitAsync(Bound);
        Assert.True(first == waiting, $"The seek to {target} did not wait for the run.");
        var holding = await Task.WhenAny(held, seek).WaitAsync(Bound);
        Assert.True(holding == held, $"The run ended before the seek to {target} was cut short: no work held it.");

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek.WaitAsync(Bound));
    }

    /// <summary>A step at which the test hook throws, once it has recorded the step.</summary>
    public Step? ThrowAtStep { get; set; }

    private void OnStep(Step step)
    {
        _steps.Enqueue(step);
        if (ThrowAtStep == step)
        {
            SignalStep(step);
            throw new InvalidOperationException($"The test hook threw at {step}.");
        }
        SignalStep(step);
    }

    private void SignalStep(Step step)
    {
        List<TaskCompletionSource> taken = [];
        lock (_gate)
        {
            _awaitedSteps.RemoveAll(w =>
            {
                if (w.Step != step)
                    return false;
                taken.Add(w.Taken);
                return true;
            });
        }
        foreach (var t in taken)
            t.TrySetResult();
    }

    private void OnEndOfStream()
    {
        _endsOfStreamInStep.Enqueue(Session.IsPerformingStep);
        _endsOfStream.Enqueue(Session.RunNumber);
        List<TaskCompletionSource> raised = [];
        lock (_gate)
        {
            _awaitedEnds.RemoveAll(w =>
            {
                if (_endsOfStream.Count < w.Count)
                    return false;
                raised.Add(w.Raised);
                return true;
            });
        }
        foreach (var r in raised)
            r.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        // Whatever a test left held is let go, so the session's dispose does not wait on it.
        Media.ReleaseAll();
        Opener.Release();
        Clock.FailPosition = false;
        Clock.FailPause = false;
        Clock.FailStop = false;
        ThrowAtStep = null;
        try
        {
            await Session.DisposeAsync().AsTask().WaitAsync(Bound);
        }
        catch (Exception) when (Media.FailFree != ItemResources.None || DisposeThrows)
        {
            // A test that fails a free has asserted on it already.
        }
    }
}

/// <summary>Keeps what is logged at Warning and above, for every category.</summary>
internal sealed class WarningLog : ILoggerFactory, ILogger
{
    private readonly List<string> _lines = [];

    public string[] Lines
    {
        get
        {
            lock (_lines)
                return [.. _lines];
        }
    }

    public ILogger CreateLogger(string categoryName) => this;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (logLevel < LogLevel.Warning)
            return;
        lock (_lines)
            _lines.Add(formatter(state, exception));
    }
}

/// <summary>What the fake media holds: which streams have a decoder, and whether there is a pacer.</summary>
internal sealed record MediaShape(bool Video, bool Audio, bool Pacer)
{
    public static readonly MediaShape AudioAndVideo = new(Video: true, Audio: true, Pacer: true);
    public static readonly MediaShape VideoOnly = new(Video: true, Audio: false, Pacer: true);
    public static readonly MediaShape AudioOnly = new(Video: false, Audio: true, Pacer: false);
    public static readonly MediaShape Nothing = new(Video: false, Audio: false, Pacer: false);
}

/// <summary>The calls the rig's fakes record, in order, each with what work was in flight.</summary>
internal sealed class RigLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<string> _violations = [];

    public string[] Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    /// <summary>The calls made while work that uses what they touch was in flight, which none should be.</summary>
    public string[] Violations
    {
        get
        {
            lock (_gate)
                return [.. _violations];
        }
    }

    public void Add(string call)
    {
        lock (_gate)
            _calls.Add(call);
    }

    public void Violation(string what)
    {
        lock (_gate)
            _violations.Add(what);
    }

    /// <summary>The calls that start with one of <paramref name="prefixes"/>, in order.</summary>
    public string[] Of(params string[] prefixes) =>
        [.. Calls.Where(c => prefixes.Any(p => c.StartsWith(p, StringComparison.Ordinal)))];

    public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

    public int IndexOf(string prefix, int from = 0)
    {
        var calls = Calls;
        for (var i = from; i < calls.Length; i++)
        {
            if (calls[i].StartsWith(prefix, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    public int LastIndexOf(string prefix) =>
        Array.FindLastIndex(Calls, c => c.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>
/// One piece of a run's work the fake media runs: a pump, a graph or a recovery walk. It runs until
/// the test completes it or faults it, or until the run is cancelled; one that ignores
/// cancellation, as a sink of a caller's own may, runs until the test releases it.
/// </summary>
internal sealed class FakeWork
{
    private readonly TaskCompletionSource<Exception?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _heldThroughCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeWork(ItemWork kind, int run, CancellationToken token, bool ignoresCancellation)
    {
        Kind = kind;
        Run = run;
        Token = token;
        IgnoresCancellation = ignoresCancellation;
        if (ignoresCancellation)
        {
            token.UnsafeRegister(
                static state =>
                {
                    var work = (FakeWork)state!;
                    if (work.Holding)
                        work._heldThroughCancellation.TrySetResult();
                },
                this
            );
        }
    }

    public ItemWork Kind { get; }

    /// <summary>Which of the media's runs it belongs to, counted from one.</summary>
    public int Run { get; }

    public CancellationToken Token { get; }

    public bool IgnoresCancellation { get; }

    /// <summary>Completes once the work has ended, however it ended.</summary>
    public Task Ended => _ended.Task;

    /// <summary>Whether the work ignores its cancellation and has not been let go.</summary>
    public bool Holding => IgnoresCancellation && !_release.Task.IsCompleted;

    /// <summary>
    /// Completes once its run's cancellation has reached the work while it held: the work goes on,
    /// and keeps its run going, until the test lets it go.
    /// </summary>
    public Task HeldThroughCancellation => _heldThroughCancellation.Task;

    /// <summary>Ends the work as having run to its end.</summary>
    public void Complete() => _release.TrySetResult(null);

    /// <summary>Ends the work with <paramref name="error"/>.</summary>
    public void Fault(Exception error) => _release.TrySetResult(error);

    /// <summary>Lets work that ignores its cancellation end: as cancelled if it was, as completed if not.</summary>
    public void Release() => _release.TrySetResult(null);

    /// <summary>
    /// Ends the work as having run to its end, even if its run was cancelled meanwhile: it had
    /// finished before it looked.
    /// </summary>
    public void Finish()
    {
        _finished = true;
        _release.TrySetResult(null);
    }

    private volatile bool _finished;

    internal async Task RunAsync(Action<FakeWork> exit)
    {
        try
        {
            var fault = IgnoresCancellation
                ? await _release.Task.ConfigureAwait(false)
                : await _release.Task.WaitAsync(Token).ConfigureAwait(false);
            if (fault is not null)
                throw fault;
            if (!_finished)
                Token.ThrowIfCancellationRequested();
        }
        finally
        {
            exit(this);
            _ended.TrySetResult();
        }
    }
}

/// <summary>
/// Media that records each call the session makes, as the native call it stands for, and runs each
/// piece of a run's work as a <see cref="FakeWork"/> the test controls. A seek, a reset, a run's
/// start, a flush of the pacer or a free made while any work is in flight is a violation.
/// </summary>
/// <remarks>
/// A piece of work is set up from the hooks (<see cref="IgnoresCancellation"/>,
/// <see cref="FaultOnStart"/>, <see cref="ThrowOnCancel"/>, <see cref="FirstFrameOnGraphStart"/>)
/// before it can be seen to start, so a test may change a hook once it has seen the start, and
/// the change applies from the next piece.
/// </remarks>
internal sealed class FakeItemMedia : IItemMedia
{
    private readonly RigLog _log;
    private readonly MediaShape _shape;
    private readonly Lock _gate = new();
    private readonly Dictionary<ItemWork, int> _inFlight = [];
    private readonly Dictionary<ItemWork, int> _mostAtOnce = [];
    private readonly List<FakeWork> _works = [];
    private readonly List<TimelineId> _timelines = [];
    private readonly List<(ItemWork Kind, int Index, TaskCompletionSource<FakeWork> Started)> _awaited = [];
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ItemResources _resources;
    private int _runs;
    private TimeSpan _floor;
    private TaskCompletionSource? _seekHold;

    public FakeItemMedia(MediaShape shape, RigLog log)
    {
        _shape = shape;
        _log = log;
        _resources =
            ItemResources.Demux
            | ItemResources.Pipeline
            | (shape.Video ? ItemResources.VideoDecoder : ItemResources.None)
            | (shape.Audio ? ItemResources.AudioDecoder : ItemResources.None);
    }

    /// <summary>The kinds of work that ignore their cancellation until the test releases them.</summary>
    public ItemWork IgnoresCancellation { get; set; }

    /// <summary>Whether the first graph to start decodes the first frame at once. On by default.</summary>
    public bool FirstFrameOnGraphStart { get; set; } = true;

    /// <summary>Whether <see cref="OpenPacer"/> throws, as a pacer that cannot be built does.</summary>
    public Exception? FailPacer { get; set; }

    /// <summary>The resource whose free throws.</summary>
    public ItemResources FailFree { get; set; }

    /// <summary>Whether the next run's graph cannot be built.</summary>
    public Exception? FailBeginRun { get; set; }

    /// <summary>The kinds of work that fault as soon as they start, and what they fault with.</summary>
    public ItemWork FaultOnStart { get; set; }

    /// <summary>
    /// The kinds of work that register a callback on their run's token which throws when the run
    /// is cancelled, as a faulty cancellation callback in a sink would.
    /// </summary>
    public ItemWork ThrowOnCancel { get; set; }

    public Exception CancelFault { get; set; } = new InvalidOperationException("A cancellation callback threw.");

    public Exception StartFault { get; set; } = new InvalidOperationException("The work faulted as it started.");

    /// <summary>
    /// Called on a piece of work's own thread as soon as it can be seen to have started, before it
    /// goes on: the earliest a test that waits for a start can act on it.
    /// </summary>
    public Action<FakeWork>? OnWorkSeen { get; set; }

    private TaskCompletionSource? _closeGatesHold;
    private readonly TaskCompletionSource _closeGatesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _settleHold;
    private readonly TaskCompletionSource _settleEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Holds the next close of the gates, on the thread the session closes them on, until
    /// <see cref="ReleaseCloseGates"/>: the session performs nothing else meanwhile.
    /// </summary>
    public void HoldCloseGates()
    {
        lock (_gate)
            _closeGatesHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes once a held close of the gates has begun.</summary>
    public Task CloseGatesEntered => _closeGatesEntered.Task;

    public void ReleaseCloseGates()
    {
        TaskCompletionSource? hold;
        lock (_gate)
        {
            hold = _closeGatesHold;
            _closeGatesHold = null;
        }
        hold?.TrySetResult();
    }

    /// <summary>Holds the next settle until <see cref="ReleaseSettle"/>, or until its token is cancelled.</summary>
    public void HoldSettle()
    {
        lock (_gate)
            _settleHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes once a held settle has begun.</summary>
    public Task SettleEntered => _settleEntered.Task;

    public void ReleaseHeldSettle()
    {
        TaskCompletionSource? hold;
        lock (_gate)
        {
            hold = _settleHold;
            _settleHold = null;
        }
        hold?.TrySetResult();
    }

    public bool Opened { get; set; }

    public ItemResources Resources
    {
        get
        {
            lock (_gate)
                return _resources;
        }
    }

    public MediaInfo? MediaInfo => null;

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);

    public Task? FirstFrameDecoded => _shape.Video ? _firstFrame.Task : null;

    public HardwareDecodeBackendKind? BoundBackend => null;

    /// <summary>How many runs the session has started over this media.</summary>
    public int Runs
    {
        get
        {
            lock (_gate)
                return _runs;
        }
    }

    public void DecodeFirstFrame() => _firstFrame.TrySetResult();

    /// <summary>The most pieces of <paramref name="kind"/> that were in flight at once.</summary>
    public int MostAtOnce(ItemWork kind)
    {
        lock (_gate)
            return _mostAtOnce.GetValueOrDefault(kind);
    }

    /// <summary>Whether any work is in flight.</summary>
    public bool Quiet
    {
        get
        {
            lock (_gate)
                return _inFlight.Values.All(n => n == 0);
        }
    }

    /// <summary>Completes with the <paramref name="index"/>-th piece of <paramref name="kind"/> to start, counted from one.</summary>
    public Task<FakeWork> Started(ItemWork kind, int index = 1)
    {
        var started = new TaskCompletionSource<FakeWork>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var existing = _works.Where(w => w.Kind == kind).Skip(index - 1).FirstOrDefault();
            if (existing is not null)
                started.TrySetResult(existing);
            else
                _awaited.Add((kind, index, started));
        }
        return started.Task;
    }

    /// <summary>The latest piece of <paramref name="kind"/> to start.</summary>
    public FakeWork Latest(ItemWork kind)
    {
        lock (_gate)
            return _works.Last(w => w.Kind == kind);
    }

    /// <summary>
    /// Completes once a run's cancellation reaches a piece of work that holds now
    /// (<see cref="FakeWork.Holding"/>). With none, it never completes.
    /// </summary>
    public Task HeldThroughCancellation()
    {
        lock (_gate)
        {
            var holding = _works.Where(w => w.Holding).Select(w => w.HeldThroughCancellation).ToArray();
            return holding.Length == 0 ? new TaskCompletionSource().Task : Task.WhenAny(holding);
        }
    }

    /// <summary>
    /// Holds the next demuxer seek until <see cref="ReleaseSeek"/>, whatever its token, as a call
    /// that cannot be interrupted would. Released, it ends as cancelled if its token was.
    /// </summary>
    public void HoldSeeks()
    {
        lock (_gate)
            _seekHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseSeek()
    {
        TaskCompletionSource? hold;
        lock (_gate)
        {
            hold = _seekHold;
            _seekHold = null;
        }
        hold?.TrySetResult();
    }

    /// <summary>Lets every piece of work end, and releases a held seek, gate close or settle.</summary>
    public void ReleaseAll()
    {
        FakeWork[] works;
        lock (_gate)
            works = [.. _works];
        foreach (var work in works)
            work.Release();
        ReleaseSeek();
        ReleaseCloseGates();
        ReleaseHeldSettle();
    }

    /// <summary>The clock the session gave the pacer to present against.</summary>
    public IClockSource? Master { get; private set; }

    public void OpenPacer(IClockSource master)
    {
        _log.Add("OpenPacer");
        if (FailPacer is { } failure)
            throw failure;
        Master = master;
        if (_shape.Pacer)
        {
            lock (_gate)
                _resources |= ItemResources.Pacer;
        }
    }

    public void OpenGates() => _log.Add("OpenGates");

    public void CloseGates()
    {
        _log.Add("CloseGates");
        Task? hold;
        lock (_gate)
            hold = _closeGatesHold?.Task;
        if (hold is null)
            return;
        _closeGatesEntered.TrySetResult();
        hold.Wait(ItemRuntimeRig.Bound);
    }

    public void FlushPacer() => Native("FlushPacer");

    public void BeginRun(TimelineId timeline, ItemGraphPolicy graph, TimeSpan floor, TimeSpan? end, bool holdForSettle)
    {
        Native($"BeginRun({graph}, floor {floor.TotalSeconds:0.###}, end {end?.TotalSeconds.ToString("0.###") ?? "none"}, hold {holdForSettle})");
        if (FailBeginRun is { } failure)
        {
            FailBeginRun = null;
            throw failure;
        }
        lock (_gate)
        {
            _runs++;
            _floor = floor;
            _timelines.Add(timeline);
        }
    }

    /// <summary>The timeline each run started on, in the order the runs started.</summary>
    public IReadOnlyList<TimelineId> RunTimelines
    {
        get
        {
            lock (_gate)
                return [.. _timelines];
        }
    }

    public Task RunPumpAsync(CancellationToken cancellationToken) => Run(ItemWork.Pump, cancellationToken);

    public Task RunGraphAsync(CancellationToken cancellationToken)
    {
        var firstFrame = FirstFrameOnGraphStart;
        var running = Run(ItemWork.Graph, cancellationToken);
        if (firstFrame)
            _firstFrame.TrySetResult();
        return running;
    }

    public Task RunRecoveryWalkAsync(CancellationToken cancellationToken) => Run(ItemWork.Recovery, cancellationToken);

    private Task Run(ItemWork kind, CancellationToken token)
    {
        FakeWork work;
        List<TaskCompletionSource<FakeWork>> started = [];
        lock (_gate)
        {
            // Set up from the hooks before anything can see it start (#750): a test that waits for
            // the start and then changes a hook changes the next work's, not this one's.
            work = new FakeWork(kind, _runs, token, (IgnoresCancellation & kind) != 0);
            if ((FaultOnStart & kind) != 0)
                work.Fault(StartFault);
            if ((ThrowOnCancel & kind) != 0)
            {
                var fault = CancelFault;
                token.Register(() => throw fault);
            }

            _works.Add(work);
            var now = _inFlight.GetValueOrDefault(kind) + 1;
            _inFlight[kind] = now;
            _mostAtOnce[kind] = Math.Max(_mostAtOnce.GetValueOrDefault(kind), now);
            var index = _works.Count(w => w.Kind == kind);
            _awaited.RemoveAll(a =>
            {
                if (a.Kind != kind || a.Index != index)
                    return false;
                started.Add(a.Started);
                return true;
            });
        }
        OnWorkSeen?.Invoke(work);
        _log.Add($"{kind} started");
        foreach (var s in started)
            s.TrySetResult(work);
        return work.RunAsync(Exit);
    }

    private void Exit(FakeWork work)
    {
        lock (_gate)
            _inFlight[work.Kind]--;
        _log.Add($"{work.Kind} ended");
    }

    public async ValueTask SeekAsync(TimeSpan target, CancellationToken cancellationToken)
    {
        Native($"Seek({target.TotalSeconds:0.###})");
        Task? hold;
        lock (_gate)
            hold = _seekHold?.Task;
        if (hold is not null)
            await hold.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void ResetForSeek() => Native("ResetForSeek");

    public long PacerRun
    {
        get
        {
            lock (_gate)
                return _runs;
        }
    }

    public async Task<TimeSpan?> WaitForSeekTargetAsync(long run, TimeSpan cap, CancellationToken cancellationToken)
    {
        _log.Add($"Settle({run})");
        Task? hold;
        lock (_gate)
            hold = _settleHold?.Task;
        if (hold is not null)
        {
            _settleEntered.TrySetResult();
            await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        lock (_gate)
            return run == _runs && _floor > TimeSpan.Zero ? _floor : null;
    }

    public void ReleaseSettle(long run) => _log.Add($"ReleaseSettle({run})");

    public ValueTask FreeAsync(ItemResources resource)
    {
        Native($"Free({resource})");
        lock (_gate)
        {
            if ((_resources & resource) == 0)
                _log.Violation($"Free({resource}) of a resource not held");
            _resources &= ~resource;
        }
        if ((FailFree & resource) != 0)
            throw new InvalidOperationException($"Freeing the {resource} failed.");
        return ValueTask.CompletedTask;
    }

    public DecodedMediaStreamDiagnosticsSnapshot StreamDiagnostics => DecodedMediaStreamDiagnosticsSnapshot.Empty;

    public int VideoFramesDroppedLate => 0;

    public TimeSpan? VideoPresentationLag => null;

    // A call that touches what the run's work uses: recorded, and a violation if any work is in flight.
    private void Native(string call)
    {
        string? inFlight;
        lock (_gate)
        {
            var running = _inFlight.Where(p => p.Value > 0).Select(p => p.Key.ToString()).ToArray();
            inFlight = running.Length == 0 ? null : string.Join(", ", running);
        }
        _log.Add(call);
        if (inFlight is not null)
            _log.Violation($"{call} while {inFlight} ran");
    }
}

/// <summary>Opens <see cref="FakeItemMedia"/>, at once or when the test releases it.</summary>
internal sealed class FakeMediaOpener(FakeItemMedia media, RigLog log) : IItemMediaOpener
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the open waits for <see cref="Release"/>, or for its token to be cancelled.</summary>
    public bool Hold { get; set; }

    /// <summary>Whether a held open ignores its token, as one over a source that stalls may.</summary>
    public bool IgnoresCancellation { get; set; }

    /// <summary>What the open throws, having freed what it opened.</summary>
    public Exception? Fail { get; set; }

    /// <summary>Completes once the open has begun.</summary>
    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public async ValueTask<IItemMedia> OpenAsync(IMediaSource source, CancellationToken cancellationToken)
    {
        log.Add("Open");
        _entered.TrySetResult();
        if (Hold && IgnoresCancellation)
            await _released.Task.ConfigureAwait(false);
        else if (Hold)
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail is { } failure)
            throw failure;
        media.Opened = true;
        return media;
    }
}

/// <summary>An audio sink that records each call into the rig's log.</summary>
internal sealed class RecordingAudioSink(RigLog log) : IAudioSink
{
    /// <summary>The call that throws: "Activate", "Pause", "Resume" or "Deactivate".</summary>
    public string? Fail { get; set; }

    public ValueTask PresentAsync(IAudioBuffer buffer, CancellationToken ct)
    {
        buffer.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => Call("Activate", cancellationToken);

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) => Call("Pause", cancellationToken);

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => Call("Resume", cancellationToken);

    public ValueTask DeactivateAsync(CancellationToken cancellationToken = default) => Call("Deactivate", cancellationToken);

    private ValueTask Call(string what, CancellationToken cancellationToken)
    {
        log.Add($"Audio.{what}");
        if (Fail == what)
            return ValueTask.FromException(new InvalidOperationException($"The audio sink's {what} failed."));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The position clock, whose position read can be made to throw, as an app's clock may.</summary>
/// <param name="time">Whose time it reads. Null is the system's.</param>
internal sealed class RigClock(TimeProvider? time = null) : IPlaybackClock
{
    private readonly PlaybackClock _inner = new(time ?? TimeProvider.System);

    /// <summary>Whether reading the position throws.</summary>
    public bool FailPosition { get; set; }

    /// <summary>Whether pausing throws.</summary>
    public bool FailPause { get; set; }

    /// <summary>Whether stopping throws.</summary>
    public bool FailStop { get; set; }

    public TimeSpan Position =>
        FailPosition ? throw new InvalidOperationException("The clock's position cannot be read.") : _inner.Position;

    public bool IsRunning => _inner.IsRunning;

    public bool IsPaused => _inner.IsPaused;

    public void Start(TimeSpan startPosition) => _inner.Start(startPosition);

    public void Pause()
    {
        if (FailPause)
            throw new InvalidOperationException("The clock cannot be paused.");
        _inner.Pause();
    }

    public void Resume() => _inner.Resume();

    public void Seek(TimeSpan position) => _inner.Seek(position);

    public void Stop()
    {
        if (FailStop)
            throw new InvalidOperationException("The clock cannot be stopped.");
        _inner.Stop();
    }
}

/// <summary>A source the fake opener never reads.</summary>
internal sealed class FakeSource : IMediaSource
{
    public string DisplayName => "fake";

    public Uri? Uri => null;

    public string? FilePath => null;

    public bool IsSeekable => true;
}
