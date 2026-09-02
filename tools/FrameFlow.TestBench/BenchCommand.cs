namespace FrameFlow.TestBench;

/// <summary>
/// One parsed bench command. A verb and its arguments, nothing more.
/// </summary>
/// <remarks>
/// The ADR's Decision 6 resolution dropped the assertion grammar: there are no
/// operators, no metric paths, no metric kinds, and no <c>mark</c> / <c>since</c> /
/// <c>expect</c> / <c>require</c> / <c>set</c>. A reproduction that asserts is a C#
/// file-based app, so this type is a command model rather than a language.
/// </remarks>
internal abstract record BenchCommand
{
    /// <summary>Build a session on <paramref name="Path"/>, replacing any current one.</summary>
    /// <remarks>
    /// <see cref="FrameFlow.Playback.IPlaybackController.LoadAsync"/>, which resets the
    /// session while the sink counters keep climbing — the first row of Decision 3's
    /// <c>load</c> semantics table, and the one Decision 5's delta interpretation assumes.
    /// </remarks>
    internal sealed record Load(string Path) : BenchCommand;

    internal sealed record Unload : BenchCommand;

    internal sealed record Play : BenchCommand;

    internal sealed record Pause : BenchCommand;

    internal sealed record Seek(TimeSpan Position) : BenchCommand;

    internal sealed record Volume(float Level) : BenchCommand;

    internal sealed record Mute(bool On) : BenchCommand;

    internal sealed record Repeat(FrameFlow.Media.RepeatMode Mode) : BenchCommand;

    internal sealed record Status : BenchCommand;

    /// <param name="All">Print the whole snapshot rather than the interpreted summary.</param>
    internal sealed record Diag(bool All) : BenchCommand;

    /// <summary>
    /// A sleep. Not a condition.
    /// </summary>
    /// <remarks>
    /// The only wait the bench has. Conditional waiting left with the assertions: a
    /// repro polls in C#, where the loop is a few lines and does not need operators or
    /// a metric namespace to express. The concept is still required — <c>Position</c> is
    /// clock-driven and settles rather than stepping, so anything asserting straight
    /// after an action asserts against a pipeline that has not caught up.
    /// </remarks>
    internal sealed record Wait(TimeSpan Duration) : BenchCommand;

    internal sealed record Quit : BenchCommand;
}
