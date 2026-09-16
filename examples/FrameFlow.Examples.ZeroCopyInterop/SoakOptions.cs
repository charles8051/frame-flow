namespace FrameFlow.Examples.ZeroCopyInterop;

/// <summary>
/// The soak run: two players in one window, each on its own zero-copy presenter, looping a clip
/// with hardware decode. It samples both players' diagnostics on an interval and writes a row per
/// player per sample, which is what a before/after comparison of a playback change reads.
/// </summary>
/// <param name="SecondFilePath">
/// The right pane's clip. The left pane's is the file passed as the positional argument.
/// </param>
/// <param name="SampleSeconds">Seconds between samples.</param>
/// <param name="CsvPath">Where the samples are written.</param>
/// <param name="Label">A name for the run, written into every row so two runs can be compared.</param>
public sealed record SoakOptions(
    string? SecondFilePath,
    int SampleSeconds,
    string CsvPath,
    string Label
);
