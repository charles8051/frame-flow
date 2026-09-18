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
            BenchCommand.Load load => Load(load),
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

    /// <summary>
    /// Renders <c>load</c> with its flags ahead of the path, which is the order the parser
    /// reads: a path is the trailing run of the line and can contain spaces, so nothing may
    /// follow it. Options come out sorted, because a dictionary's order is not the order they
    /// were written and a transcript that reorders between runs is a diff nobody wants.
    /// </summary>
    private static string Load(BenchCommand.Load load)
    {
        var parts = new List<string>();

        if (load.Format is { Length: > 0 } format)
            parts.Add($"--format {format}");

        if (load.Options is { Count: > 0 } options)
        {
            foreach (var key in options.Keys.OrderBy(k => k, StringComparer.Ordinal))
                parts.Add($"--option {key}={options[key]}");
        }

        parts.Add(load.Path);
        return $"load {string.Join(' ', parts)}";
    }

    /// <summary>Renders a duration in the form the parser accepts.</summary>
    internal static string Duration(TimeSpan value) =>
        value.TotalMilliseconds < 1_000 ? $"{value.TotalMilliseconds:0.##}ms"
        : value.TotalSeconds < 60 ? $"{value.TotalSeconds:0.###}s"
        : value.TotalMinutes < 60 ? $"{value.TotalMinutes:0.###}m"
        : $"{value.TotalHours:0.###}h";
}
