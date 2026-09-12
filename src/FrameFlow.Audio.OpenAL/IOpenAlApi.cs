// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Silk.NET.OpenAL;

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// The subset of the OpenAL entry points <see cref="OpenAlAudioSink"/> calls,
/// behind an interface so a fake device can stand in for the driver.
/// </summary>
/// <remarks>
/// <para>
/// The sink's buffer-queue logic was previously testable only from two ends.
/// <see cref="BufferQueueState"/> is pure and exhaustively covered, but it only
/// sees the scalars the shell hands it, so it cannot observe that the shell read
/// <c>AL_BUFFERS_PROCESSED</c> from a source it had not yet established was
/// stopped — the shape of #133. The device-gated tests drive a real OpenAL Soft
/// and prove end-to-end behaviour, but they are opt-in, depend on a real device's
/// timing, and can assert counters only, never a call sequence (#138).
/// </para>
/// <para>
/// The member set is deliberately exactly what the sink calls and nothing more.
/// Every method mirrors its Silk.NET signature so <see cref="SilkOpenAlApi"/> is
/// a straight forward with no translation, and so a call the sink adds later
/// fails to compile here rather than silently escaping the fake.
/// </para>
/// <para>
/// <b>Errors are not modelled.</b> The sink calls no <c>alGetError</c>, so a
/// failed <c>al*</c> call is not observable anywhere in this assembly today. The
/// interface keeps that posture rather than inventing a reporting channel the
/// production path would not use. If error checking is ever added to the sink,
/// it belongs here at the same time.
/// </para>
/// </remarks>
internal interface IOpenAlApi
{
    /// <summary>Creates one source name (<c>alGenSources</c> with n = 1).</summary>
    uint GenSource();

    /// <summary>Creates one buffer name (<c>alGenBuffers</c> with n = 1).</summary>
    uint GenBuffer();

    /// <summary>Deletes one source name (<c>alDeleteSources</c> with n = 1).</summary>
    void DeleteSource(uint source);

    /// <summary>Deletes one buffer name (<c>alDeleteBuffers</c> with n = 1).</summary>
    void DeleteBuffer(uint buffer);

    /// <summary>Starts or restarts playback (<c>alSourcePlay</c>).</summary>
    void SourcePlay(uint source);

    /// <summary>Suspends playback, preserving the sample offset (<c>alSourcePause</c>).</summary>
    void SourcePause(uint source);

    /// <summary>Stops playback (<c>alSourceStop</c>).</summary>
    void SourceStop(uint source);

    /// <summary>Returns the source to <c>AL_INITIAL</c> with the offset at zero (<c>alSourceRewind</c>).</summary>
    void SourceRewind(uint source);

    /// <summary>Fills a buffer with PCM (<c>alBufferData</c>).</summary>
    /// <param name="buffer">Buffer name to fill.</param>
    /// <param name="format">Channel count and sample width.</param>
    /// <param name="data">Interleaved sample data.</param>
    /// <param name="size">Length of <paramref name="data"/> in bytes.</param>
    /// <param name="frequency">Sample rate in Hz.</param>
    unsafe void BufferData(uint buffer, BufferFormat format, void* data, int size, int frequency);

    /// <summary>Appends buffers to a source's queue (<c>alSourceQueueBuffers</c>).</summary>
    unsafe void SourceQueueBuffers(uint source, int count, uint* buffers);

    /// <summary>Removes processed buffers from the head of a source's queue (<c>alSourceUnqueueBuffers</c>).</summary>
    unsafe void SourceUnqueueBuffers(uint source, int count, uint* buffers);

    /// <summary>Reads an integer source property (<c>alGetSourcei</c>).</summary>
    void GetSourceProperty(uint source, GetSourceInteger param, out int value);

    /// <summary>Reads an integer buffer property (<c>alGetBufferi</c>).</summary>
    void GetBufferProperty(uint buffer, GetBufferInteger param, out int value);

    /// <summary>Writes a float source property (<c>alSourcef</c>).</summary>
    void SetSourceProperty(uint source, SourceFloat param, float value);
}

/// <summary>
/// A sink's reference on an OpenAL device and context, behind an interface so a
/// fake can supply one.
/// </summary>
/// <remarks>
/// <see cref="IsConnected"/> is on the lease rather than on <see cref="IOpenAlApi"/>
/// because it is an ALC property of the device, not an AL property of a source.
/// A seam over the AL calls alone cannot make a device go away mid-session, which
/// is the ordering question #130's failure class turns on: what the sink does when
/// the endpoint disappears between the source-state read and the queue.
/// </remarks>
internal interface IOpenAlContextLease : IDisposable
{
    /// <summary>The AL entry points bound to this lease's context.</summary>
    IOpenAlApi Al { get; }

    /// <summary>The endpoint this context was opened on, as the driver names it.</summary>
    string DeviceName { get; }

    /// <summary>Whether the device still considers itself connected (<c>ALC_CONNECTED</c>).</summary>
    bool IsConnected { get; }
}

/// <summary>
/// Forwards <see cref="IOpenAlApi"/> to a Silk.NET <see cref="AL"/> handle.
/// </summary>
/// <remarks>
/// One instance per <see cref="SharedOpenAlContext"/>, created with the context
/// and living as long as it. The wrapper holds no state of its own, so it is as
/// safe to call concurrently on distinct sources as the handle it wraps.
/// </remarks>
internal sealed class SilkOpenAlApi : IOpenAlApi
{
    private readonly AL _al;

    internal SilkOpenAlApi(AL al) => _al = al;

    public uint GenSource() => _al.GenSource();

    public uint GenBuffer() => _al.GenBuffer();

    public void DeleteSource(uint source) => _al.DeleteSource(source);

    public void DeleteBuffer(uint buffer) => _al.DeleteBuffer(buffer);

    public void SourcePlay(uint source) => _al.SourcePlay(source);

    public void SourcePause(uint source) => _al.SourcePause(source);

    public void SourceStop(uint source) => _al.SourceStop(source);

    public void SourceRewind(uint source) => _al.SourceRewind(source);

    public unsafe void BufferData(
        uint buffer,
        BufferFormat format,
        void* data,
        int size,
        int frequency
    ) => _al.BufferData(buffer, format, data, size, frequency);

    public unsafe void SourceQueueBuffers(uint source, int count, uint* buffers) =>
        _al.SourceQueueBuffers(source, count, buffers);

    public unsafe void SourceUnqueueBuffers(uint source, int count, uint* buffers) =>
        _al.SourceUnqueueBuffers(source, count, buffers);

    public void GetSourceProperty(uint source, GetSourceInteger param, out int value) =>
        _al.GetSourceProperty(source, param, out value);

    public void GetBufferProperty(uint buffer, GetBufferInteger param, out int value) =>
        _al.GetBufferProperty(buffer, param, out value);

    public void SetSourceProperty(uint source, SourceFloat param, float value) =>
        _al.SetSourceProperty(source, param, value);
}
