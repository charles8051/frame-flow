// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Audio.OpenAL;

/// <summary>What <see cref="SharedOpenAlContext.Acquire"/> does with a request for a lease.</summary>
internal enum ContextAcquire
{
    /// <summary>The process is exiting. No lease; the sink stays inert, as with no device.</summary>
    Refuse,

    /// <summary>Nothing is open. Open the device, and lease it if that worked.</summary>
    Open,

    /// <summary>The device is open. Lease it again.</summary>
    Share,
}

/// <summary>
/// The pure core of <see cref="SharedOpenAlContext"/>'s lifetime: whether the shared device is
/// open, how many leases hold it, and whether the process has begun to exit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Leasing</b> is ADR-0058's: the first lease opens the device, each later one shares it, and
/// the last release closes it.
/// </para>
/// <para>
/// <b>Exit</b> is two steps, because the sinks have to let go under their own locks and the
/// shell must not take a sink's lock while it holds the context's (#633). <see cref="BeginExit"/>
/// refuses every later lease, so nothing reopens the device behind the shutdown. The shell then
/// asks each sink to release its device resources, and their releases count down as usual.
/// <see cref="Abandon"/> then closes the device whatever is still leased, so a lease that was
/// never given back cannot keep it open into the runtime's shutdown, which is what made such a
/// process fail with <c>0xC0000409</c>.
/// </para>
/// </remarks>
/// <param name="Open">Whether the shared device and context are open.</param>
/// <param name="Leases">Leases outstanding on the open device. Zero when it is closed.</param>
/// <param name="Exiting">Whether the process has begun to exit. Never cleared.</param>
internal readonly record struct OpenAlContextLifetime(bool Open, int Leases, bool Exiting)
{
    /// <summary>No device open, no lease, not exiting.</summary>
    public static OpenAlContextLifetime Initial => default;

    /// <summary>Decides a request for a lease. <see cref="ContextAcquire.Open"/> changes nothing until <see cref="Opened"/>.</summary>
    public (OpenAlContextLifetime Next, ContextAcquire Action) Acquire()
    {
        if (Exiting)
            return (this, ContextAcquire.Refuse);
        if (Open)
            return (this with { Leases = Leases + 1 }, ContextAcquire.Share);
        return (this, ContextAcquire.Open);
    }

    /// <summary>The device opened for the request <see cref="Acquire"/> answered with <see cref="ContextAcquire.Open"/>. That request holds the one lease.</summary>
    public OpenAlContextLifetime Opened() => this with { Open = true, Leases = 1 };

    /// <summary>A lease was given back. The last one closes the device.</summary>
    public (OpenAlContextLifetime Next, bool Close) Release()
    {
        if (!Open)
            return (this, false);
        if (Leases > 1)
            return (this with { Leases = Leases - 1 }, false);
        return (this with { Open = false, Leases = 0 }, true);
    }

    /// <summary>The process has begun to exit: refuse every lease from here on.</summary>
    public OpenAlContextLifetime BeginExit() => this with { Exiting = true };

    /// <summary>Closes the device whatever is still leased. Nothing to close if it already is.</summary>
    public (OpenAlContextLifetime Next, bool Close) Abandon() =>
        Open ? (this with { Open = false, Leases = 0 }, true) : (this, false);
}
