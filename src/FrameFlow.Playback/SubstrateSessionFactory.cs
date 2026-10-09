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
/// disposal; the factory owns nothing beyond the captured config.
/// </remarks>
internal sealed class SubstrateSessionFactory : IPlaybackSessionFactory, IPlaylistItemRuntimeFactory
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

    private readonly LatenessRecoveryOptions? _latenessRecovery;

    // One per factory, because the factory owns the sink and the sink is what the
    // announcement is about. Every item it creates shares it (#287).
    private readonly VideoFormatAnnouncer _formatAnnouncer = new();

    /// <summary>
    /// The lateness-recovery tuning every session this factory creates carries, for the tests
    /// that check a caller's options reached here rather than being dropped on the way (#319).
    /// </summary>
    internal LatenessRecoveryOptions? LatenessRecovery => _latenessRecovery;

    /// <summary>The caller's backend order every session this factory creates carries (#532).</summary>
    internal IReadOnlyList<HardwareDecodeBackendKind> PreferredBackends => _preferredBackends;

    /// <summary>The caller's codec exclusions every session this factory creates carries.</summary>
    internal IReadOnlyList<string> ExcludedCodecs => _excludedCodecs;

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
        IReadOnlyList<string>? excludedCodecs = null
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
    }

    public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks) =>
        Create(clock, callbacks);

    public IPlaylistItemRuntime CreateItem(IPlaybackClock clock, SessionCallbacks callbacks) =>
        Create(clock, callbacks);

    private SubstrateSession Create(IPlaybackClock clock, SessionCallbacks callbacks)
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
            LatenessRecovery = _latenessRecovery,
            FormatAnnouncer = _formatAnnouncer,
        };
    }
}
