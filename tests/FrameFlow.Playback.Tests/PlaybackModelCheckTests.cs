// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using Xunit.Abstractions;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// A bounded model check of <see cref="PlaybackProtocol"/>: every sequence of commands and session
/// reports, breadth-first to <see cref="Depth"/> from <see cref="InternalPlaybackState.Idle"/>, with
/// the rules below checked on every state reached.
/// </summary>
/// <remarks>
/// <para>
/// The model plays the controller's shell. It performs each decision's actions on a model of what
/// the shell holds: whether a session exists, the session's generation and run, and the position
/// clock. It re-enters the core with each auto-chained trigger, and routes a failed initialize as
/// the shell does, to <see cref="PlaybackTrigger.FatalError"/>, or to
/// <see cref="PlaybackTrigger.NothingToPlay"/> for a reload that found nothing it could open.
/// </para>
/// <para>
/// An end of stream is raised by the session's current run and delivered later, so a seek, a reload
/// or another end of stream may come in between. Up to two are in flight. Delivery asks
/// <see cref="PlaybackProtocol.Freshness"/>, as the dispatch loop does, and only a current one
/// reaches the core.
/// </para>
/// <para>
/// The rules: the core is total; at <see cref="InternalPlaybackState.Ended"/> the clock is not
/// running; an end of stream from a run or session that has been replaced changes nothing; a
/// reload that opens nothing never reaches <see cref="InternalPlaybackState.Error"/>; every state
/// recovers to <see cref="InternalPlaybackState.Paused"/> by an unload and a load, without a
/// reset; and every state of the machine is reached.
/// </para>
/// </remarks>
public sealed class PlaybackModelCheckTests(ITestOutputHelper output)
{
    private const int Depth = 9;

    private enum Clock
    {
        None,
        NotStarted,
        Running,
        Paused,
        Frozen,
    }

    private enum Opens
    {
        Opens,
        Fails,
        OpensNothing,
    }

    private sealed record World(
        InternalPlaybackState State,
        bool HasSession,
        Clock Clock,
        int Generation,
        int Run,
        ImmutableList<TriggerOrigin> InFlight
    )
    {
        public static World Initial { get; } =
            new(InternalPlaybackState.Idle, false, Clock.None, 0, 0, []);

        public string Key =>
            $"{State}|{HasSession}|{Clock}|{Generation}|{Run}|{string.Join(",", InFlight)}";
    }

    private abstract record Command
    {
        public sealed record Load(Opens Opens) : Command;

        public sealed record Fire(PlaybackTrigger Trigger) : Command;

        public sealed record Seek : Command;

        public sealed record RaiseEndOfStream : Command;

        public sealed record Deliver(int Index) : Command;
    }

    private static readonly PlaybackTrigger[] Fired =
    [
        PlaybackTrigger.Play,
        PlaybackTrigger.Pause,
        PlaybackTrigger.Unload,
        PlaybackTrigger.Reset,
        PlaybackTrigger.FatalError,
        PlaybackTrigger.BufferUnderrun,
        PlaybackTrigger.BufferReady,
    ];

    [Fact]
    public void EveryCommandSequence_KeepsThePlaybackRules()
    {
        var seen = new HashSet<InternalPlaybackState>();
        var visited = new Dictionary<string, string> { [World.Initial.Key] = "" };
        var frontier = new List<(World World, string Path)> { (World.Initial, "") };
        var deliveredCurrent = false;

        for (var depth = 0; depth < Depth && frontier.Count > 0; depth++)
        {
            var next = new List<(World, string)>();
            foreach (var (world, path) in frontier)
            {
                foreach (var command in CommandsFrom(world))
                {
                    var trail = $"{path} {Describe(command)}";
                    var after = Apply(world, command, seen, trail);

                    if (command is Command.Deliver d)
                    {
                        var origin = world.InFlight[d.Index];
                        var current = new TriggerOrigin(world.Generation, world.Run);
                        if (origin != current)
                            Check(
                                after.State == world.State && after.Clock == world.Clock,
                                "An end of stream from a replaced run or session changes nothing",
                                trail
                            );
                        else if (world.State == InternalPlaybackState.Playing)
                            deliveredCurrent |= after.State == InternalPlaybackState.Ended;
                    }

                    if (command is Command.Load { Opens: Opens.OpensNothing })
                        Check(
                            after.State != InternalPlaybackState.Error,
                            "A reload that opens nothing ends, and does not fail into Error",
                            trail
                        );

                    CheckState(after, trail);
                    if (visited.TryAdd(after.Key, trail))
                        next.Add((after, trail));
                }
            }
            frontier = next;
        }

        output.WriteLine($"{visited.Count} states to depth {Depth}.");
        Check(deliveredCurrent, "A current end of stream ends playback", "(coverage)");
        Assert.Equal(
            Enum.GetValues<InternalPlaybackState>().Order(),
            seen.Order()
        );
    }

    private static IEnumerable<Command> CommandsFrom(World world)
    {
        foreach (var opens in Enum.GetValues<Opens>())
            yield return new Command.Load(opens);
        foreach (var trigger in Fired)
            yield return new Command.Fire(trigger);
        yield return new Command.Seek();
        if (world.HasSession && world.InFlight.Count < 2 && IsRunning(world.State))
            yield return new Command.RaiseEndOfStream();
        for (var i = 0; i < world.InFlight.Count; i++)
            yield return new Command.Deliver(i);
    }

    // A session raises an end of stream only from a run that is decoding.
    private static bool IsRunning(InternalPlaybackState state) =>
        state is InternalPlaybackState.Playing or InternalPlaybackState.Paused or InternalPlaybackState.Rebuffering;

    private static World Apply(World world, Command command, HashSet<InternalPlaybackState> seen, string trail) =>
        command switch
        {
            Command.Load l => Run(world, PlaybackTrigger.Load, l.Opens, seen, trail),
            Command.Fire f => Run(world, f.Trigger, Opens.Opens, seen, trail),
            // A seek out of Ended goes through the machine. Any other seek on a session is the seek
            // machine's, which leaves the playback state as it is. Either way the session starts a
            // new run.
            Command.Seek when world.State == InternalPlaybackState.Ended && world.HasSession =>
                Run(world, PlaybackTrigger.Seek, Opens.Opens, seen, trail) with { Run = world.Run + 1 },
            Command.Seek when world.HasSession && IsRunning(world.State) => world with { Run = world.Run + 1 },
            Command.Seek => world,
            Command.RaiseEndOfStream => world with
            {
                InFlight = world.InFlight.Add(new TriggerOrigin(world.Generation, world.Run)),
            },
            Command.Deliver d => Deliver(world with { InFlight = world.InFlight.RemoveAt(d.Index) }, world.InFlight[d.Index], seen, trail),
            _ => throw new InvalidOperationException(),
        };

    private static World Deliver(World world, TriggerOrigin origin, HashSet<InternalPlaybackState> seen, string trail) =>
        PlaybackProtocol.Freshness(
            PlaybackTrigger.LastFrameRendered,
            origin,
            new TriggerOrigin(world.Generation, world.Run)
        ) == TriggerFreshness.Current
            ? Run(world, PlaybackTrigger.LastFrameRendered, Opens.Opens, seen, trail)
            : world;

    // The dispatch loop's interpreter: one decision's actions in order, then its follow-up.
    private static World Run(
        World world,
        PlaybackTrigger trigger,
        Opens opens,
        HashSet<InternalPlaybackState> seen,
        string trail
    )
    {
        PlaybackTrigger? next = trigger;
        for (var fuel = 0; next is { } current; fuel++)
        {
            Check(fuel < 16, "An auto-chain ends", trail);
            next = null;

            var decision = PlaybackProtocol.Advance(world.State, current, new PlaybackInputs(world.HasSession));
            if (!decision.Handled)
            {
                Check(
                    decision.NextState == world.State && decision.Actions.Count == 0,
                    "A trigger the core does not handle leaves the state as it is",
                    trail
                );
                return world;
            }

            world = world with { State = decision.NextState };
            seen.Add(world.State);

            foreach (var action in decision.Actions)
            {
                switch (action.Kind)
                {
                    case PlaybackActionKind.CreateSession:
                        world = world with
                        {
                            HasSession = true,
                            Generation = world.Generation + 1,
                            Run = 0,
                            Clock = Clock.NotStarted,
                        };
                        break;
                    case PlaybackActionKind.InitializeSession when opens != Opens.Opens:
                        // The shell disposes the session, abandons the rest of the decision and
                        // routes the failure from where the decision went.
                        world = world with { HasSession = false, Clock = Clock.None };
                        next = opens == Opens.Fails ? PlaybackTrigger.FatalError : PlaybackTrigger.NothingToPlay;
                        goto Abandoned;
                    case PlaybackActionKind.PlaySession when world.HasSession:
                        world = world with { Clock = Clock.Running };
                        break;
                    case PlaybackActionKind.PauseSession when world.HasSession:
                        world = world with { Clock = Clock.Paused };
                        break;
                    case PlaybackActionKind.FreezeClock when world.HasSession:
                        world = world with { Clock = Clock.Frozen };
                        break;
                    case PlaybackActionKind.DisposeSession:
                        world = world with { HasSession = false, Clock = Clock.None };
                        break;
                    case PlaybackActionKind.FireTrigger:
                        next = action.FollowUp;
                        break;
                }
            }

            Abandoned:
            // Only the initial trigger carries the load's outcome.
            opens = Opens.Opens;
        }

        return world;
    }

    private static void CheckState(World world, string trail)
    {
        Check(
            world.State != InternalPlaybackState.Ended || world.Clock != Clock.Running,
            "At Ended the clock is not running",
            trail
        );

        // Nothing has to reset the player: an unload where the state allows one, and a load that
        // opens, reach Paused from every state. The probe's states are not the walk's, so they are
        // kept out of its coverage.
        var probed = new HashSet<InternalPlaybackState>();
        var recovered = world;
        if (PlaybackProtocol.Advance(recovered.State, PlaybackTrigger.Unload, new PlaybackInputs(recovered.HasSession)).Handled)
            recovered = Run(recovered, PlaybackTrigger.Unload, Opens.Opens, probed, trail);
        recovered = Run(recovered, PlaybackTrigger.Load, Opens.Opens, probed, trail);
        Check(
            recovered.State == InternalPlaybackState.Paused,
            $"An unload and a load recover {world.State} without a reset",
            trail
        );
    }

    private static string Describe(Command command) =>
        command switch
        {
            Command.Load l => l.Opens == Opens.Opens ? "Load" : $"Load({l.Opens})",
            Command.Fire f => f.Trigger.ToString(),
            Command.Seek => "Seek",
            Command.RaiseEndOfStream => "RaiseEos",
            Command.Deliver d => $"DeliverEos[{d.Index}]",
            _ => command.ToString(),
        };

    private static void Check(bool holds, string rule, string trail)
    {
        if (!holds)
            Assert.Fail($"{rule}. Broken by:{trail}");
    }
}

/// <summary>
/// A bounded model check of <see cref="PlaylistSessionProtocol"/> and <see cref="PlaylistQueue"/>
/// driven through <see cref="PlaybackProtocol"/>: every sequence of player commands and runtime
/// reports, breadth-first to <see cref="Depth"/>, for playlists whose items play, cannot be opened,
/// or fault once they play.
/// </summary>
/// <remarks>
/// <para>
/// The model plays both shells. The controller's interprets <see cref="PlaybackProtocol"/>'s actions
/// and hands each session command to the session core. The session's steps an input until it is
/// done, feeding back an outcome for each awaited action from the item's behaviour, and posts what
/// the session reports to the controller, which handles it after the command, and drops it if the
/// session has been replaced. Starting from the queue, and replay from Ended, follow the
/// controller's shell: reserve the start, unload, load and play.
/// </para>
/// <para>
/// An item that faults does so as soon as it plays, before any other input. That is one schedule
/// of many, and it is the one in which a failing item could rebuild with nothing in between.
/// </para>
/// <para>
/// The rules: every input settles; an advance never opens an item again after it failed to start
/// in that advance; once any item has played the player never reaches
/// <see cref="InternalPlaybackState.Error"/>, whatever the length of the failing run, its kind or
/// the repeat mode; at <see cref="InternalPlaybackState.Ended"/> the clock is not running; and an
/// end of stream from a run a seek or rewind has replaced changes nothing.
/// </para>
/// <para>
/// Not covered: preparing ahead, jumps and queue edits, buffering reports, cancellation, and the
/// item runtime, which <see cref="ItemRuntimeProtocolWalkTests"/> walks on its own.
/// </para>
/// </remarks>
public sealed class PlaylistModelCheckTests(ITestOutputHelper output)
{
    private const int Depth = 9;
    private const int StepFuel = 400;
    private const int FaultFuel = 40;

    public enum Behaviour
    {
        Plays,
        CannotOpen,
        FaultsOnPlay,

        /// <summary>Opens the first time, and cannot be opened after that.</summary>
        OpensOnce,
    }

    private enum Clock
    {
        None,
        NotStarted,
        Running,
        Paused,
        Frozen,
        Stopped,
    }

    private sealed class Source(string name) : IMediaSource
    {
        public string DisplayName => name;
        public Uri? Uri => null;
        public string? FilePath => null;
        public bool IsSeekable => true;
    }

    private static readonly Exception Boom = new IOException("boom");

    public static TheoryData<string, RepeatMode, Behaviour[]> Scenarios
    {
        get
        {
            var data = new TheoryData<string, RepeatMode, Behaviour[]>();
            foreach (var repeat in Enum.GetValues<RepeatMode>())
            {
                data.Add("two that play", repeat, [Behaviour.Plays, Behaviour.Plays]);

                // A run of failing items after one that plays: shorter than the failure guard
                // allows, and longer.
                foreach (var kind in new[] { Behaviour.CannotOpen, Behaviour.FaultsOnPlay })
                    foreach (var length in new[] { 1, 2, PlaylistFailureGuard.MaxConsecutiveFailures + 1 })
                        data.Add($"one that plays, then {length} {kind}", repeat, [Behaviour.Plays, .. Enumerable.Repeat(kind, length)]);

                data.Add("one that opens once, then one that cannot open", repeat, [Behaviour.OpensOnce, Behaviour.CannotOpen]);
                data.Add("the first cannot open", repeat, [Behaviour.CannotOpen, Behaviour.Plays]);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EveryCommandSequence_KeepsThePlaylistRules(string name, RepeatMode repeat, Behaviour[] behaviours)
    {
        var items = behaviours.Select((_, i) => new PlaylistItem(new Source($"{(char)('a' + i)}"))).ToArray();
        var model = new Model(items, behaviours);
        var initial = new World(
            InternalPlaybackState.Idle,
            Clock.None,
            null,
            0,
            PlaylistQueue.Create(items, repeat),
            0,
            false
        );

        var visited = new HashSet<World> { initial };
        var frontier = new List<(World World, string Path)> { (initial, "") };
        for (var depth = 0; depth < Depth && frontier.Count > 0; depth++)
        {
            var next = new List<(World, string)>();
            foreach (var (world, path) in frontier)
            {
                foreach (var command in Enum.GetValues<Command>())
                {
                    var trail = $"{path} {command}";
                    var after = model.Apply(world, command, trail);
                    if (after is null)
                        continue;
                    CheckWorld(after, trail);
                    if (visited.Add(after))
                        next.Add((after, trail));
                }
            }
            frontier = next;
        }

        output.WriteLine($"{name}, {repeat}: {visited.Count} states to depth {Depth}.");
        // The walk reaches what it is there to check. A first item that cannot open fails the load,
        // as a single source's does.
        if (behaviours[0] == Behaviour.CannotOpen)
            Assert.Contains(visited, w => w.State == InternalPlaybackState.Error);
        else
            Assert.Contains(visited, w => w.EverPlayed);
    }

    private static void CheckWorld(World world, string trail)
    {
        Check(
            world.State != InternalPlaybackState.Error || !world.EverPlayed,
            "Once an item has played, no run of failures puts the player in Error",
            trail
        );
        Check(
            world.State != InternalPlaybackState.Ended || world.Session is null || world.Clock != Clock.Running,
            "At Ended the clock is not running",
            trail
        );
    }

    private static void Check(bool holds, string rule, string trail)
    {
        if (!holds)
            Assert.Fail($"{rule}. Broken by:{trail}");
    }

    private enum Command
    {
        Play,
        Pause,
        Seek,
        EndOfStream,
        StaleEndOfStream,
        Skip,
    }

    private sealed record World(
        InternalPlaybackState State,
        Clock Clock,
        PlaylistSessionState? Session,
        int SessionId,
        PlaylistQueue Queue,
        // The items of OpensOnce that have opened, as a bit mask over the playlist.
        int Opened,
        bool EverPlayed
    );

    private sealed class Model(PlaylistItem[] items, Behaviour[] behaviours)
    {
        private const int Id = 1;
        private readonly MediaInfo[] _infos = items
            .Select(i => new MediaInfo(i.Source.DisplayName, TimeSpan.FromSeconds(10), [], []))
            .ToArray();

        // One command's working copy of the world.
        private InternalPlaybackState _state;
        private Clock _clock;
        private PlaylistSessionState? _session;
        private int _sessionId;
        private PlaylistQueue _queue = null!;
        private int _opened;
        private bool _everPlayed;
        private int _run;
        private string _trail = "";
        private readonly List<(int SessionId, PlaybackTrigger Trigger)> _posted = [];

        public World? Apply(World world, Command command, string trail)
        {
            _state = world.State;
            _clock = world.Clock;
            _session = world.Session;
            _sessionId = world.SessionId;
            _queue = world.Queue;
            _opened = world.Opened;
            _everPlayed = world.EverPlayed;
            _run = world.Session?.Item?.KnownRun ?? 0;
            _trail = trail;
            _posted.Clear();

            switch (command)
            {
                case Command.Play:
                    Play();
                    break;
                case Command.Pause:
                    Run(PlaybackTrigger.Pause);
                    break;
                case Command.Seek:
                    if (!Seek())
                        return null;
                    break;
                case Command.EndOfStream:
                    if (_session?.Item is not { } item)
                        return null;
                    Drive(new PlaylistSessionInput.EndOfStream(_session.Generation, item.KnownRun));
                    break;
                case Command.StaleEndOfStream:
                    if (_session?.Item is not { KnownRun: > 0 } stale)
                        return null;
                    var (session, queue) = (_session, _queue);
                    var actions = Drive(new PlaylistSessionInput.EndOfStream(_session.Generation, stale.KnownRun - 1));
                    Check(
                        Equals(session, _session)
                            && Equals(queue, _queue)
                            && actions.All(a => a is PlaylistSessionAction.Log),
                        "An end of stream from a run a seek or rewind has replaced changes nothing",
                        trail
                    );
                    break;
                case Command.Skip:
                    if (_session is null)
                        return null;
                    Drive(new PlaylistSessionInput.SkipRequested(_session.Generation, _session.Run));
                    break;
            }

            Settle();
            return new World(_state, _clock, _session, _sessionId, _queue, _opened, _everPlayed);
        }

        // ── The controller's shell ─────────────────────────────────────────

        private void Play()
        {
            if (_state == InternalPlaybackState.Idle || (_state == InternalPlaybackState.Ended && _session is null))
            {
                StartFromQueue();
                return;
            }

            if (_state == InternalPlaybackState.Ended)
            {
                // Replay from Ended: refused before the unload when the queue has nothing to start.
                var (queue, reserved) = _queue.ReserveStart();
                if (!reserved)
                    return;
                _queue = queue;
                Run(PlaybackTrigger.Unload);
                if (Load())
                    Run(PlaybackTrigger.Play);
                return;
            }

            Run(PlaybackTrigger.Play);
        }

        private void StartFromQueue()
        {
            var (queue, reserved) = _queue.ReserveStart();
            if (!reserved)
                return;
            _queue = queue;
            if (Load())
                Run(PlaybackTrigger.Play);
            else
                _queue = _queue.ReleaseStart();
        }

        private bool Seek()
        {
            if (_session is null)
                return false;
            if (_state == InternalPlaybackState.Ended)
            {
                if (!PlaybackProtocol.Advance(_state, PlaybackTrigger.Seek, new PlaybackInputs(true)).Handled)
                    return false;
                Run(PlaybackTrigger.Seek);
            }
            if (_session is not null)
                Drive(new PlaylistSessionInput.Seek(Id, TimeSpan.FromSeconds(1)));
            return true;
        }

        private bool _loadFailed;

        private bool Load()
        {
            _loadFailed = false;
            Run(PlaybackTrigger.Load);
            return !_loadFailed;
        }

        private void Run(PlaybackTrigger trigger)
        {
            PlaybackTrigger? next = trigger;
            for (var fuel = 0; next is { } current; fuel++)
            {
                Check(fuel < 16, "An auto-chain ends", _trail);
                next = null;
                var decision = PlaybackProtocol.Advance(_state, current, new PlaybackInputs(_session is not null));
                if (!decision.Handled)
                    return;
                _state = decision.NextState;

                foreach (var action in decision.Actions)
                {
                    if (Perform(action) is { } route)
                    {
                        next = route;
                        break;
                    }
                    if (action.Kind == PlaybackActionKind.FireTrigger)
                        next = action.FollowUp;
                }
            }
        }

        // Performs one controller action. A failure that abandons the decision answers the trigger
        // the shell routes it to.
        private PlaybackTrigger? Perform(PlaybackAction action)
        {
            switch (action.Kind)
            {
                case PlaybackActionKind.CreateSession:
                    _session = PlaylistSessionState.Initial;
                    _sessionId++;
                    _clock = Clock.NotStarted;
                    break;
                case PlaybackActionKind.InitializeSession:
                    if (Result(Drive(new PlaylistSessionInput.Initialize(Id))) is { Kind: not PlaylistOutcome.Ok } failed)
                    {
                        _loadFailed = true;
                        DisposeSession();
                        return failed.Error is NoItemCouldStartException ? PlaybackTrigger.NothingToPlay : PlaybackTrigger.FatalError;
                    }
                    break;
                case PlaybackActionKind.WarmUp when _session is not null:
                    if (Result(Drive(new PlaylistSessionInput.WarmUp(Id))) is { Kind: not PlaylistOutcome.Ok })
                        return PlaybackTrigger.FatalError;
                    break;
                case PlaybackActionKind.PlaySession when _session is not null:
                    _clock = Clock.Running;
                    Drive(new PlaylistSessionInput.Play(Id));
                    break;
                case PlaybackActionKind.PauseSession when _session is not null:
                    _clock = Clock.Paused;
                    Drive(new PlaylistSessionInput.Pause(Id));
                    break;
                case PlaybackActionKind.FreezeClock when _session is not null:
                    _clock = Clock.Frozen;
                    break;
                case PlaybackActionKind.DisposeSession:
                    DisposeSession();
                    break;
            }
            return null;
        }

        private void DisposeSession()
        {
            if (_session is not null)
                Drive(new PlaylistSessionInput.Dispose(), disposing: true);
            _session = null;
            _clock = Clock.None;
        }

        // Handles what the session posted, then lets an item that faults on play fault, until
        // nothing is left to handle.
        private void Settle()
        {
            for (var fuel = 0; ; fuel++)
            {
                Check(fuel < FaultFuel, "A failing item does not rebuild forever", _trail);
                while (_posted.Count > 0)
                {
                    var (sessionId, trigger) = _posted[0];
                    _posted.RemoveAt(0);
                    if (sessionId == _sessionId && _session is not null)
                        Run(trigger);
                }

                if (
                    _session is { Run: PlaylistRunState.Playing, Item: { Played: true } item } session
                    && behaviours[Array.IndexOf(items, item.Item)] == Behaviour.FaultsOnPlay
                    && session.LastFaultedGeneration != session.Generation
                )
                {
                    Drive(new PlaylistSessionInput.Fault(session.Generation, Boom, TimeSpan.Zero));
                    continue;
                }
                return;
            }
        }

        // ── The session's shell ────────────────────────────────────────────

        private List<PlaylistSessionAction> Drive(PlaylistSessionInput input, bool disposing = false)
        {
            var all = new List<PlaylistSessionAction>();
            var failedToStart = new List<PlaylistItem>();
            var next = input;
            for (var fuel = 0; ; fuel++)
            {
                Check(fuel < StepFuel, $"The session settles {input}", _trail);
                var (state, queue, step) = PlaylistSessionProtocol.Step(_session!, _queue, next, new PlaylistStepContext(disposing));
                _session = state;
                _queue = queue;

                foreach (var action in step.Actions)
                {
                    all.Add(action);
                    switch (action)
                    {
                        case PlaylistSessionAction.ReportEndOfStream:
                            _posted.Add((_sessionId, PlaybackTrigger.LastFrameRendered));
                            break;
                        case PlaylistSessionAction.ReportFatal:
                            _posted.Add((_sessionId, PlaybackTrigger.FatalError));
                            break;
                        case PlaylistSessionAction.StopClock:
                            _clock = Clock.Stopped;
                            break;
                        case PlaylistSessionAction.ReportItemFailed { What: PlaylistItemFailure.CouldNotStart } failed:
                            Check(
                                !failedToStart.Contains(failed.Item),
                                "An advance does not open an item again after it failed to start",
                                _trail
                            );
                            failedToStart.Add(failed.Item);
                            break;
                    }
                }

                if (_session.Item is { Played: true })
                    _everPlayed = true;

                if (step.Awaited is { } awaited)
                {
                    all.Add(awaited);
                    next = Outcome(awaited);
                    continue;
                }
                if (step.Done)
                    return all;
                next = new PlaylistSessionInput.Continue();
            }
        }

        // The item runtime, as the session sees it.
        private PlaylistSessionInput.Outcome Outcome(PlaylistSessionAction action)
        {
            switch (action)
            {
                case PlaylistSessionAction.OpenItem open:
                    var index = Array.FindIndex(items, i => ReferenceEquals(i.Source, open.Source));
                    var bit = 1 << index;
                    var opens = behaviours[index] switch
                    {
                        Behaviour.CannotOpen => false,
                        Behaviour.OpensOnce => (_opened & bit) == 0,
                        _ => true,
                    };
                    if (!opens)
                        return new(PlaylistOutcome.Failed, Error: Boom);
                    _opened |= bit;
                    _run = 0;
                    return new(PlaylistOutcome.Ok, _run, _infos[index]);
                case PlaylistSessionAction.PlayItem:
                    _clock = Clock.Running;
                    return new(PlaylistOutcome.Ok, _run);
                case PlaylistSessionAction.PauseItem:
                    if (_clock == Clock.Running)
                        _clock = Clock.Paused;
                    return new(PlaylistOutcome.Ok, _run);
                case PlaylistSessionAction.SeekItem or PlaylistSessionAction.RewindItem:
                    _run++;
                    return new(PlaylistOutcome.Ok, _run);
                case PlaylistSessionAction.PromotePrepared:
                    return new(PlaylistOutcome.Failed, Error: Boom);
                default:
                    return new(PlaylistOutcome.Ok, _run);
            }
        }

        private static PlaylistCommandResult? Result(List<PlaylistSessionAction> actions) =>
            actions.OfType<PlaylistSessionAction.CompleteCommand>().LastOrDefault(c => c.Command == Id)?.Result;
    }
}
