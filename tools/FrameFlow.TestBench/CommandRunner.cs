using FrameFlow.Media;
using FrameFlow.Playback;
using FrameFlow.Playback.Diagnostics;

namespace FrameFlow.TestBench;

/// <summary>
/// Runs parsed commands against a live <see cref="IPlaybackController"/> and reports what
/// happened, one line per command.
/// </summary>
/// <remarks>
/// <para>
/// The bench builds on <see cref="IPlaybackController"/> rather than
/// <c>IMediaPlayer</c>, per Decision 3. <c>IMediaPlayer</c> has no load — its source is
/// fixed at construction — and <see cref="IPlaybackController"/> has no volume, so the
/// two are composed: this type holds the controller, and the audio sink's
/// <see cref="IVolumeControl"/> when there is one.
/// </para>
/// <para>
/// Every command returns rather than throws. A bench that fell over on a refused seek
/// would lose the session it took thirty seconds to reach, which is the loop this whole
/// tool exists to close.
/// </para>
/// </remarks>
internal sealed class CommandRunner(
    IPlaybackController controller,
    IVolumeControl? volume,
    HeadlessVideoSink? headlessSink,
    TextWriter output
)
{
    private readonly IPlaybackController _controller = controller;
    private readonly IVolumeControl? _volume = volume;
    private readonly HeadlessVideoSink? _headlessSink = headlessSink;
    private readonly TextWriter _out = output;

    /// <summary>The last snapshot <c>diag</c> printed, for the interval it reports next.</summary>
    private PlaybackDiagnosticsSnapshot? _lastDiag;

    /// <summary>Set by <c>quit</c>, and by end of script.</summary>
    internal bool ShouldExit { get; private set; }

    /// <summary>Runs one command.</summary>
    /// <returns><see langword="true"/> when it succeeded.</returns>
    internal async Task<bool> RunAsync(BenchCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case BenchCommand.Load load:
                return Report(await _controller.LoadAsync(MediaSource.FromFile(load.Path), ct));

            case BenchCommand.Unload:
                return Report(await _controller.UnloadAsync(ct));

            case BenchCommand.Play:
                return Report(await _controller.PlayAsync(ct));

            case BenchCommand.Pause:
                return Report(await _controller.PauseAsync(ct));

            case BenchCommand.Seek seek:
                return Report(await _controller.SeekAsync(seek.Position, ct));

            case BenchCommand.Repeat repeat:
                return Report(await _controller.SetRepeatModeAsync(repeat.Mode, ct));

            case BenchCommand.Volume set:
                return SetVolume(set.Level);

            case BenchCommand.Mute mute:
                return SetMuted(mute.On);

            case BenchCommand.Status:
                _out.WriteLine(DiagnosticsRenderer.Status(_controller));
                return true;

            case BenchCommand.Diag diag:
                PrintDiagnostics(diag.All);
                return true;

            case BenchCommand.Wait wait:
                await Task.Delay(wait.Duration, ct);
                return true;

            case BenchCommand.Quit:
                ShouldExit = true;
                return true;

            default:
                _out.WriteLine($"FAIL  unhandled command {command.GetType().Name}");
                return false;
        }
    }

    private bool SetVolume(float level)
    {
        if (_volume is null)
            return Fail("no audio sink — the bench was started with --no-audio");

        try
        {
            _volume.Volume = level;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // IVolumeControl rejects invalid gain rather than clamping, so a caller
            // learns about the mistake. Passing that through keeps the bench honest.
            return Fail(ex.Message);
        }

        _out.WriteLine($"ok    volume {level:0.##}");
        return true;
    }

    private bool SetMuted(bool muted)
    {
        if (_volume is null)
            return Fail("no audio sink — the bench was started with --no-audio");

        _volume.Muted = muted;
        _out.WriteLine($"ok    mute {(muted ? "on" : "off")}");
        return true;
    }

    private void PrintDiagnostics(bool all)
    {
        var snapshot = _controller.GetDiagnostics();

        if (all)
        {
            _out.WriteLine(DiagnosticsRenderer.Full(snapshot, _headlessSink));
            _lastDiag = snapshot;
            return;
        }

        _out.WriteLine(DiagnosticsRenderer.Summary(snapshot, _headlessSink));

        // The interval since the previous `diag`, interpreted rather than dumped.
        // This is the counter-delta knowledge Decision 5 moved out of popcorn and into
        // the library; the bench is its first consumer in this tree.
        if (_lastDiag is { } previous)
            _out.WriteLine(DiagnosticsRenderer.Interval(previous, snapshot));

        _lastDiag = snapshot;
    }

    private bool Report(Result result)
    {
        if (result.IsSuccess)
        {
            _out.WriteLine($"ok    {DiagnosticsRenderer.Status(_controller)}");
            return true;
        }

        return Fail(result.Error is { } error ? $"{error.Category}: {error.Message}" : "failed");
    }

    private bool Fail(string message)
    {
        _out.WriteLine($"FAIL  {message}");
        return false;
    }
}
