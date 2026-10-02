// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Avalonia.Windows;

/// <summary>
/// Admits one holder at a time to the video processor path (#560). <see cref="Process"/> is the
/// lease every <see cref="CompositionInteropVideoView"/> in the process shares, so two views can
/// never issue concurrent <c>VideoProcessorBlt</c>s (ADR-0063).
/// </summary>
internal sealed class SuperResolutionLease
{
    private object? _holder;

    /// <summary>The lease shared by every view in the process.</summary>
    public static SuperResolutionLease Process { get; } = new();

    /// <summary>Whether <paramref name="owner"/> holds the lease, or could take it now.</summary>
    public bool IsAvailableTo(object owner)
    {
        var holder = Volatile.Read(ref _holder);
        return holder is null || ReferenceEquals(holder, owner);
    }

    /// <summary>Takes the lease for <paramref name="owner"/>, or confirms it already holds it.</summary>
    public bool TryAcquire(object owner)
    {
        var holder = Interlocked.CompareExchange(ref _holder, owner, null);
        return holder is null || ReferenceEquals(holder, owner);
    }

    /// <summary>Releases the lease if <paramref name="owner"/> holds it. Idempotent.</summary>
    public void Release(object owner) => Interlocked.CompareExchange(ref _holder, null, owner);
}
