using System.Runtime.InteropServices;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Tests for the SafeHandle wrapper types. Verifies the IsInvalid
/// contract without requiring actual FFmpeg binaries — handles are
/// constructed with known-invalid (zero) or fake non-zero pointers.
/// The release path requires live native resources and is exercised
/// only in integration tests.
/// </summary>
public sealed class SafeHandleTests : IClassFixture<FfmpegBootstrapFixture>
{
    /// <summary>
    /// Factory closures for every <see cref="SafeHandle"/>-derived
    /// type we ship. Each factory takes a pointer value and returns
    /// the corresponding handle. The IsInvalid contract is identical
    /// across all of them — one theory exercises every type.
    /// </summary>
    public static readonly TheoryData<string, Func<nint, SafeHandle>> HandleFactories =
        new()
        {
            { nameof(CodecContextHandle), ptr => new CodecContextHandle(ptr) },
            { nameof(FrameHandle), ptr => new FrameHandle(ptr) },
            { nameof(PacketHandle), ptr => new PacketHandle(ptr) },
            { nameof(SwsContextHandle), ptr => new SwsContextHandle(ptr) },
            { nameof(FormatContextHandle), ptr => new FormatContextHandle(ptr) },
        };

    [Theory]
    [MemberData(nameof(HandleFactories))]
    public void NonZeroPtr_IsNotInvalid(string _, Func<nint, SafeHandle> factory)
    {
        var handle = factory((nint)1);
        try
        {
            Assert.False(handle.IsInvalid);
        }
        finally
        {
            // Detach from native ownership — the pointer is fake and
            // ReleaseHandle would call the native free function on
            // garbage memory.
            handle.SetHandleAsInvalid();
        }
    }

    [Theory]
    [MemberData(nameof(HandleFactories))]
    public void ZeroPtr_IsInvalid(string _, Func<nint, SafeHandle> factory)
    {
        var handle = factory(nint.Zero);
        Assert.True(handle.IsInvalid);
        // Disposing an invalid handle is safe per SafeHandle contract.
        handle.Dispose();
    }
}
