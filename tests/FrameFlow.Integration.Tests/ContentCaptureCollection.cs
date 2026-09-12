namespace FrameFlow.Integration.Tests;

/// <summary>
/// Serialises the test classes that retain whole decoded streams in memory and
/// compare them against a reference decode.
/// </summary>
/// <remarks>
/// <para>
/// xUnit parallelises across collections and runs the tests inside one
/// collection in order. By default each class is its own collection, so these
/// classes would run concurrently with each other and with every other
/// integration class, each holding a complete playback capture and a complete
/// reference capture at the same time. For the 1080p corpus entry that is two
/// copies of 90 frames of Y plane, and the decode work behind it is software
/// FFmpeg competing for the same cores.
/// </para>
/// <para>
/// That matters because the content assertions require zero frame loss. Frame
/// loss is a real signal when the pipeline is at fault and noise when the
/// machine simply could not decode in real time, and the assertion cannot tell
/// the two apart. Serialising the heavy classes keeps the premise the zero-loss
/// budget rests on — that decode can keep up — closer to true on a shared CI
/// runner.
/// </para>
/// <para>
/// This does not make the suite hermetic. Other integration classes still run
/// alongside. It removes the largest and most avoidable source of contention,
/// which is these classes piling onto each other.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ContentCaptureCollection
{
    /// <summary>The collection name, referenced by <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Content capture (serialised)";
}
