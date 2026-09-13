namespace FrameFlow.Integration.Tests;

/// <summary>
/// Serialises the test classes that play through a real OpenAL device.
/// </summary>
/// <remarks>
/// Every sink in a process shares one device and context (ADR-0058), and these tests
/// measure how fast that device consumes audio. Two classes playing at once would share the
/// device and compete for the CPU that mixes it, and each would measure the other.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class OpenAlDeviceCollection
{
    /// <summary>The collection name, referenced by <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "OpenAL device";
}
