// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FrameFlow.Media;
using FrameFlow.Media.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.SDL;

namespace FrameFlow.SDL;

/// <summary>
/// An <see cref="IVideoSink"/> that renders video frames to an SDL2 window.
/// </summary>
/// <remarks>
/// <para>
/// Uses a split-thread pattern:
/// <see cref="PresentAsync"/> stores a frame via <see cref="Interlocked.Exchange{T}(ref T, T)"/>
/// from any thread, and <see cref="RenderPendingFrame"/> consumes it on the SDL thread.
/// </para>
/// <para>
/// Works with <see cref="IVideoFrame"/> and owns its <see cref="IFramePool"/>, which
/// provides backpressure when all frames are in-flight.
/// </para>
/// <para>
/// Frames are accessed via <see cref="IVideoFrame.AsCpu()"/> for pixel data.
/// After rendering, frames are disposed (returning them to the pool).
/// When a new frame overwrites a pending frame that hasn't been rendered yet,
/// the old frame is disposed (dropped).
/// </para>
/// </remarks>
public sealed unsafe partial class SdlVideoSink : IVideoSink
{
    private static readonly VideoSinkMeters Meters = new(
        "FrameFlow.SDL.Sink",
        "frameflow.sdl.sink",
        nameof(SdlVideoSink)
    );

    private readonly Silk.NET.SDL.Sdl? _sdl;
    private readonly ILogger<SdlVideoSink> _logger;
    private Window* _window;
    private Renderer* _renderer;
    private Texture* _texture;
    private int _textureWidth;
    private int _textureHeight;

    private readonly LatestWinsFrameSlot _slot = new();
    private readonly VideoSinkTelemetry _telemetry;
    private volatile bool _destroyRequested;
    private int _resourcesDestroyedFlag;
    private int _sdlThreadId;

    /// <summary>Gets the total number of frames rendered to the SDL window.</summary>
    public int RenderedFrameCount => (int)_telemetry.PresentedCount;

    /// <summary>Gets the total number of frames dropped because the SDL thread lagged.</summary>
    public int DroppedFrameCount => (int)_telemetry.DroppedCount;

    /// <inheritdoc />
    public IFramePool FramePool { get; }

    /// <summary>
    /// Initializes an SDL2 video sink and creates the SDL window and renderer.
    /// Must be called on the SDL thread — the same thread that called <c>SDL_Init</c>.
    /// </summary>
    /// <param name="sdl">The SDL2 API instance. The sink does not own this instance.</param>
    /// <param name="framePool">The frame pool that produces frames for this sink.</param>
    /// <param name="title">Window title displayed in the title bar.</param>
    /// <param name="width">Initial window width in pixels.</param>
    /// <param name="height">Initial window height in pixels.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="SdlException">
    /// Thrown if <c>SDL_CreateWindow</c> or <c>SDL_CreateRenderer</c> fails.
    /// </exception>
    public SdlVideoSink(
        Silk.NET.SDL.Sdl sdl,
        IFramePool framePool,
        string title,
        int width,
        int height,
        ILogger<SdlVideoSink>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(sdl);
        ArgumentNullException.ThrowIfNull(framePool);

        _sdl = sdl;
        FramePool = framePool;
        _telemetry = new VideoSinkTelemetry(Meters, _slot);
        _logger = logger ?? NullLogger<SdlVideoSink>.Instance;
        _sdlThreadId = Environment.CurrentManagedThreadId;

        _window = sdl.CreateWindow(
            title,
            Silk.NET.SDL.Sdl.WindowposUndefined,
            Silk.NET.SDL.Sdl.WindowposUndefined,
            width,
            height,
            (uint)(WindowFlags.Shown | WindowFlags.Resizable)
        );

        if (_window == null)
            throw new SdlException("SDL_CreateWindow", sdl.GetErrorS());

        _renderer = sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Accelerated);
        if (_renderer == null)
        {
            sdl.DestroyWindow(_window);
            _window = null;
            throw new SdlException("SDL_CreateRenderer", sdl.GetErrorS());
        }

        sdl.RenderClear(_renderer);
        sdl.RenderPresent(_renderer);

        LogSinkCreated(_logger, width, height);
    }

    /// <summary>
    /// Private constructor for headless/test mode — no SDL resources are created.
    /// </summary>
    private SdlVideoSink(IFramePool framePool, ILogger<SdlVideoSink>? logger)
    {
        _sdl = null;
        FramePool = framePool;
        _telemetry = new VideoSinkTelemetry(Meters, _slot);
        _logger = logger ?? NullLogger<SdlVideoSink>.Instance;
        _sdlThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Creates a headless <see cref="SdlVideoSink"/> for use in tests or environments
    /// without a display. All SDL calls are skipped; <see cref="PresentAsync"/> accepts
    /// and disposes frames normally via <see cref="RenderPendingFrame"/>.
    /// </summary>
    /// <param name="framePool">The frame pool for backpressure and frame lifecycle.</param>
    /// <param name="logger">Optional logger.</param>
    public static SdlVideoSink CreateHeadless(
        IFramePool framePool,
        ILogger<SdlVideoSink>? logger = null
    ) => new(framePool, logger);

    /// <inheritdoc />
    public ValueTask PresentAsync(IVideoFrame frame, CancellationToken ct)
    {
        if (_destroyRequested)
        {
            frame.Dispose();
            return ValueTask.CompletedTask;
        }

        // Latest-wins: the slot disposes any superseded frame and counts the drop. The
        // returned flag drives this sink's drop telemetry (meter + log) outside the slot.
        if (_slot.TrySet(frame))
        {
            _telemetry.RecordSupersededDrop();
            LogFrameDropped(_logger, RenderedFrameCount);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFormatChangedAsync(VideoFormatInfo format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(format);

        LogFormatChanged(_logger, format.Width, format.Height, format.Format);

        // Texture recreation will happen lazily in RenderPendingFrame via EnsureTexture.
        // Reset dimensions to force recreation on next render.
        _textureWidth = 0;
        _textureHeight = 0;

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Renders the most recently presented frame to the SDL window.
    /// Must be called on the SDL thread, typically on every event-loop iteration.
    /// </summary>
    /// <remarks>
    /// If no frame has arrived since the last call, returns immediately.
    /// Also performs deferred SDL resource destruction if <see cref="DisposeAsync"/>
    /// has been called.
    /// </remarks>
    public void RenderPendingFrame()
    {
        AssertSdlThread();

        if (_destroyRequested)
        {
            _slot.Take()?.Dispose();
            DestroyResourcesCore();
            return;
        }

        var frame = _slot.Take();
        if (frame is null)
            return;

        try
        {
            var cpuData = frame.AsCpu();
            if (cpuData is not null && _sdl is not null && _renderer != null)
            {
                var data = cpuData.Value;
                if (EnsureTexture(data.Width, data.Height))
                {
                    var span = data.PlaneY.Span;
                    fixed (byte* pixels = span)
                    {
                        _sdl.UpdateTexture(_texture, null, pixels, data.StrideY);
                    }

                    _sdl.RenderClear(_renderer);
                    _sdl.RenderCopy(_renderer, _texture, null, null);
                    _sdl.RenderPresent(_renderer);

                    // Counted here rather than at Take: a frame the texture path could not
                    // draw never reached the screen and must not read as presented.
                    _telemetry.RecordPresented(frame.Pts);

                    var presented = RenderedFrameCount;
                    if (presented % 10 == 0 && _window != null)
                    {
                        _sdl.SetWindowTitle(
                            _window,
                            $"FrameFlow — frame {presented} | PTS {frame.Pts.TotalMilliseconds:F0}ms"
                        );
                    }
                }
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Before this existed the sink inherited the default <see cref="IVideoSink"/>
    /// implementation, so every SDL session reported
    /// <see cref="VideoSinkDiagnosticsSnapshot.Empty"/> into the playback pipeline snapshot —
    /// zero frames presented and no A/V drift, whatever was actually on screen.
    /// </remarks>
    public VideoSinkDiagnosticsSnapshot GetDiagnostics() => _telemetry.Snapshot();

    /// <summary>
    /// Sets the SDL window title. Must be called on the SDL thread.
    /// </summary>
    public void SetWindowTitle(string title)
    {
        AssertSdlThread();
        if (_sdl is not null && _window != null)
            _sdl.SetWindowTitle(_window, title);
    }

    /// <summary>
    /// Destroys all SDL resources on the SDL thread. Call from a <c>finally</c>
    /// block after the event loop exits. Idempotent.
    /// </summary>
    public void DestroyResources()
    {
        AssertSdlThread();
        _destroyRequested = true;

        _slot.Take()?.Dispose();

        DestroyResourcesCore();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _destroyRequested = true;

        _slot.Take()?.Dispose();

        return ValueTask.CompletedTask;
    }

    private void DestroyResourcesCore()
    {
        if (Interlocked.CompareExchange(ref _resourcesDestroyedFlag, 1, 0) != 0)
            return;
        if (_sdl is null)
            return;

        LogDestroyingResources(_logger, _texture != null, _renderer != null, _window != null);

        if (_texture != null)
        {
            _sdl.DestroyTexture(_texture);
            _texture = null;
        }
        if (_renderer != null)
        {
            _sdl.DestroyRenderer(_renderer);
            _renderer = null;
        }
        if (_window != null)
        {
            _sdl.DestroyWindow(_window);
            _window = null;
        }
    }

    private bool EnsureTexture(int width, int height)
    {
        if (_texture != null && _textureWidth == width && _textureHeight == height)
            return true;

        if (_sdl is null || _renderer == null)
            return false;

        if (_texture != null)
            _sdl.DestroyTexture(_texture);

        _texture = _sdl.CreateTexture(
            _renderer,
            (uint)PixelFormatEnum.Argb8888,
            (int)TextureAccess.Streaming,
            width,
            height
        );

        if (_texture == null)
        {
            _textureWidth = 0;
            _textureHeight = 0;
            LogTextureCreationFailed(_logger, width, height, _sdl.GetErrorS());
            return false;
        }

        _textureWidth = width;
        _textureHeight = height;
        return true;
    }

    private void AssertSdlThread()
    {
        if (_sdl is null)
            return;
        if (Environment.CurrentManagedThreadId == _sdlThreadId)
            return;

#if DEBUG
        throw new InvalidOperationException(
            $"SDL call from thread {Environment.CurrentManagedThreadId}, "
                + $"expected SDL thread {_sdlThreadId}. "
                + "All SDL rendering calls must happen on the thread that called SDL_Init."
        );
#else
        _logger.LogWarning(
            "SDL thread-affinity violation: called from thread {CallerThreadId}, expected {SdlThreadId}",
            Environment.CurrentManagedThreadId,
            _sdlThreadId
        );
#endif
    }

    // ── Source-generated log methods ──────────────────────────

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "SdlVideoSink created: {Width}x{Height}"
    )]
    private static partial void LogSinkCreated(ILogger logger, int width, int height);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Video frame dropped: SDL thread has not consumed previous frame. RenderedFrameCount={RenderedFrameCount}"
    )]
    private static partial void LogFrameDropped(ILogger logger, int renderedFrameCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Video format changed: {Width}x{Height} {Format}"
    )]
    private static partial void LogFormatChanged(
        ILogger logger,
        int width,
        int height,
        Media.PixelFormat format
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "SDL_CreateTexture failed for {Width}x{Height}: {SdlError}"
    )]
    private static partial void LogTextureCreationFailed(
        ILogger logger,
        int width,
        int height,
        string sdlError
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Destroying SDL resources (texture={HasTexture}, renderer={HasRenderer}, window={HasWindow})"
    )]
    private static partial void LogDestroyingResources(
        ILogger logger,
        bool hasTexture,
        bool hasRenderer,
        bool hasWindow
    );
}
