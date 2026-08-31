using FrameFlow.Media;

namespace FrameFlow.Examples.DualPlayer;

/// <summary>
/// Which clock masters a player's A/V sync.
/// </summary>
/// <remarks>
/// The mechanism mirrors what a digital-signage host does: the clock is
/// selected purely by whether an audio sink is attached, not by any explicit
/// clock argument.
/// </remarks>
public enum ClockSourceKind
{
    /// <summary>
    /// Pace video off FrameFlow's <c>WallClockSource</c> fallback (ADR-0003) by
    /// attaching <b>no audio sink</b>. The clip's audio stream is left
    /// unconsumed — safe because FrameFlow discards a stream with no consumer
    /// at the demuxer (ADR-0059), so the demux pump never backpressures and
    /// freezes video. This is a signage host's "no audio" mode.
    /// </summary>
    Wall,

    /// <summary>
    /// Let the audio sink master the clock: attach an audible
    /// <c>OpenAlAudioSink</c>, which drains and clocks the audio stream off its
    /// sample counter (the default substrate behaviour when a decodable audio
    /// stream is present). This is a signage host's "audible" mode.
    /// </summary>
    Audio,
}

/// <summary>
/// Per-player configuration for one of the two side-by-side panes.
/// </summary>
public sealed record PlayerConfig(
    string Label,
    string CorpusPath,
    HardwareDecodeMode HardwareDecodeMode,
    ClockSourceKind ClockSource,
    bool Loop
);

/// <summary>
/// Parsed options for the whole example: two <see cref="PlayerConfig"/>
/// (left + right) plus process-wide knobs (log file, auto-exit).
/// </summary>
/// <remarks>
/// <para>
/// CLI grammar (all optional; the defaults already produce the canonical
/// "HW both, loop on, wall vs audio clock" scenario so a bare
/// <c>dotnet run</c> is interesting):
/// </para>
/// <list type="bullet">
///   <item><c>--left &lt;path&gt;</c> / <c>--right &lt;path&gt;</c> — corpus file per pane.</item>
///   <item><c>--hw &lt;auto|disabled|required&gt;</c> — both panes; <c>--left-hw</c> / <c>--right-hw</c> override one.</item>
///   <item><c>--left-clock &lt;wall|audio&gt;</c> / <c>--right-clock &lt;wall|audio&gt;</c>.</item>
///   <item><c>--loop</c> / <c>--no-loop</c> — both panes; <c>--left-loop</c> / <c>--right-loop</c> set one on.</item>
///   <item><c>--log-file &lt;name|path&gt;</c> — a bare filename lands under <c>&lt;repo&gt;/logs/</c>; an absolute path is honoured as-is.</item>
///   <item><c>--exit-after &lt;seconds&gt;</c> — auto-close for unattended runs.</item>
/// </list>
/// </remarks>
public sealed record DualPlayerOptions(
    PlayerConfig Left,
    PlayerConfig Right,
    string? LogFilePath,
    int ExitAfterSeconds
)
{
    /// <summary>Default corpus file for both panes: 1080p H.264 + AAC audio
    /// — high-resolution enough to make hardware decode meaningful and
    /// carrying an audio track so the audio-master clock has something to
    /// lock to.</summary>
    public const string DefaultCorpusFileName = "test-1080p-h264-aac.mp4";

    public static DualPlayerOptions Parse(string[] args)
    {
        string? GetValue(string flag)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.Ordinal))
                    return args[i + 1];
            }
            return null;
        }

        bool HasFlag(string flag) => args.Contains(flag, StringComparer.Ordinal);

        var defaultCorpus = ResolveDefaultCorpus();

        var leftPath = GetValue("--left") ?? defaultCorpus;
        var rightPath = GetValue("--right") ?? defaultCorpus;

        var sharedHw = ParseHwMode(GetValue("--hw"), HardwareDecodeMode.Auto);
        var leftHw = ParseHwMode(GetValue("--left-hw"), sharedHw);
        var rightHw = ParseHwMode(GetValue("--right-hw"), sharedHw);

        // Default clocks encode the requested scenario: left = wall, right = audio.
        var leftClock = ParseClock(GetValue("--left-clock"), ClockSourceKind.Wall);
        var rightClock = ParseClock(GetValue("--right-clock"), ClockSourceKind.Audio);

        // Loop defaults ON (short corpus files; continuous demo). --no-loop
        // turns both off; --left-loop / --right-loop force one on regardless.
        var loopBoth = !HasFlag("--no-loop");
        var leftLoop = loopBoth || HasFlag("--left-loop");
        var rightLoop = loopBoth || HasFlag("--right-loop");

        var logFile = GetValue("--log-file");

        var exitAfter = 0;
        if (int.TryParse(GetValue("--exit-after"), out var secs) && secs > 0)
            exitAfter = secs;

        return new DualPlayerOptions(
            Left: new PlayerConfig("LEFT", leftPath, leftHw, leftClock, leftLoop),
            Right: new PlayerConfig("RIGHT", rightPath, rightHw, rightClock, rightLoop),
            LogFilePath: logFile,
            ExitAfterSeconds: exitAfter
        );
    }

    private static HardwareDecodeMode ParseHwMode(string? raw, HardwareDecodeMode fallback) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "disabled" or "software" or "off" => HardwareDecodeMode.Disabled,
            "required" => HardwareDecodeMode.Required,
            "auto" => HardwareDecodeMode.Auto,
            _ => fallback,
        };

    private static ClockSourceKind ParseClock(string? raw, ClockSourceKind fallback) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "wall" or "wallclock" or "system" => ClockSourceKind.Wall,
            "audio" or "audiomaster" or "master" => ClockSourceKind.Audio,
            _ => fallback,
        };

    /// <summary>
    /// Resolves the default corpus file by walking up from the app base
    /// directory to the repo root (the directory containing
    /// <c>FrameFlow.slnx</c>), then into <c>tests/corpus/files</c>. Mirrors
    /// the test projects' <c>TestEnvironment.FindRepoRoot</c>. Returns the
    /// expected path even if the file is absent so the missing-file error is
    /// surfaced by the player rather than swallowed here.
    /// </summary>
    private static string ResolveDefaultCorpus()
    {
        var root = RepoRoot.Find();
        return Path.Combine(root, "tests", "corpus", "files", DefaultCorpusFileName);
    }
}
