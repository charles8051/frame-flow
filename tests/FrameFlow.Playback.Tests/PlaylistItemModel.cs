namespace FrameFlow.Playback.Tests;

/// <summary>
/// A model of what a <see cref="SubstrateSession"/> does with its runs, shared by the rig's fake
/// item runtimes and the ordering explorer.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>
///   A run launches at the first play, and at a seek or rewind that is not paused. Before its first
///   play a runtime reads as not paused, so a seek then launches a run: the gap the end-of-queue record
///   leaves open.
///   </description></item>
///   <item><description>A paused seek or rewind advances the run without launching.</description></item>
///   <item><description>
///   An end-of-stream comes only from a launched run that has not been stopped, once per run.
///   </description></item>
///   <item><description>
///   A seek or rewind can be cancelled before the run advances, which leaves the run as it was, or
///   after, which leaves the new run stopped.
///   </description></item>
/// </list>
/// </remarks>
/// <param name="Run">The run number, as <c>RunNumber</c> reports it.</param>
/// <param name="Launched">Whether the current run is running.</param>
/// <param name="Paused">Whether the runtime is paused.</param>
/// <param name="EndRaised">Whether the current run has raised its end-of-stream.</param>
/// <param name="Disposed">Whether the runtime has been disposed.</param>
internal sealed record PlaylistItemModel(
    int Run,
    bool Launched,
    bool Paused,
    bool EndRaised,
    bool Disposed
)
{
    /// <summary>A runtime that has opened and not played.</summary>
    public static PlaylistItemModel Opened { get; } = new(0, false, false, false, false);

    /// <summary>Whether the runtime can raise an end-of-stream now.</summary>
    public bool CanRaiseEndOfStream => Launched && !EndRaised && !Disposed;

    /// <summary>A play that succeeded: the run launches, or resumes.</summary>
    public PlaylistItemModel Played() => this with { Launched = true, Paused = false };

    /// <summary>A pause that succeeded.</summary>
    public PlaylistItemModel PausedNow() => this with { Paused = true };

    /// <summary>A seek or rewind that succeeded: a new run, launched unless paused.</summary>
    public PlaylistItemModel Repositioned() => RunAdvanced().Relaunched();

    /// <summary>
    /// A seek or rewind has stopped the run it interrupts and advanced the run number, and has not
    /// relaunched. A seek or rewind cancelled at this point leaves the new run stopped.
    /// </summary>
    public PlaylistItemModel RunAdvanced() =>
        this with
        {
            Run = Run + 1,
            Launched = false,
            EndRaised = false,
        };

    /// <summary>A seek or rewind that had advanced the run finishes: it launches unless paused.</summary>
    public PlaylistItemModel Relaunched() => this with { Launched = !Paused };

    /// <summary>A seek or rewind cancelled after the run advanced: the new run is stopped.</summary>
    public PlaylistItemModel CancelledAfterTheRunAdvanced() => RunAdvanced();

    /// <summary>The current run raised its end-of-stream.</summary>
    public PlaylistItemModel RaisedEndOfStream() => this with { EndRaised = true };

    /// <summary>The runtime was disposed.</summary>
    public PlaylistItemModel DisposedNow() => this with { Launched = false, Disposed = true };
}
