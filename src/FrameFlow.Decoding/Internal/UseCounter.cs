// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Diagnostics;

namespace FrameFlow.Decoding.Internal;

/// <summary>
/// A Debug-build count of the uses of a passive resource's native state that are in flight, which
/// its dispose checks is zero (ADR-0086, decision 3.5).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DemuxSession"/>, <see cref="AudioDecoder"/> and <see cref="DecodingPipeline"/> take
/// no lock around their native calls. Their rule is that the owner ends every use before it
/// disposes them: a pump, a decode, a seek, a flush or a reset is over before the free that would
/// pull its state from under it. This counts the uses so a dispose that breaks the rule says so.
/// </para>
/// <para>
/// In a Debug build the dispose throws <see cref="InvalidOperationException"/> and frees nothing,
/// so a test that reaches it fails by name rather than crashing in native code, and a later
/// dispose, once the uses have ended, frees as usual. <c>Debug.Fail</c> would end the test host
/// instead. In a Release build every member is compiled away: the calls are
/// <see cref="ConditionalAttribute"/> and the struct holds nothing.
/// </para>
/// </remarks>
internal struct UseCounter
{
#if DEBUG
    private int _inFlight;
#endif

    /// <summary>A use of the native state begins.</summary>
    [Conditional("DEBUG")]
    public void Enter()
    {
#if DEBUG
        Interlocked.Increment(ref _inFlight);
#endif
    }

    /// <summary>A use of the native state has ended.</summary>
    [Conditional("DEBUG")]
    public void Exit()
    {
#if DEBUG
        Interlocked.Decrement(ref _inFlight);
#endif
    }

    /// <summary>Throws when a use is still in flight as <paramref name="owner"/> is disposed.</summary>
    /// <exception cref="InvalidOperationException">A use has not ended.</exception>
    [Conditional("DEBUG")]
    public void ThrowIfInUse(object owner)
    {
#if DEBUG
        int inFlight = Volatile.Read(ref _inFlight);
        if (inFlight != 0)
        {
            throw new InvalidOperationException(
                $"{owner.GetType().Name} was disposed with {inFlight} use(s) of its native state "
                    + "still in flight. Its owner must end every use (a pump, a decode, a seek, a "
                    + "flush or a reset) before disposing it (ADR-0086, decision 3.5)."
            );
        }
#endif
    }
}
