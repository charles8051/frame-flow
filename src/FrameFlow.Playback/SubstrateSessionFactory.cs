// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Graph;
using FrameFlow.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Playback;

/// <summary>
/// Factory that creates <see cref="SubstrateSession"/> instances bound
/// to the configured <see cref="IVideoSink"/> / <see cref="IAudioSink"/>
/// and hardware-decode policy. Implements the controller-facing
/// <see cref="IPlaybackSessionFactory"/> contract.
/// </summary>
/// <remarks>
/// The factory captures the long-lived sinks + options + optional
/// consumer-side configurators at construction time and produces a
/// fresh session per controller load. The controller owns session
/// disposal; the factory owns nothing beyond the captured config.
/// </remarks>
internal sealed class SubstrateSessionFactory : IPlaybackSessionFactory
{
    private readonly IVideoSink? _videoSink;
    private readonly IAudioSink? _audioSink;
    private readonly HardwareDecodeMode _hwMode;
    private readonly FrameFlow.Media.HardwareDecodeCapabilities? _hwCapabilities;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<
        GraphChain<VideoFrameRef>,
        GraphChain<VideoFrameRef>
    >? _videoConfigurator;
    private readonly Func<
        GraphChain<PcmAudioBufferRef>,
        GraphChain<PcmAudioBufferRef>
    >? _audioConfigurator;
    private readonly bool _yieldHardwareFrames;

    public SubstrateSessionFactory(
        IVideoSink? videoSink = null,
        IAudioSink? audioSink = null,
        HardwareDecodeMode hwMode = HardwareDecodeMode.Auto,
        FrameFlow.Media.HardwareDecodeCapabilities? hardwareDecodeCapabilities = null,
        ILoggerFactory? loggerFactory = null,
        Func<GraphChain<VideoFrameRef>, GraphChain<VideoFrameRef>>? videoConfigurator = null,
        Func<GraphChain<PcmAudioBufferRef>, GraphChain<PcmAudioBufferRef>>? audioConfigurator = null,
        bool yieldHardwareFrames = false
    )
    {
        _videoSink = videoSink;
        _audioSink = audioSink;
        _hwMode = hwMode;
        _hwCapabilities = hardwareDecodeCapabilities;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _videoConfigurator = videoConfigurator;
        _audioConfigurator = audioConfigurator;
        _yieldHardwareFrames = yieldHardwareFrames;
    }

    public IPlaybackSession CreateSession(IPlaybackClock clock, SessionCallbacks callbacks)
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
            _yieldHardwareFrames
        );
    }
}
