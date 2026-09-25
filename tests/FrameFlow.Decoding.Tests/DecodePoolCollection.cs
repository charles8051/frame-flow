namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Runs alone the test classes that assert on <c>DecodePoolMetrics</c>' process-wide sums.
/// </summary>
/// <remarks>
/// The capacity and outstanding counts cover every hardware decoder in the process, so a
/// hardware decoder opened by a test running alongside would move them.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DecodePoolCollection
{
    /// <summary>The collection name, referenced by <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Decode pool metrics";
}
