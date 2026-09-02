namespace FrameFlow.TestBench;

/// <summary>
/// Renders a parsed command back into the line that produced it.
/// </summary>
/// <remarks>
/// The transcript is the artifact worth pasting into an issue, so it has to be a thing
/// the bench would accept back. <c>wait 00:00:02</c> is not — durations round-trip
/// through the same grammar the parser reads.
/// </remarks>
internal static class CommandFormatter
{
    internal static string Describe(BenchCommand command) =>
        command switch
        {
            BenchCommand.Load load => $"load {load.Path}",
            BenchCommand.Unload => "unload",
            BenchCommand.Play => "play",
            BenchCommand.Pause => "pause",
            BenchCommand.Seek seek => $"seek {Duration(seek.Position)}",
            BenchCommand.Volume volume => $"volume {volume.Level:0.##}",
            BenchCommand.Mute mute => $"mute {(mute.On ? "on" : "off")}",
            BenchCommand.Repeat repeat => $"repeat {repeat.Mode.ToString().ToLowerInvariant()}",
            BenchCommand.Status => "status",
            BenchCommand.Diag diag => diag.All ? "diag --all" : "diag",
            BenchCommand.Wait wait => $"wait {Duration(wait.Duration)}",
            BenchCommand.Quit => "quit",
            _ => command.GetType().Name,
        };

    /// <summary>Renders a duration in the form the parser accepts.</summary>
    internal static string Duration(TimeSpan value) =>
        value.TotalMilliseconds < 1_000 ? $"{value.TotalMilliseconds:0.##}ms"
        : value.TotalSeconds < 60 ? $"{value.TotalSeconds:0.###}s"
        : value.TotalMinutes < 60 ? $"{value.TotalMinutes:0.###}m"
        : $"{value.TotalHours:0.###}h";
}
