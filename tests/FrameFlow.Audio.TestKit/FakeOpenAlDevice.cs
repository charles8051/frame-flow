// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text;
using FrameFlow.Audio.OpenAL;
using Silk.NET.OpenAL;

namespace FrameFlow.Audio.TestKit;

/// <summary>
/// An in-memory OpenAL device that models the 1.1 specification's source and
/// buffer semantics, so <see cref="OpenAlAudioSink"/>'s ordering against the
/// device — and the PCM the device was actually handed — are assertable on a
/// headless runner (#138).
/// </summary>
/// <remarks>
/// <para>
/// <b>The semantic this exists for.</b> OpenAL 1.1 §4.3.2, on
/// <c>AL_BUFFERS_PROCESSED</c>: "On a source in the AL_STOPPED state, all buffers
/// are processed. On a source in the AL_INITIAL state, no buffers are processed,
/// all buffers are pending." A stopped source therefore reports buffers it never
/// played, including ones queued after it stopped. That is normative, not an
/// OpenAL Soft quirk, and it is the mechanism of #133.
/// </para>
/// <para>
/// The fake keeps that reporting separate from what it actually played.
/// <see cref="BuffersProcessed"/> follows the spec; <see cref="PlayedSamples"/>
/// grows only when <see cref="AdvancePlayback"/> moves the cursor over a buffer
/// while the source is playing. A sink that believes the processed count while
/// the source is stopped shows up as a full queue log and silent output, which
/// is exactly what a listener heard in #133 and what no counter reported.
/// </para>
/// <para>
/// <b>Virtual time.</b> Nothing plays on its own. A test calls
/// <see cref="AdvancePlayback"/> to move the play cursor, which is what makes an
/// underrun, a re-prime, and the burst-gap-burst feed pattern reachable without a
/// sound card or a real-time wait.
/// </para>
/// <para>
/// <b>Stricter than the driver, on purpose.</b> The production
/// <see cref="IOpenAlApi"/> models no errors, because the sink calls no
/// <c>alGetError</c> and a failed <c>al*</c> call is not observable anywhere in
/// that assembly. Where the spec says a call fails, this fake throws instead. In
/// production those failures are silent and corrupt the buffer pool; under test
/// they should stop the test.
/// </para>
/// <para>
/// Every member is guarded by one lock and every call is recorded with its
/// managed thread id, so a test can drive the fake from several threads at once
/// and still assert an ordering over <see cref="Calls"/>.
/// </para>
/// </remarks>
public sealed class FakeOpenAlDevice : IOpenAlApi
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, FakeBuffer> _buffers = new();
    private readonly Dictionary<uint, FakeSource> _sources = new();
    private readonly List<AlCall> _calls = new();
    private readonly List<short> _playedSamples = new();

    private uint _nextSource = 1;
    private uint _nextBuffer = 1;
    private int _playedSampleRate;
    private int _playedChannels;
    private bool _connected = true;
    private int _leasesOutstanding;
    private int _leasesDisposed;

    /// <summary>The endpoint name this fake reports, as a driver would.</summary>
    public string DeviceName { get; init; } = "FakeOpenAL on Test Endpoint";

    /// <summary>
    /// Whether the device reports itself connected (<c>ALC_CONNECTED</c>). Set to
    /// <see langword="false"/> to make the endpoint go away underneath an open
    /// context, the way a disconnecting Remote Desktop session does (#130).
    /// </summary>
    public bool Connected
    {
        get
        {
            lock (_gate)
                return _connected;
        }
        set
        {
            lock (_gate)
                _connected = value;
        }
    }

    /// <summary>Every <c>al*</c> call this device has received, in order.</summary>
    public IReadOnlyList<AlCall> Calls
    {
        get
        {
            lock (_gate)
                return _calls.ToArray();
        }
    }

    /// <summary>
    /// The interleaved PCM the device actually played, in play order.
    /// </summary>
    /// <remarks>
    /// Grows only through <see cref="AdvancePlayback"/>, one whole buffer at a
    /// time as the cursor crosses it. Buffers a stopped source reports as
    /// processed but never played do not appear here, which is the distinction
    /// the counters cannot make.
    /// </remarks>
    public IReadOnlyList<short> PlayedSamples
    {
        get
        {
            lock (_gate)
                return _playedSamples.ToArray();
        }
    }

    /// <summary>
    /// Sample rate of the audio in <see cref="PlayedSamples"/>, or 0 before
    /// anything has played. Taken from the buffers as they are played, because a
    /// queue is single-format (OpenAL 1.1 §4.3.5).
    /// </summary>
    public int PlayedSampleRate
    {
        get
        {
            lock (_gate)
                return _playedSampleRate;
        }
    }

    /// <summary>
    /// Channel count of the audio in <see cref="PlayedSamples"/>, or 0 before
    /// anything has played.
    /// </summary>
    public int PlayedChannels
    {
        get
        {
            lock (_gate)
                return _playedChannels;
        }
    }

    /// <summary>
    /// Whether no live source still holds a buffer the device has not played.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drain condition. It is about what the device has played, not about
    /// what the sink has taken back: a buffer leaves a queue when the sink
    /// unqueues it, which happens on a flush or at deactivation, so after the
    /// last buffer is pushed the queue can stay populated indefinitely while the
    /// device is in fact finished. An empty-queue condition is therefore never
    /// reached on a clean play-to-EOF.
    /// </para>
    /// <para>
    /// A sample count that has stopped moving is no good either: it cannot
    /// distinguish a finished device from a descheduled pump thread. This can,
    /// because the played flags are set under the same lock that advances the
    /// cursor, so there is no moment when a buffer is played but not yet counted.
    /// </para>
    /// <para>
    /// It stays false when a source holds buffers it will never play — the
    /// stopped-source case behind #133 — which is a failure the caller should see
    /// as a failed drain rather than as a completed one.
    /// </para>
    /// </remarks>
    public bool AllQueuedAudioPlayed
    {
        get
        {
            lock (_gate)
                return _sources.Values.All(s => s.Deleted || s.Queue.All(e => e.Played));
        }
    }

    /// <summary>Leases handed out that have not been disposed.</summary>
    public int LeasesOutstanding
    {
        get
        {
            lock (_gate)
                return _leasesOutstanding - _leasesDisposed;
        }
    }

    /// <summary>Buffer names generated and not yet deleted.</summary>
    public int LiveBuffers
    {
        get
        {
            lock (_gate)
                return _buffers.Values.Count(b => !b.Deleted);
        }
    }

    /// <summary>Source names generated and not yet deleted.</summary>
    public int LiveSources
    {
        get
        {
            lock (_gate)
                return _sources.Values.Count(s => !s.Deleted);
        }
    }

    /// <summary>
    /// Hands out a lease on this device. Shaped as a
    /// <see cref="Func{TResult}"/> target so it can be passed straight to
    /// <see cref="OpenAlAudioSink"/>'s internal constructor.
    /// </summary>
    /// <remarks>
    /// Internal because <see cref="IOpenAlContextLease"/> is internal to
    /// <c>FrameFlow.Audio.OpenAL</c>; a public member cannot name it. Callers
    /// outside this assembly build a sink through <see cref="FakeOpenAlSink"/>.
    /// </remarks>
    internal IOpenAlContextLease? Lease()
    {
        lock (_gate)
        {
            _leasesOutstanding++;
            return new FakeLease(this);
        }
    }

    // ── Virtual time ────────────────────────────────────────────────────────

    /// <summary>
    /// Moves every playing source's cursor forward by <paramref name="elapsed"/>
    /// of audio, emitting the PCM of each buffer the cursor fully crosses and
    /// stopping any source that reaches the end of its queue.
    /// </summary>
    /// <remarks>
    /// A source that runs out of queued buffers enters <c>AL_STOPPED</c>, per
    /// OpenAL 1.1 §4.3.5: "A playing source will enter the AL_STOPPED state if it
    /// completes playback of the last buffer in its queue." That is the underrun,
    /// and it is the entry point to #133's failure.
    /// </remarks>
    public void AdvancePlayback(TimeSpan elapsed)
    {
        lock (_gate)
        {
            foreach (var source in _sources.Values)
            {
                if (source.Deleted || source.State != AlState.Playing)
                    continue;
                if (source.QueueSampleRate <= 0)
                    continue;

                long samples = (long)(elapsed.TotalSeconds * source.QueueSampleRate);
                AdvanceSourceUnderLock(source, samples);
            }
        }
    }

    private void AdvanceSourceUnderLock(FakeSource source, long samples)
    {
        long total = source.TotalQueuedSamples;
        source.Cursor = Math.Min(source.Cursor + samples, total);

        // Emit each buffer the cursor has fully crossed, in queue order. Emission
        // is per whole buffer: a buffer half-consumed is the current buffer, not a
        // played one, and the sink queues in whole blocks anyway.
        long boundary = 0;
        foreach (var entry in source.Queue)
        {
            boundary += entry.SamplesPerChannel;
            if (source.Cursor < boundary)
                break;
            if (entry.Played)
                continue;
            entry.Played = true;
            if (_buffers.TryGetValue(entry.Buffer, out var buffer))
            {
                _playedSamples.AddRange(buffer.Samples);
                _playedSampleRate = buffer.SampleRate;
                _playedChannels = buffer.Channels;
            }
        }

        if (source.Cursor >= total)
            source.State = AlState.Stopped;
    }

    // ── IOpenAlApi ──────────────────────────────────────────────────────────

    public uint GenSource()
    {
        lock (_gate)
        {
            var name = _nextSource++;
            _sources[name] = new FakeSource { Name = name };
            Record("GenSource", source: name);
            return name;
        }
    }

    public uint GenBuffer()
    {
        lock (_gate)
        {
            var name = _nextBuffer++;
            _buffers[name] = new FakeBuffer { Name = name };
            Record("GenBuffer", buffer: name);
            return name;
        }
    }

    public void DeleteSource(uint source)
    {
        lock (_gate)
        {
            Record("DeleteSource", source: source);
            var s = RequireSource(source);
            // §4.3.1: "A playing source can be deleted - the source will be
            // stopped automatically and then deleted."
            s.State = AlState.Stopped;
            s.Queue.Clear();
            s.Deleted = true;
        }
    }

    public void DeleteBuffer(uint buffer)
    {
        lock (_gate)
        {
            Record("DeleteBuffer", buffer: buffer);
            var b = RequireBuffer(buffer);
            // §5.2.2: "A buffer which is attached to a source can not be deleted."
            if (IsQueuedAnywhereUnderLock(buffer))
            {
                throw new InvalidOperationException(
                    $"alDeleteBuffers on buffer {buffer}, which is still queued on a source. "
                        + "OpenAL refuses this; the buffer must be unqueued first."
                );
            }
            b.Deleted = true;
        }
    }

    public void SourcePlay(uint source)
    {
        lock (_gate)
        {
            Record("SourcePlay", source: source);
            var s = RequireSource(source);
            // §4.3.6 state transitions. Only a PAUSED source resumes where it left
            // off; every other state starts at the head of the queue.
            //
            // A second Play on an already-PLAYING source is not a no-op, which is
            // the transition most often assumed away: "alSourcePlay applied to a
            // AL_PLAYING source will restart the source from the beginning. It will
            // not affect the configuration, and will leave the source in AL_PLAYING
            // state, but reset the sampling offset to the beginning." §4.3.2 says it
            // again from the other side: "An alSourceStop, alSourceRewind, or a
            // second alSourcePlay call will reset the offset to the beginning of the
            // buffer." A sink that issues a redundant SourcePlay therefore replays
            // audio the device already played.
            switch (s.State)
            {
                case AlState.Paused:
                    s.State = AlState.Playing;
                    break;
                default:
                    s.Cursor = 0;
                    foreach (var entry in s.Queue)
                        entry.Played = false;
                    s.State = AlState.Playing;
                    break;
            }
        }
    }

    public void SourcePause(uint source)
    {
        lock (_gate)
        {
            Record("SourcePause", source: source);
            var s = RequireSource(source);
            // §4.3.6: Pause is a legal NOP on INITIAL, PAUSED and STOPPED.
            if (s.State == AlState.Playing)
                s.State = AlState.Paused;
        }
    }

    public void SourceStop(uint source)
    {
        lock (_gate)
        {
            Record("SourceStop", source: source);
            var s = RequireSource(source);
            // §4.3.6: NOP on INITIAL and STOPPED. §4.3.2 on AL_SAMPLE_OFFSET: an
            // alSourceStop resets the offset to the beginning.
            if (s.State is AlState.Playing or AlState.Paused)
            {
                s.State = AlState.Stopped;
                s.Cursor = 0;
            }
        }
    }

    public void SourceRewind(uint source)
    {
        lock (_gate)
        {
            Record("SourceRewind", source: source);
            var s = RequireSource(source);
            // §4.3.6: from any state the source ends at INITIAL with the sampling
            // offset reset. NOP on a source already INITIAL.
            s.State = AlState.Initial;
            s.Cursor = 0;
        }
    }

    public unsafe void BufferData(
        uint buffer,
        BufferFormat format,
        void* data,
        int size,
        int frequency
    )
    {
        lock (_gate)
        {
            Record("BufferData", buffer: buffer, detail: $"{format} {size}B @{frequency}Hz");
            var b = RequireBuffer(buffer);
            // §5.1: a PENDING buffer "cannot be deleted or changed". Refilling a
            // buffer still on a queue is the silent way a sink corrupts audio it
            // has not finished playing.
            if (IsQueuedAnywhereUnderLock(buffer))
            {
                throw new InvalidOperationException(
                    $"alBufferData on buffer {buffer}, which is still queued on a source. "
                        + "OpenAL refuses this; the buffer must be unqueued first."
                );
            }

            var samples = new short[size / sizeof(short)];
            var src = new ReadOnlySpan<short>(data, samples.Length);
            src.CopyTo(samples);

            b.Samples = samples;
            b.Format = format;
            b.SampleRate = frequency;
        }
    }

    public unsafe void SourceQueueBuffers(uint source, int count, uint* buffers)
    {
        lock (_gate)
        {
            var names = new uint[count];
            new ReadOnlySpan<uint>(buffers, count).CopyTo(names);
            Record(
                "SourceQueueBuffers",
                source: source,
                detail: string.Join(",", names)
            );

            var s = RequireSource(source);

            // §4.3.5: "All buffers in a queue must have the same format and
            // attributes... An attempt to mix formats or other buffer attributes
            // will result in a failure... If the queue operation fails, the source
            // queue will remain unchanged."
            foreach (var name in names)
            {
                var b = RequireBuffer(name);
                if (s.QueueFormat is { } existing && (existing != b.Format || s.QueueSampleRate != b.SampleRate))
                {
                    throw new InvalidOperationException(
                        $"alSourceQueueBuffers mixing formats on source {source}: queue is "
                            + $"{existing} @{s.QueueSampleRate}Hz, buffer {name} is "
                            + $"{b.Format} @{b.SampleRate}Hz."
                    );
                }
            }

            foreach (var name in names)
            {
                var b = _buffers[name];
                s.QueueFormat ??= b.Format;
                s.QueueSampleRate = b.SampleRate;
                s.Queue.Add(
                    new FakeQueueEntry
                    {
                        Buffer = name,
                        SamplesPerChannel = b.SamplesPerChannel,
                    }
                );
            }
        }
    }

    public unsafe void SourceUnqueueBuffers(uint source, int count, uint* buffers)
    {
        lock (_gate)
        {
            Record("SourceUnqueueBuffers", source: source, detail: count.ToString());
            var s = RequireSource(source);

            int available = BuffersProcessedUnderLock(s);
            // §4.3.5: "The operation will fail with an AL_INVALID_VALUE error if
            // more buffers are requested than available, leaving the destination
            // arguments unchanged."
            if (count > available)
            {
                throw new InvalidOperationException(
                    $"alSourceUnqueueBuffers asked for {count} buffers from source {source}, "
                        + $"which reports {available} processed (state {s.State}, "
                        + $"queued {s.Queue.Count}). OpenAL fails this and returns nothing."
                );
            }

            var dest = new Span<uint>(buffers, count);
            for (int i = 0; i < count; i++)
            {
                var entry = s.Queue[0];
                s.Queue.RemoveAt(0);
                dest[i] = entry.Buffer;
                // The offset is relative to the head of the queue, so removing an
                // entry shifts it back by that entry's length.
                s.Cursor = Math.Max(0, s.Cursor - entry.SamplesPerChannel);
            }

            if (s.Queue.Count == 0)
                s.QueueFormat = null;
        }
    }

    public void GetSourceProperty(uint source, GetSourceInteger param, out int value)
    {
        lock (_gate)
        {
            var s = RequireSource(source);
            value = param switch
            {
                GetSourceInteger.BuffersQueued => s.Queue.Count,
                GetSourceInteger.BuffersProcessed => BuffersProcessedUnderLock(s),
                GetSourceInteger.SourceState => (int)ToSilkState(s.State),
                GetSourceInteger.SampleOffset => SampleOffsetUnderLock(s),
                GetSourceInteger.ByteOffset => SampleOffsetUnderLock(s) * s.BytesPerFrame,
                _ => 0,
            };
            Record("GetSourceProperty", source: source, detail: $"{param}={value}");
        }
    }

    public void GetBufferProperty(uint buffer, GetBufferInteger param, out int value)
    {
        lock (_gate)
        {
            var b = RequireBuffer(buffer);
            value = param switch
            {
                GetBufferInteger.Size => b.Samples.Length * sizeof(short),
                GetBufferInteger.Channels => b.Channels,
                GetBufferInteger.Bits => b.Bits,
                GetBufferInteger.Frequency => b.SampleRate,
                _ => 0,
            };
            Record("GetBufferProperty", buffer: buffer, detail: $"{param}={value}");
        }
    }

    public void SetSourceProperty(uint source, SourceFloat param, float value)
    {
        lock (_gate)
        {
            Record("SetSourceProperty", source: source, detail: $"{param}={value}");
            var s = RequireSource(source);
            if (param == SourceFloat.Gain)
                s.Gain = value;
        }
    }

    // ── Queries a test asserts against ──────────────────────────────────────

    /// <summary>The number of buffers <paramref name="source"/> reports processed, per spec.</summary>
    public int BuffersProcessed(uint source)
    {
        lock (_gate)
            return BuffersProcessedUnderLock(RequireSource(source));
    }

    /// <summary>The number of buffers currently on <paramref name="source"/>'s queue.</summary>
    public int BuffersQueued(uint source)
    {
        lock (_gate)
            return RequireSource(source).Queue.Count;
    }

    /// <summary>The execution state of <paramref name="source"/>.</summary>
    public AlState StateOf(uint source)
    {
        lock (_gate)
            return RequireSource(source).State;
    }

    /// <summary>The gain last written to <paramref name="source"/>.</summary>
    public float GainOf(uint source)
    {
        lock (_gate)
            return RequireSource(source).Gain;
    }

    /// <summary>The only source the sink generated. Fails if there is not exactly one.</summary>
    public uint SingleSource()
    {
        lock (_gate)
        {
            var live = _sources.Values.Where(s => !s.Deleted).Select(s => s.Name).ToArray();
            if (live.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one live source, found {live.Length}."
                );
            }
            return live[0];
        }
    }

    /// <summary>The names of the recorded calls, in order — the sequence a test asserts on.</summary>
    public IReadOnlyList<string> CallNames
    {
        get
        {
            lock (_gate)
                return _calls.Select(c => c.Name).ToArray();
        }
    }

    /// <summary>Renders the call log, one call per line, for an assertion failure message.</summary>
    public string DescribeCalls()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var call in _calls)
                sb.AppendLine(call.ToString());
            return sb.ToString();
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private int BuffersProcessedUnderLock(FakeSource s)
    {
        // §4.3.2, AL_BUFFERS_PROCESSED: "On a source in the AL_STOPPED state, all
        // buffers are processed. On a source in the AL_INITIAL state, no buffers
        // are processed, all buffers are pending."
        //
        // The STOPPED case is the whole of #133: buffers queued onto a source that
        // starved are reported processed without the device ever playing them, so
        // a sink that recycles on that count can never build its queue back up to
        // the pre-buffer threshold.
        if (s.State == AlState.Stopped)
            return s.Queue.Count;
        if (s.State == AlState.Initial)
            return 0;

        int processed = 0;
        long boundary = 0;
        foreach (var entry in s.Queue)
        {
            boundary += entry.SamplesPerChannel;
            if (s.Cursor < boundary)
                break;
            processed++;
        }
        return processed;
    }

    private static int SampleOffsetUnderLock(FakeSource s)
    {
        // §4.3.2, AL_SAMPLE_OFFSET: "The position is relative to the beginning of
        // all the queued buffers for the source." An alSourceStop or alSourceRewind
        // resets it, and both transitions already zero the cursor.
        if (s.State is AlState.Stopped or AlState.Initial)
            return 0;
        return (int)s.Cursor;
    }

    private bool IsQueuedAnywhereUnderLock(uint buffer) =>
        _sources.Values.Any(s => !s.Deleted && s.Queue.Any(e => e.Buffer == buffer));

    private FakeSource RequireSource(uint name)
    {
        if (!_sources.TryGetValue(name, out var s) || s.Deleted)
        {
            throw new InvalidOperationException(
                $"al* call against source {name}, which was never generated or is deleted."
            );
        }
        return s;
    }

    private FakeBuffer RequireBuffer(uint name)
    {
        if (!_buffers.TryGetValue(name, out var b) || b.Deleted)
        {
            throw new InvalidOperationException(
                $"al* call against buffer {name}, which was never generated or is deleted."
            );
        }
        return b;
    }

    private void Record(string name, uint? source = null, uint? buffer = null, string? detail = null) =>
        _calls.Add(
            new AlCall(
                _calls.Count,
                name,
                source,
                buffer,
                detail,
                Environment.CurrentManagedThreadId
            )
        );

    private static SourceState ToSilkState(AlState state) =>
        state switch
        {
            AlState.Initial => Silk.NET.OpenAL.SourceState.Initial,
            AlState.Playing => Silk.NET.OpenAL.SourceState.Playing,
            AlState.Paused => Silk.NET.OpenAL.SourceState.Paused,
            _ => Silk.NET.OpenAL.SourceState.Stopped,
        };

    private sealed class FakeLease : IOpenAlContextLease
    {
        private FakeOpenAlDevice? _device;

        internal FakeLease(FakeOpenAlDevice device) => _device = device;

        public IOpenAlApi Al =>
            _device ?? throw new ObjectDisposedException(nameof(FakeLease));

        public string DeviceName =>
            (_device ?? throw new ObjectDisposedException(nameof(FakeLease))).DeviceName;

        public bool IsConnected =>
            (_device ?? throw new ObjectDisposedException(nameof(FakeLease))).Connected;

        public void Dispose()
        {
            var device = Interlocked.Exchange(ref _device, null);
            if (device is null)
                return;
            lock (device._gate)
                device._leasesDisposed++;
        }
    }

    private sealed class FakeBuffer
    {
        public uint Name;
        public short[] Samples = [];
        public BufferFormat Format = BufferFormat.Stereo16;
        public int SampleRate;
        public bool Deleted;

        public int Channels =>
            Format is BufferFormat.Mono8 or BufferFormat.Mono16 ? 1 : 2;

        public int Bits => Format is BufferFormat.Mono8 or BufferFormat.Stereo8 ? 8 : 16;

        public int SamplesPerChannel => Channels > 0 ? Samples.Length / Channels : 0;
    }

    private sealed class FakeQueueEntry
    {
        public uint Buffer;
        public int SamplesPerChannel;
        public bool Played;
    }

    private sealed class FakeSource
    {
        public uint Name;
        public AlState State = AlState.Initial;
        public List<FakeQueueEntry> Queue = new();
        public long Cursor;
        public bool Deleted;
        public float Gain = 1f;
        public BufferFormat? QueueFormat;
        public int QueueSampleRate;

        public long TotalQueuedSamples => Queue.Sum(e => (long)e.SamplesPerChannel);

        public int BytesPerFrame =>
            QueueFormat is BufferFormat.Mono16 ? 2
            : QueueFormat is BufferFormat.Stereo16 ? 4
            : QueueFormat is BufferFormat.Mono8 ? 1
            : 2;
    }
}

/// <summary>A source's execution state, mirroring the four OpenAL states.</summary>
public enum AlState
{
    Initial,
    Playing,
    Paused,
    Stopped,
}

/// <summary>One recorded <c>al*</c> call.</summary>
/// <param name="Index">Position in the call log.</param>
/// <param name="Name">The entry point, without the <c>al</c> prefix.</param>
/// <param name="Source">Source name, when the call names one.</param>
/// <param name="Buffer">Buffer name, when the call names one.</param>
/// <param name="Detail">Arguments or result, rendered for the failure message.</param>
/// <param name="ThreadId">Managed thread the call arrived on.</param>
public readonly record struct AlCall(
    int Index,
    string Name,
    uint? Source,
    uint? Buffer,
    string? Detail,
    int ThreadId
)
{
    public override string ToString()
    {
        var target =
            Source is { } s ? $" src={s}"
            : Buffer is { } b ? $" buf={b}"
            : string.Empty;
        var detail = Detail is null ? string.Empty : $" [{Detail}]";
        return $"#{Index,-4} t{ThreadId,-3} {Name}{target}{detail}";
    }
}
