// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using FrameFlow.Media;
using FrameFlow.Player;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia;

/// <summary>
/// Drop-in player control bundling <see cref="FrameFlowVideoView"/>
/// and <see cref="FrameFlowPlayerChrome"/> into the standard
/// overlay-on-video YouTube/Netflix layout: video fills the surface,
/// chrome sits at the bottom over a transparent-to-dark gradient,
/// auto-hides on pointer idle, fades back in on pointer movement.
/// Bind <see cref="Transport"/>; handle <see cref="FileOpenRequested"/>
/// to wire your build pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition.</b> This control is a thin wrapper: it sets up
/// the Grid + gradient Border + hover machinery and forwards
/// properties. The actual playback controls live in
/// <see cref="FrameFlowPlayerChrome"/>; the actual video rendering
/// lives in <see cref="FrameFlowVideoView"/>. Drop down to either
/// individually for custom layouts (e.g. the multicast example
/// embeds the chrome in its own row below multiple video panes).
/// </para>
/// <para>
/// <b>Player ownership.</b> The control NEVER builds an
/// <see cref="IMediaTransport"/> itself — that's the consumer's job
/// via <c>FrameFlowPlayer.Create().WithMedia(path).BuildPlayerAsync()</c>. When the user
/// clicks Open (chrome's button) or drops a file (this view's
/// drag-drop), <see cref="FileOpenRequested"/> fires with the chosen
/// path; the consumer disposes any prior <see cref="Transport"/> and
/// awaits that, builds a new one, and assigns it. The prior player holds
/// the surface's sink until its disposal completes, so a build started
/// before then fails at its claim (ADR-0085, decision 6).
/// </para>
/// <para>
/// <b>Display-only path.</b> If you only need video rendering with
/// no controls, use <see cref="FrameFlowVideoView"/> directly.
/// </para>
/// <para>
/// <b>Surface ownership.</b> The player view constructs its default
/// surface and owns it (ADR-0044): <see cref="DisposeAsync"/> disposes it,
/// wherever it is, and nothing the caller supplied. Avalonia never disposes
/// a control, so a player view that is never disposed keeps the default
/// surface and its sink for as long as it is referenced. See
/// <see cref="VideoSurface"/> for what a swap does to the surfaces.
/// </para>
/// </remarks>
public sealed class FrameFlowPlayerView : UserControl, IAsyncDisposable
{
    /// <summary>The transport to display + control.</summary>
    public static readonly StyledProperty<IMediaTransport?> TransportProperty =
        AvaloniaProperty.Register<FrameFlowPlayerView, IMediaTransport?>(nameof(Transport));

    /// <inheritdoc cref="TransportProperty"/>
    public IMediaTransport? Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
    }

    /// <summary>Initial state of the Loop toggle. See
    /// <see cref="FrameFlowTransportBar.LoopByDefault"/>.</summary>
    public static readonly StyledProperty<bool> LoopByDefaultProperty =
        AvaloniaProperty.Register<FrameFlowPlayerView, bool>(nameof(LoopByDefault));

    /// <inheritdoc cref="LoopByDefaultProperty"/>
    public bool LoopByDefault
    {
        get => GetValue(LoopByDefaultProperty);
        set => SetValue(LoopByDefaultProperty, value);
    }

    /// <summary>
    /// Fires when the user clicks the Open button (and selects a
    /// file) or drops a file onto the control. Consumer handles
    /// teardown of the prior <see cref="Transport"/> and assigns
    /// the new one.
    /// </summary>
    public event EventHandler<FileOpenRequestedEventArgs>? FileOpenRequested;

    private IVideoSurface _surface;
    private Grid? _root;
    private readonly FrameFlowPlayerChrome _chrome;
    private Border? _chromeOverlay;

    // The surface this control constructed and owns, hosted or swapped out. It and the two fields
    // after it are UI thread only.
    private readonly FrameFlowVideoView _defaultSurface;

    // The player view's claim on the default surface's sink while that surface is swapped out
    // (ADR-0085, decision 7). Null while it is hosted, and once the player view is disposed.
    private IDisposable? _defaultSurfaceClaim;

    private bool _disposed;

    public FrameFlowPlayerView()
    {
        _chromeFollower = new TransportFollower(FollowTransport);
        Focusable = true;

        _defaultSurface = new FrameFlowVideoView();
        _surface = _defaultSurface;
        _chrome = new FrameFlowPlayerChrome
        {
            HasOpenButton = true,
            EnableKeyboardShortcuts = true,
        };
        _chrome.FileOpenRequested += OnChromeFileOpenRequested;
        // Pointer over the inner seek bar pins the chrome visible too —
        // the chrome panel's PointerEntered/Exited handles the
        // transport row, but the seek bar margin can extend outside
        // that handler's region depending on theme.
        _chrome.SeekBar.PointerEntered += (_, _) => _pointerOverChrome = true;
        _chrome.SeekBar.PointerExited += (_, _) => _pointerOverChrome = false;

        Content = BuildLayout();

        // Drag-drop on the whole player area (declared on the control,
        // handlers on this).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private Control BuildLayout()
    {
        // ┌─────────────────────────────────────────────────────────────┐
        // │                                                             │
        // │              FrameFlowVideoView (fills surface)             │
        // │                                                             │
        // │ ┌──── FrameFlowPlayerChrome over gradient scrim ──────────┐ │
        // │ │ status strip / seek bar / transport row                 │ │
        // │ └─────────────────────────────────────────────────────────┘ │
        // └─────────────────────────────────────────────────────────────┘
        _root = new Grid();
        _root.Children.Add(_surface.Control);

        // Bottom-aligned overlay with transparent → dark gradient so
        // chrome text reads against any video content. 24 px padding-
        // top gives the gradient room to fade in before the status
        // strip starts (YouTube/Netflix scrim look).
        _chromeOverlay = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 24, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(180, 0, 0, 0), 0.5),
                    new GradientStop(Color.FromArgb(230, 0, 0, 0), 1),
                },
            },
            Child = _chrome,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(250),
                },
            },
        };
        _chromeOverlay.PointerEntered += (_, _) => _pointerOverChrome = true;
        _chromeOverlay.PointerExited += (_, _) => _pointerOverChrome = false;
        _root.Children.Add(_chromeOverlay);

        return _root;
    }

    /// <summary>
    /// The video surface this player hosts. Defaults to a CPU <see cref="FrameFlowVideoView"/> the
    /// player view constructs and owns; assign a different <see cref="IVideoSurface"/> (e.g. the
    /// Windows zero-copy presenter <c>FrameFlow.Avalonia.Windows.CompositionInteropVideoView</c>)
    /// to swap the rendering path while keeping the transport chrome. Build the player against the
    /// surface it will play to: <see cref="AttachSink"/> hands out the hosted surface's sink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A swap never moves a player from one sink to another: a player's sink is fixed when it is
    /// built (ADR-0085, decision 7). So the player view swaps out an in-box surface,
    /// <see cref="FrameFlowVideoView"/> or <c>CompositionInteropVideoView</c>, only once it has
    /// claimed that surface's sink itself, as a player would. When a player or pass holds the sink,
    /// the swap throws and nothing changes. Otherwise no build can claim the sink while the swap
    /// runs, and the surface leaves the visual tree keeping its sink. A surface you supplied that
    /// has no sink yet has nothing to claim.
    /// </para>
    /// <para>
    /// The default surface stays alive when it is swapped out, and stays claimed by the player view
    /// until it is swapped back in or the player view is disposed: a player built against it in the
    /// meantime fails at its claim, rather than playing to a surface that is not shown. A surface
    /// you supplied goes back to you with the player view's claim released, and is yours to dispose.
    /// </para>
    /// <para>
    /// A surface of your own type exposes no sink to the player view: it is swapped out without a
    /// check and stays yours. So is a player that feeds an in-box surface's sink through a sink or
    /// graph that does not pass the claim on: the swap does not see it, and it goes on feeding a
    /// surface that is not shown.
    /// </para>
    /// <para>
    /// <see cref="DisposeAsync"/> is final: afterwards the player view swaps nothing, and assigning
    /// another surface throws.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A player or pass holds the outgoing surface's sink, and would go on feeding a surface that is
    /// not shown. The message names the holder, and nothing has changed. Dispose that player first,
    /// or build the player against the new surface.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Another surface is assigned after <see cref="DisposeAsync"/>.
    /// </exception>
    public IVideoSurface VideoSurface
    {
        get => _surface;
        set
        {
            if (value is null || ReferenceEquals(_surface, value))
                return;

            ObjectDisposedException.ThrowIf(_disposed, this);

            var outgoing = _surface;

            // Taking the claim is the check that no player feeds the outgoing surface, and it holds
            // off any build while the surface leaves the tree (ADR-0085, decision 7).
            var claim = ClaimForSwap(outgoing);
            try
            {
                _root?.Children.Remove(outgoing.Control);
                _surface = value;
                // Insert at 0 so the surface sits behind the chrome overlay (added last).
                _root?.Children.Insert(0, value.Control);
            }
            finally
            {
                // Settled even when a surface's own attach or detach throws. Once the outgoing
                // surface is out, the default one stays claimed until it comes back in or the
                // player view is disposed, and the default one coming back in is shown again, so a
                // build may claim it. Any other claim goes back with its surface.
                if (ReferenceEquals(_surface, value))
                {
                    if (ReferenceEquals(outgoing, _defaultSurface))
                        (_defaultSurfaceClaim, claim) = (claim, null);
                    else if (ReferenceEquals(value, _defaultSurface))
                        ReleaseDefaultSurfaceClaim();
                }

                claim?.Dispose();
            }
        }
    }

    // How the player view's claims name it, in the error a build against the surface's sink gets
    // while the player view holds it.
    private const string SwappingClaimant = "a FrameFlowPlayerView swapping its surface out";

    private const string DefaultSurfaceClaimant =
        "a FrameFlowPlayerView that swapped out its default surface (swap it back in to build against it)";

    /// <summary>
    /// Claims the sink of the surface the <see cref="VideoSurface"/> setter swaps out, as a player
    /// would. Returns the lease, or <see langword="null"/> when there is nothing to claim: a surface
    /// of the app's own type, a surface with no sink, a disposed one included, or a sink its owner
    /// has disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">A player or pass holds the sink.</exception>
    private IDisposable? ClaimForSwap(IVideoSurface outgoing)
    {
        if (outgoing is not IInBoxVideoSurface inBox)
            return null;

        // The default surface is the player view's own, and the one it promises stays claimed while
        // it is out, so it gets its sink now if it has none: a build written against it later finds
        // that sink claimed. A surface the caller supplied with no sink yet has nothing to claim.
        var isDefault = ReferenceEquals(outgoing, _defaultSurface);
        if (isDefault)
        {
            try
            {
                _defaultSurface.EnsureSink();
            }
            catch (ObjectDisposedException)
            {
                // The app disposed it through VideoSurface. It has no sink and presents nothing, and
                // a build against it fails on the disposal, so there is nothing to claim.
            }
        }

        if (inBox.Sink is not { } sink)
            return null;

        IDisposable? lease;
        try
        {
            lease = sink.TryClaim(isDefault ? DefaultSurfaceClaimant : SwappingClaimant);
        }
        catch (ObjectDisposedException)
        {
            // A caller's sink that its owner disposed presents nothing, whoever still feeds it, so
            // there is nothing for a claim to protect.
            return null;
        }

        return lease
            ?? throw new InvalidOperationException(
                $"The video surface {outgoing.GetType().Name} cannot be swapped out: its sink is held by "
                    + $"{sink.Claimant ?? "a player or pass"}, which would go on feeding a surface that is "
                    + "not shown. Dispose that player first, or build the player against the new surface."
            );
    }

    private void ReleaseDefaultSurfaceClaim()
    {
        var claim = _defaultSurfaceClaim;
        _defaultSurfaceClaim = null;
        claim?.Dispose();
    }

    /// <summary>
    /// Disposes the default surface, the <see cref="FrameFlowVideoView"/> the player view
    /// constructed, wherever it is: hosted, or swapped out under the player view's claim, which is
    /// released once the surface and its sink are disposed, so no build can take the sink in
    /// between. A surface the caller supplied is the caller's to dispose and is not touched. A
    /// second call does nothing. Call on the UI thread, after disposing the player built against the
    /// default surface: the surface disposes its sink even while a player still feeds it.
    /// </summary>
    /// <remarks>
    /// Avalonia never disposes a control. A player view that is never disposed keeps its default
    /// surface and that surface's sink for as long as it is referenced (ADR-0085, decision 2).
    /// Dispose is final: afterwards the default surface presents nothing,
    /// <see cref="AttachSink"/> throws <see cref="ObjectDisposedException"/> while it is hosted,
    /// and assigning <see cref="VideoSurface"/> another surface throws
    /// <see cref="ObjectDisposedException"/>.
    /// </remarks>
    /// <returns>The default surface's dispose: complete, or faulted with what it threw.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        try
        {
            return _defaultSurface.DisposeAsync();
        }
        finally
        {
            // After the dispose: a claim released first would leave a window in which a build on
            // another thread could take a sink about to be disposed. The sink's gate closes with
            // the claim still held, and the claim is released afterwards.
            ReleaseDefaultSurfaceClaim();
        }
    }

    /// <summary>
    /// Wires the logger and returns the hosted surface's <see cref="IVideoSink"/> to hand to
    /// <c>FrameFlowPlayer.Create()</c>. The sink says which memory domains it takes, and the
    /// player keeps hardware frames on the GPU for one that takes them (#294).
    /// </summary>
    /// <remarks>
    /// Can be called before or after this control joins the visual tree; either way the logger
    /// reaches the sink (see <see cref="IVideoSurface.AttachSink"/>). A player built with it holds
    /// the sink, so an in-box hosted surface, <see cref="FrameFlowVideoView"/> or
    /// <c>CompositionInteropVideoView</c>, cannot be swapped out until that player is disposed (see
    /// <see cref="VideoSurface"/>).
    /// </remarks>
    public IVideoSink AttachSink(ILoggerFactory loggerFactory) => _surface.AttachSink(loggerFactory);

    /// <summary>
    /// The underlying <see cref="FrameFlowPlayerChrome"/> — exposed
    /// for callers who need to toggle <see cref="FrameFlowPlayerChrome.HasOpenButton"/>
    /// or <see cref="FrameFlowPlayerChrome.EnableKeyboardShortcuts"/>
    /// beyond what this wrapper offers.
    /// </summary>
    public FrameFlowPlayerChrome Chrome => _chrome;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TransportProperty)
        {
            var p = change.GetNewValue<IMediaTransport?>();
            _chrome.Transport = p;

            // Wire the hover-to-reveal state subscription so the
            // chrome pins visible on Paused/Idle/Error and fades on
            // Playing-with-idle-pointer.
            _chromeFollower.Assign(p);
            if (p is null)
            {
                _chromeHideTimer?.Stop();
                ShowChrome();
            }
        }
        else if (change.Property == LoopByDefaultProperty)
        {
            _chrome.LoopByDefault = change.GetNewValue<bool>();
        }
    }

    // ── File open: chrome button + drag-drop ────────────────────────────

    private void OnChromeFileOpenRequested(object? sender, FileOpenRequestedEventArgs e) =>
        FileOpenRequested?.Invoke(this, e);

    // The DataTransfer surface, which Avalonia 11.3 and 12 both have. Avalonia 12 removed
    // DragEventArgs.Data and DataFormats.Files, which this used before.
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
            return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path is null)
                continue;
            FileOpenRequested?.Invoke(this, new FileOpenRequestedEventArgs(path));
            // First droppable file wins — matches the picker contract.
            break;
        }
    }

    // ── Hover-to-reveal chrome overlay ──────────────────────────────────
    // The chrome overlay (status strip + seek bar + transport row)
    // auto-hides after a couple of seconds of pointer idle while
    // playback is active, fading back in on any pointer movement
    // (YouTube / Netflix pattern). When the transport is paused / idle
    // / errored, the chrome stays visible — those are the states
    // where the user is most likely to want to scrub or hit Play.
    // Pointer-over-chrome pins it visible so it doesn't vanish while
    // you're reaching for the seek thumb or a transport button.
    private static readonly TimeSpan ChromeHideAfter = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _chromeHideTimer;
    private readonly TransportFollower _chromeFollower;
    private bool _pointerOverChrome;
    private TopLevel? _pointerHostTopLevel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Back in the tree with the transport it left with: follow its state again. Before the
        // timer exists, so the read starts no hide, as on the first attach. The timer and the
        // pointer hook below are tied to the visual tree whatever the transport, so they stay on
        // the attach itself rather than on the follower's presenting.
        _chromeFollower.Attach();

        _pointerHostTopLevel = TopLevel.GetTopLevel(this);
        _pointerHostTopLevel?.AddHandler(
            PointerMovedEvent,
            OnTopLevelPointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true
        );

        _chromeHideTimer = new DispatcherTimer(
            ChromeHideAfter,
            DispatcherPriority.Background,
            OnChromeHideTick
        );
        // Don't auto-start the timer; pointer movement starts it.
        ShowChrome();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pointerHostTopLevel?.RemoveHandler(PointerMovedEvent, OnTopLevelPointerMoved);
        _pointerHostTopLevel = null;
        _chromeHideTimer?.Stop();
        _chromeHideTimer = null;
        _chromeFollower.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    // On every bind, so on every return to the tree.
    private void FollowTransport(IMediaTransport transport, TransportFollower.Follow follow)
    {
        follow.On(transport.StateChanged, OnTrackedStateChanged);
        OnTrackedStateChanged(transport.State);
    }

    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        // Any movement reveals the chrome and restarts the idle timer.
        ShowChrome();
        if (ShouldAutoHide())
            RestartHideTimer();
    }

    private void OnChromeHideTick(object? sender, EventArgs e)
    {
        _chromeHideTimer?.Stop();
        if (_pointerOverChrome || !ShouldAutoHide())
            return;
        if (_chromeOverlay is not null)
            _chromeOverlay.Opacity = 0;
    }

    private void ShowChrome()
    {
        if (_chromeOverlay is not null)
            _chromeOverlay.Opacity = 1;
    }

    private void RestartHideTimer()
    {
        if (_chromeHideTimer is null)
            return;
        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    /// <summary>
    /// True when the transport is in a state where the chrome should
    /// fade after a beat. Paused / idle / errored states keep the
    /// chrome pinned so scrubbing remains easy.
    /// </summary>
    private bool ShouldAutoHide()
    {
        var transport = Transport;
        if (transport is null)
            return false;
        return transport.State == PlaybackState.Playing;
    }

    private void OnTrackedStateChanged(PlaybackState state)
    {
        if (state == PlaybackState.Playing)
            RestartHideTimer();
        else
        {
            // Paused, Ended, Error, Idle, … — pin chrome visible.
            _chromeHideTimer?.Stop();
            ShowChrome();
        }
    }
}

/// <summary>
/// Event payload for
/// <see cref="FrameFlowPlayerView.FileOpenRequested"/> and
/// <see cref="FrameFlowPlayerChrome.FileOpenRequested"/>. The
/// consumer inspects <see cref="FilePath"/>, builds an
/// <see cref="IMediaTransport"/>, and assigns it to
/// <see cref="FrameFlowPlayerView.Transport"/>.
/// </summary>
public sealed class FileOpenRequestedEventArgs(string filePath) : EventArgs
{
    /// <summary>Local filesystem path of the file the user opened.</summary>
    public string FilePath { get; } = filePath;
}
