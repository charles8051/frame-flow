// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Playback.Core;
using Action = FrameFlow.Playback.Core.ItemRuntimeAction;
using Input = FrameFlow.Playback.Core.ItemRuntimeInput;

namespace FrameFlow.Playback.Tests;

/// <summary>
/// Drives <see cref="ItemRuntimeProtocol"/> one input at a time and keeps what it asked for, so a
/// test reads as the shell would see it: the input, then the immediate actions, then the awaited
/// one.
/// </summary>
internal sealed class ItemRuntimeTranscript(ItemRuntimeState initial)
{
    /// <summary>Audio and video, a wallclock master and a pacer: every step of every path.</summary>
    public const ItemResources AudioAndVideo =
        ItemResources.Clock
        | ItemResources.Demux
        | ItemResources.VideoDecoder
        | ItemResources.AudioDecoder
        | ItemResources.Pipeline
        | ItemResources.Pacer;

    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    private readonly List<Action> _all = [];
    private int _nextCommand = 1;

    public ItemRuntimeTranscript()
        : this(ItemRuntimeState.Initial()) { }

    public ItemRuntimeState State { get; private set; } = initial;

    /// <summary>Every action asked for so far, in order, the awaited ones where they came.</summary>
    public IReadOnlyList<Action> All => _all;

    /// <summary>The last step.</summary>
    public ItemRuntimeStep Last { get; private set; } = new([], null);

    /// <summary>The last step's immediate actions.</summary>
    public Action[] Actions => [.. Last.Actions];

    /// <summary>The number the next command gets.</summary>
    public int NextCommand => _nextCommand;

    public ItemRuntimeStep Send(Input input)
    {
        (State, Last) = ItemRuntimeProtocol.Step(State, input);
        _all.AddRange(Last.Actions);
        if (Last.Awaited is { } awaited)
            _all.Add(awaited);
        return Last;
    }

    /// <summary>The actions from here on.</summary>
    public int Mark() => _all.Count;

    public IReadOnlyList<Action> Since(int mark) => _all.Skip(mark).ToArray();

    // ── Commands ────────────────────────────────────────────────────────────

    public int Initialize(TimeSpan start = default, TimeSpan? end = null) =>
        Command(id => new Input.Initialize(id, start, end));

    public int WarmUp(TimeSpan clock = default) => Command(id => new Input.WarmUp(id, clock));

    public int Play(TimeSpan clock = default, bool clockPaused = false) =>
        Command(id => new Input.Play(id, clock, clockPaused));

    public int Pause() => Command(id => new Input.Pause(id));

    public int Seek(TimeSpan target, bool clockPaused = false) =>
        Command(id => new Input.Seek(id, target, clockPaused));

    public int Rewind(TimeSpan position = default, TimeSpan? end = null, bool clockPaused = false) =>
        Command(id => new Input.Rewind(id, position, end, clockPaused));

    public ItemRuntimeStep CallerCancels(int command) => Send(new Input.CallerCancelled(command));

    public ItemRuntimeStep Dispose() => Send(new Input.Dispose());

    public ItemRuntimeStep Attach() => Send(new Input.Attach());

    private int Command(Func<int, Input> make)
    {
        var id = _nextCommand++;
        Send(make(id));
        return id;
    }

    // ── Fed back ────────────────────────────────────────────────────────────

    public ItemRuntimeStep Opened(ItemResources resources = AudioAndVideo) =>
        Send(new Input.Opened(resources, Duration));

    public ItemRuntimeStep Ok() => Send(new Input.Performed(ItemActionOutcome.Ok));

    public ItemRuntimeStep Fails(Exception error) => Send(new Input.Performed(ItemActionOutcome.Failed, error));

    public ItemRuntimeStep Cancelled() => Send(new Input.Performed(ItemActionOutcome.Cancelled));

    /// <summary>Feeds Ok to each awaited action until a step awaits nothing.</summary>
    public void OkUntilIdle()
    {
        while (Last.Awaited is not null)
            Ok();
    }

    public ItemRuntimeStep Ends(ItemWork work, ItemWorkEnding ending = ItemWorkEnding.Cancelled, Exception? error = null) =>
        Send(new Input.WorkEnded(State.Run, work, ending, error));

    public ItemRuntimeStep FirstFrame() => Send(new Input.FirstFrameDecoded());

    // ── Scenes ──────────────────────────────────────────────────────────────

    /// <summary>Opened, warmed up and played: the run is going and the gates are open.</summary>
    public static ItemRuntimeTranscript Playing(ItemResources resources = AudioAndVideo, bool latenessRecovery = false)
    {
        var t = new ItemRuntimeTranscript(ItemRuntimeState.Initial(latenessRecovery: latenessRecovery));
        t.Initialize();
        t.Opened(resources);
        t.WarmUp();
        t.OkUntilIdle();
        t.FirstFrame();
        t.Play();
        t.OkUntilIdle();
        Assert.Equal(ItemRunStatus.Running, t.State.Status);
        Assert.True(t.State.Played);
        Assert.Null(t.State.Command);
        return t;
    }

    /// <summary>The results handed back for <paramref name="command"/>, in order.</summary>
    public IReadOnlyList<ItemCommandResult> ResultsOf(int command) =>
        _all.OfType<Action.CompleteCommand>().Where(c => c.Command == command).Select(c => c.Result).ToArray();

    public int Count<T>()
        where T : Action => _all.OfType<T>().Count();
}
