// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;

namespace FrameFlow.Media.Tests.Doubles;

/// <summary>
/// A frame that counts its references and its releases past zero, so a test can say exactly who
/// still holds it.
/// </summary>
internal sealed class CountedFrame(
    int width = 4,
    int height = 4,
    PixelFormat format = PixelFormat.Bgra32,
    SampleAspectRatio sampleAspectRatio = default,
    VideoRotation rotation = VideoRotation.None,
    FrameMemoryDomain memoryDomain = FrameMemoryDomain.Cpu
) : IVideoFrame
{
    private int _references = 1;
    private int _overReleases;

    /// <summary>The references still held.</summary>
    public int References => Volatile.Read(ref _references);

    /// <summary>Releases past zero: each one a caller that disposed what it did not own.</summary>
    public int OverReleases => Volatile.Read(ref _overReleases);

    public int Width { get; } = width;
    public int Height { get; } = height;
    public TimeSpan Pts { get; init; }
    public TimeSpan Duration => TimeSpan.FromMilliseconds(33);
    public PixelFormat Format { get; } = format;
    public FrameMemoryDomain MemoryDomain { get; } = memoryDomain;

    public SampleAspectRatio SampleAspectRatio { get; } =
        sampleAspectRatio == default ? SampleAspectRatio.Square : sampleAspectRatio;

    public VideoRotation Rotation { get; } = rotation;

    public IVideoFrame AddRef()
    {
        if (Interlocked.Increment(ref _references) <= 1)
        {
            Interlocked.Decrement(ref _references);
            throw new ObjectDisposedException(nameof(CountedFrame));
        }
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) < 0)
        {
            Interlocked.Increment(ref _references);
            Interlocked.Increment(ref _overReleases);
        }
    }

    public CpuFrameData ToCpu() => throw new NotSupportedException();
}

/// <summary>
/// A sink that records what it is given, in a log it can share with other sinks to show the order
/// they were called in. It disposes each frame unless told to keep it.
/// </summary>
internal class RecordingSink(string name, List<string>? log = null) : IVideoSink
{
    private readonly List<string> _log = log ?? [];

    public string Name => name;

    /// <summary>The frames it was given, in order.</summary>
    public List<IVideoFrame> Frames { get; } = [];

    /// <summary>The formats it was told, in order.</summary>
    public List<VideoFormatInfo> Formats { get; } = [];

    /// <summary>The cancellation tokens its presents were given.</summary>
    public List<CancellationToken> Tokens { get; } = [];

    /// <summary>When set, it keeps each frame instead of disposing it.</summary>
    public bool Keeps { get; init; }

    /// <summary>When set, a present disposes its frame, as the owner, and then throws this.</summary>
    public Exception? PresentThrows { get; init; }

    /// <summary>When set, a format change throws this.</summary>
    public Exception? FormatThrows { get; init; }

    public int? MaxHeldFrames { get; init; } = 0;

    public FrameMemoryDomains AcceptedDomains { get; init; } = FrameMemoryDomains.Cpu;

    public IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends { get; init; } = [];

    public bool Disposed { get; private set; }

    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        _log.Add(name);
        Frames.Add(frame);
        Tokens.Add(ct);
        if (!Keeps || PresentThrows is not null)
            frame.Dispose();
        if (PresentThrows is { } ex)
            throw ex;
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        _log.Add(name);
        Formats.Add(format);
        if (FormatThrows is { } ex)
            throw ex;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public override string ToString() => name;
}

/// <summary>A <see cref="RecordingSink"/> one claimant at a time can claim, as the in-box sinks are.</summary>
internal sealed class ClaimableRecordingSink(string name, List<string>? log = null)
    : RecordingSink(name, log),
        IClaimableSink
{
    private readonly SinkClaimGate _claims = new(typeof(ClaimableRecordingSink));

    public IDisposable? TryClaim(string claimant) => _claims.TryClaim(claimant);

    public string? Claimant => _claims.Claimant;

    public long ClaimOrder => _claims.ClaimOrder;
}
