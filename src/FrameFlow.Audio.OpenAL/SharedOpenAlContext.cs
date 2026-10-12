// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Silk.NET.OpenAL;

namespace FrameFlow.Audio.OpenAL;

/// <summary>
/// Process-wide, reference-counted owner of the single OpenAL device and
/// context shared by every <see cref="OpenAlAudioSink"/> in the process
/// (ADR-0058).
/// </summary>
/// <remarks>
/// <para>
/// <c>alcMakeContextCurrent</c> sets a <b>process-global</b> current context —
/// "there is only ever one current context for any one process." When each sink
/// opened its own device + context and made it current at activation, a second
/// sink's activation clobbered the first sink's current context, and the first
/// sink's <c>al*</c> calls — its sample-counter clock read and its buffer-queue
/// ops — silently retargeted the wrong context. Source names are numbered
/// per-context, so the two sinks' sources collided (both name <c>1</c>): one
/// sink's clock sampled the other's source and its
/// <c>SourceUnqueueBuffers</c> stole the other's buffers, corrupting the master
/// clock that paces video (ADR-0003 / ADR-0057).
/// </para>
/// <para>
/// The canonical OpenAL multi-source model is one device + one context made
/// current <i>once</i>, with each sink owning its own source and buffer pool
/// inside that single context. OpenAL "is already designed for one context being
/// used on multiple threads at once," so distinct sources can be driven
/// concurrently without a process-global lock on the audio/clock hot path; each
/// sink still serialises its own source state under its own lock. The context is
/// never changed after creation, so there is nothing left to clobber.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The device/context is created lazily on the first
/// <see cref="Acquire"/> and torn down on the last
/// <see cref="SharedOpenAlContextLease.Dispose"/>. The process-global
/// <see cref="Gate"/> guards every device/context lifecycle transition and the
/// lease refcount; it is <i>not</i> taken on the hot path. The lock-ordering
/// rule is per-sink lock → <see cref="Gate"/> (the gate is always the leaf), so
/// no sink lock is ever acquired while the gate is held.
/// </para>
/// <para>
/// <b>Exit.</b> The first open registers a <see cref="AppDomain.ProcessExit"/> handler,
/// <see cref="ReleaseForExit"/>, before it opens the device. The handler closes the device
/// whatever the lease count, so a process that ends with a sink undisposed exits cleanly (#633).
/// The refcount decisions, exit included, are <see cref="OpenAlContextLifetime"/>'s.
/// </para>
/// </remarks>
internal sealed class SharedOpenAlContext
{
    private static readonly Lock Gate = new();
    private static SharedOpenAlContext? _live;
    private static OpenAlContextLifetime _lifetime = OpenAlContextLifetime.Initial;
    private static int _deviceOpensTotal;
    private static bool _exitHookRegistered;

    // Every lease handed out and not yet given back, so an exit can ask each holder to let go
    // of the device under its own lock before the device is closed (#633).
    private static readonly HashSet<SharedOpenAlContextLease> _leases = new();

    // ── Test-only structural diagnostics (InternalsVisibleTo) ──────────────
    // Device-independent, deterministic proof that N sinks share ONE
    // device/context. Read under Gate so they stay coherent with lifecycle
    // transitions.

    /// <summary>
    /// Number of successful OpenAL device opens over the process lifetime. One
    /// shared open serves every concurrent sink, so this advances once per
    /// create-from-cold, not once per sink.
    /// </summary>
    internal static int DeviceOpensTotal
    {
        get
        {
            lock (Gate)
                return _deviceOpensTotal;
        }
    }

    /// <summary>Current number of outstanding leases (active sinks holding the shared context).</summary>
    internal static int CurrentLeaseCount
    {
        get
        {
            lock (Gate)
                return _lifetime.Leases;
        }
    }

    /// <summary>Whether the shared native device/context is live (between first acquire and last release).</summary>
    internal static bool IsContextLive
    {
        get
        {
            lock (Gate)
                return _live is not null;
        }
    }

    // ALC token values Silk.NET's enums do not carry. Both come from extensions that
    // every OpenAL Soft build ships, but an implementation without them leaves the
    // caller's buffer untouched rather than failing, so each read seeds a safe default.
    private const int AlcAllDevicesSpecifier = 0x1013; // ALC_ENUMERATE_ALL_EXT
    private const int AlcConnected = 0x313; // ALC_EXT_disconnect

    private readonly AL _al;
    private readonly SilkOpenAlApi _api;
    private readonly ALContext _alc;
    private readonly unsafe Device* _device;
    private readonly unsafe Context* _context;
    private readonly string _deviceName;

    private unsafe SharedOpenAlContext(
        AL al,
        ALContext alc,
        Device* device,
        Context* context,
        string deviceName
    )
    {
        _al = al;
        _api = new SilkOpenAlApi(al);
        _alc = alc;
        _device = device;
        _context = context;
        _deviceName = deviceName;
    }

    /// <summary>
    /// The shared AL API bound to the single current context. Every sink uses
    /// this for its source and buffer operations; it is safe to call
    /// concurrently from distinct threads on distinct sources.
    /// </summary>
    public IOpenAlApi Al => _api;

    /// <summary>
    /// The endpoint this context was opened on, as the driver names it — for example
    /// <c>OpenAL Soft on Headphones (4- Arctis 7 Game)</c>.
    /// </summary>
    /// <remarks>
    /// Captured once at open, because that is the only moment it is unambiguous:
    /// <c>alcOpenDevice("")</c> binds whichever endpoint is default then and holds that
    /// one, so re-reading the default later can name a different device than the one in
    /// use. Worth logging because a failure that turns out to be about the endpoint
    /// cannot be diagnosed from a log that never says which endpoint it was (#127).
    /// </remarks>
    public string DeviceName => _deviceName;

    /// <summary>
    /// Whether the device still considers itself connected, read live from
    /// <c>ALC_CONNECTED</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False once the endpoint has gone away underneath the open context — a Remote
    /// Desktop session disconnecting takes its Remote Audio endpoint with it, and an
    /// output device being disabled or unplugged does the same. OpenAL Soft keeps its
    /// mixer thread running over the dead device and marks queued buffers processed
    /// without playing them, which is indistinguishable from playback at every layer
    /// above unless something asks this question (#127).
    /// </para>
    /// <para>
    /// Reads as connected on an implementation without <c>ALC_EXT_disconnect</c>: the
    /// call leaves the buffer untouched, and the seeded default is the answer that keeps
    /// such a device working rather than reporting it permanently dead.
    /// </para>
    /// </remarks>
    public unsafe bool IsConnected
    {
        get
        {
            int connected = 1;
            _alc.GetContextProperty(_device, (GetContextInteger)AlcConnected, 1, &connected);
            return connected != 0;
        }
    }

    /// <summary>
    /// Acquires a lease on the shared device/context, creating it on first use.
    /// Returns <see langword="null"/> when no audio device can be opened, or once the process
    /// has begun to exit — the caller stays inert (mirrors the prior per-sink
    /// device-open-failure path).
    /// </summary>
    /// <param name="onRevoked">
    /// Called if the process exits while the lease is held: the holder releases its sources and
    /// buffers and disposes the lease, under its own lock. Called at most once, on the exit
    /// thread, with no context lock held.
    /// </param>
    public static SharedOpenAlContextLease? Acquire(Action? onRevoked = null)
    {
        lock (Gate)
        {
            var (next, action) = _lifetime.Acquire();
            switch (action)
            {
                case ContextAcquire.Refuse:
                    return null;

                case ContextAcquire.Open:
                    // Before the open, not after it: an exit that begins while the device is
                    // opening raises ProcessExit once, to the handlers it finds then. Registered
                    // first, the handler waits on the gate for this open and then closes it.
                    RegisterExitHookUnderGate();
                    var created = TryCreate();
                    if (created is null)
                        return null;
                    _live = created;
                    _deviceOpensTotal++;
                    _lifetime = next.Opened();
                    break;

                default:
                    _lifetime = next;
                    break;
            }

            var lease = new SharedOpenAlContextLease(_live!, onRevoked);
            _leases.Add(lease);
            return lease;
        }
    }

    /// <summary>
    /// Releases a lease. Called exactly once per lease by
    /// <see cref="SharedOpenAlContextLease.Dispose"/>; the last release tears
    /// down the device/context.
    /// </summary>
    internal static void Release(SharedOpenAlContext context, SharedOpenAlContextLease lease)
    {
        lock (Gate)
        {
            _leases.Remove(lease);

            // Defensive: the lease guarantees single-release, so a mismatch here
            // means the context was already torn down. No-op rather than throw.
            if (_live != context)
                return;

            var (next, close) = _lifetime.Release();
            _lifetime = next;
            if (!close)
                return;

            context.TearDown();
            _live = null;
        }
    }

    /// <summary>
    /// Closes the shared device at process exit, whatever the lease count (#633).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A process that ended with a lease outstanding still had the device and context open when
    /// the runtime shut down, and on Windows it failed fast with <c>0xC0000409</c> instead of
    /// exiting with its own code. A sink is released only by its disposal, so any exit that
    /// skipped one did this: an early return, an <see cref="Environment.Exit"/>, a window closed
    /// during startup.
    /// </para>
    /// <para>
    /// Three steps. Under <see cref="Gate"/>, refuse every later lease and take the outstanding
    /// ones. Then, with the gate released, ask each holder to let go: a sink stops and deletes its
    /// source and buffers and disposes its lease under its own lock, which is the lock order the
    /// rest of the class keeps (sink, then gate), and which no sink thread can be inside an
    /// <c>al*</c> call without. Last, under the gate again, close whatever is still open. A sink
    /// used afterwards finds no device, as when none could be opened.
    /// </para>
    /// </remarks>
    internal static void ReleaseForExit()
    {
        SharedOpenAlContextLease[] outstanding;
        lock (Gate)
        {
            _lifetime = _lifetime.BeginExit();
            outstanding = [.. _leases];
        }

        foreach (var lease in outstanding)
        {
            try
            {
                lease.Revoke();
            }
            catch
            {
                // On the way out, one holder failing must not keep the device open for the rest.
            }
        }

        lock (Gate)
        {
            var (next, close) = _lifetime.Abandon();
            _lifetime = next;
            if (close)
                _live?.TearDown();
            _live = null;
            _leases.Clear();
        }
    }

    private static void RegisterExitHookUnderGate()
    {
        if (_exitHookRegistered)
            return;
        _exitHookRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => ReleaseForExit();
    }

    private static unsafe SharedOpenAlContext? TryCreate()
    {
        var alc = ALContext.GetApi();
        var al = AL.GetApi();

        var device = alc.OpenDevice(string.Empty);
        if (device == null)
        {
            // No device — release the managed API wrappers and report failure.
            al.Dispose();
            alc.Dispose();
            return null;
        }

        var context = alc.CreateContext(device, null);
        // The one and only MakeContextCurrent in the process. It is never changed
        // again until TearDown, so no sink's al* calls can be retargeted.
        alc.MakeContextCurrent(context);
        return new SharedOpenAlContext(al, alc, device, context, ReadDeviceName(alc, device));
    }

    /// <summary>
    /// Names the endpoint a device handle is open on. Prefers the all-devices specifier,
    /// which distinguishes endpoints of the same kind; falls back to the basic specifier,
    /// and then to a placeholder, so a driver that answers neither costs a log line rather
    /// than an activation.
    /// </summary>
    private static unsafe string ReadDeviceName(ALContext alc, Device* device)
    {
        try
        {
            var name = alc.GetContextProperty(device, (GetContextString)AlcAllDevicesSpecifier);
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            name = alc.GetContextProperty(device, GetContextString.DeviceSpecifier);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch (Exception)
        {
            // A driver that faults on an enumeration query is not a reason to fail the
            // open; the name is diagnostic only.
        }

        return "(unnamed device)";
    }

    private unsafe void TearDown()
    {
        // Detach the process-global current context before destroying it.
        _alc.MakeContextCurrent(null);
        if (_context != null)
            _alc.DestroyContext(_context);
        if (_device != null)
            _alc.CloseDevice(_device);
        _al.Dispose();
        _alc.Dispose();
    }
}

/// <summary>
/// A single sink's reference on the shared <see cref="SharedOpenAlContext"/>.
/// Disposing it releases the reference; the last lease disposed tears down the
/// underlying device/context. Disposal is idempotent and thread-safe.
/// </summary>
internal sealed class SharedOpenAlContextLease : IOpenAlContextLease
{
    private SharedOpenAlContext? _context;
    private Action? _onRevoked;

    internal SharedOpenAlContextLease(SharedOpenAlContext context, Action? onRevoked)
    {
        _context = context;
        _onRevoked = onRevoked;
    }

    /// <summary>
    /// Asks the holder to give the device back because the process is exiting. Runs the
    /// holder's callback at most once; a lease taken with none is left to the forced close.
    /// </summary>
    internal void Revoke() => Interlocked.Exchange(ref _onRevoked, null)?.Invoke();

    /// <summary>The shared AL API. Throws if the lease has already been disposed.</summary>
    public IOpenAlApi Al =>
        (_context ?? throw new ObjectDisposedException(nameof(SharedOpenAlContextLease))).Al;

    /// <inheritdoc cref="SharedOpenAlContext.DeviceName"/>
    public string DeviceName =>
        (_context ?? throw new ObjectDisposedException(nameof(SharedOpenAlContextLease))).DeviceName;

    /// <inheritdoc cref="SharedOpenAlContext.IsConnected"/>
    public bool IsConnected =>
        (_context ?? throw new ObjectDisposedException(nameof(SharedOpenAlContextLease))).IsConnected;

    /// <inheritdoc/>
    public void Dispose()
    {
        // Single-release even under concurrent disposal: only the thread that
        // swaps the field to null calls Release.
        var context = Interlocked.Exchange(ref _context, null);
        if (context is not null)
            SharedOpenAlContext.Release(context, this);
    }
}
