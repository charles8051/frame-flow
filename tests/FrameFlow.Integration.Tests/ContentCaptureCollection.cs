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
/// No assertion depends on the serialisation. A frame the pacer drops because
/// the runner fell behind is counted, and the video comparison accounts for it
/// rather than failing (#299). What contention still costs is coverage: a
/// dropped frame is one fewer presented frame compared against the reference.
/// Serialising the heavy classes keeps that cost down and bounds the memory
/// they hold at once.
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
