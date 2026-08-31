namespace FrameFlow.Audio.Tests;

/// <summary>
/// Serialises every test class that activates a real OpenAL device. These
/// classes share the process-wide device/context (ADR-0058) and read its
/// process-global structural counters (lease refcount, device-open count), so
/// running them in parallel would corrupt both the shared device and those
/// counters. xUnit runs all classes tagged with this collection one at a time.
/// </summary>
[CollectionDefinition("OpenAL device")]
public sealed class OpenAlDeviceCollection { }
