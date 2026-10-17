// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Decoding;
using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Playback;

/// <summary>
/// Factory that creates <see cref="SubstrateSession"/> instances bound
/// to the configured <see cref="IVideoSink"/> / <see cref="IAudioSink"/>
/// and hardware-decode policy. Implements the controller-facing
/// <see cref="IPlaybackSessionFactory"/> contract, and creates the per-item
/// runtimes of a <see cref="PlaylistSession"/>.
/// </summary>
/// <remarks>
/// The factory captures the long-lived sinks + options + optional
/// consumer-side configurators at construction time and produces a
/// fresh session per controller load. The controller owns session
/// disposal. The factory owns one resource of its own: the hardware
/// device cache every session it creates borrows from, which lives as
/// long as the controller and is released by <see cref="Dispose"/>, after
/// the controller has disposed its session. The sinks it captured stay
/// the caller's.
/// </remarks>
internal sealed class SubstrateSessionFactory
    : IPlaybackSessionFactory,
        IPlaylistItemRuntimeFactory,
        IDisposable
{
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly HardwareDecodeMode _hwMode;
    private readonly FrameFlow.Media.HardwareDecodeCapabilities? _hwCapabilities;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<
        GraphChain<IVideoFrame>,
        GraphChain<IVideoFrame>
    >? _videoConfigurator;
    private readonly Func<
        GraphChain<PcmAudioBuffer>,
        GraphChain<PcmAudioBuffer>
    >? _audioConfigurator;
    private readonly bool? _yieldHardwareFrames;
    private readonly HardwareDevice? _hardwareDevice;
    private readonly IReadOnlyList<HardwareDecodeBackendKind> _preferredBackends;
    private readonly IReadOnlyList<string> _excludedCodecs;
    private readonly int _decoderThreads;

    private readonly LatenessRecoveryOptions? _latenessRecovery;

    // One per factory, because the factory owns the sink and the sink is what the
    // announcement is about. Every item it creates shares it (#287).
    private readonly VideoFormatAnnouncer _formatAnnouncer = new();

    // Beside it for the same reason: a run of failed calls is the sink's, not an item's (#703).
    private readonly VideoSinkFailures _sinkFailures = new();

    // The hardware devices every item this factory creates shares, one per backend, so an item
    // boundary does not create a device. None when the caller lends a device, which every item
    // borrows instead, or when hardware decode is disabled.
    private readonly HardwareDeviceCache? _deviceCache;

    /// <summary>
    /// The lateness-recovery tuning every session this factory creates carries, for the tests
    /// that check a caller's options reached here rather than being dropped on the way (#319).
    /// </summary>
    internal LatenessRecoveryOptions? LatenessRecovery => _latenessRecovery;

    /// <summary>The caller's backend order every session this factory creates carries (#532).</summary>
    internal IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends => _preferredBackends;

    /// <summary>The caller's codec exclusions every session this factory creates carries.</summary>
    internal IReadOnlyList<string> ExcludedCodecs => _excludedCodecs;

    /// <summary>The software decoders' thread count every item gets (#788).</summary>
    internal int DecoderThreads => _decoderThreads;

    public SubstrateSessionFactory(
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hwMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<IVideoFrame>, GraphChain<IVideoFrame>>? videoConfigurator = null,
        Func<GraphChain<PcmAudioBuffer>, GraphChain<PcmAudioBuffer>>? audioConfigurator = null,
        bool? yieldHardwareFrames = null,
        LatenessRecoveryOptions? latenessRecovery = null,
        HardwareDevice? hardwareDevice = null,
        IReadOnlyList<HardwareDecodeBackendKind>? preferredBackends = null,
        IReadOnlyList<string>? excludedCodecs = null,
        int decoderThreads = 0
    )
    {
        // Checked here rather than in the worker: this runs on the caller's thread,
        // inside PlaybackController.Create, so a contradictory pair fails next to the
        // line that wrote it instead of surfacing later as a playback fault.
        latenessRecovery?.Validate();
        _latenessRecovery = latenessRecovery;
        _videoSink = videoSink;
        _audioSink = audioSink;
        _hwMode = hwMode;
        _hwCapabilities = hardwareDecodeCapabilities;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _videoConfigurator = videoConfigurator;
        _audioConfigurator = audioConfigurator;
        _yieldHardwareFrames = yieldHardwareFrames;
        _hardwareDevice = hardwareDevice;
        // Copied: a caller's array changed after Create would otherwise reorder later items.
        _preferredBackends = preferredBackends is null ? [] : [.. preferredBackends];
        _excludedCodecs = excludedCodecs is null ? [] : [.. excludedCodecs];
        _decoderThreads = decoderThreads;
        _deviceCache = hardwareDevice is null && hwMode != HardwareDecodeMode.Disabled
            ? new HardwareDeviceCache(_loggerFactory.CreateLogger<HardwareDeviceCache>())
            : null;
    }

    public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks) =>
        Create(clock, callbacks);

    public IPlaylistItemRuntime CreateItem(IPlaybackClock clock, SessionCallbacks callbacks) =>
        Create(clock, callbacks);

    public IPlaylistItemRuntime CreatePreparedItem(IPlaybackClock clock, SessionCallbacks callbacks) =>
        Create(clock, callbacks, detached: true);

    private SubstrateSession Create(IPlaybackClock clock, SessionCallbacks callbacks, bool detached = false)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new SubstrateSession(
            _videoSink,
            _audioSink,
            clock,
            callbacks,
            _hwMode,
            _hwCapabilities,
            _loggerFactory,
            _videoConfigurator,
            _audioConfigurator,
            _yieldHardwareFrames,
            _hardwareDevice,
            _preferredBackends,
            _excludedCodecs
        )
        {
            DecoderThreads = _decoderThreads,
            LatenessRecovery = _latenessRecovery,
            FormatAnnouncer = _formatAnnouncer,
            SinkFailures = _sinkFailures,
            DeviceCache = _deviceCache,
            Detached = detached,
        };
    }

    /// <summary>
    /// Releases the shared hardware devices. Decoders still open hold their own references, so
    /// this never pulls a device from under one.
    /// </summary>
    public void Dispose() => _deviceCache?.Dispose();
}
