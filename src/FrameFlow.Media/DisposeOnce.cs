// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace FrameFlow.Media;

/// <summary>
/// Runs an owner's teardown once, and has every dispose wait for it (ADR-0086, decision 3.1). The
/// first call runs the teardown. Every other call, made while it runs or after, waits for it and
/// ends as it ended: complete, or throwing the same exception.
/// </summary>
/// <remarks>
/// <para>
/// An owner keeps one and forwards its <c>DisposeAsync</c> to <see cref="RunAsync"/>. Where its
/// members read a disposed flag, they read <see cref="HasBegun"/>, which is set as the first call
/// begins, before the teardown has done anything.
/// </para>
/// <para>
/// The teardown runs on the first caller's thread up to its first wait, as a dispose's body would.
/// A caller that cannot wait for it bounds its own await, for instance with
/// <c>DisposeAsync().AsTask().WaitAsync(timeout)</c>. The teardown goes on regardless, and frees
/// nothing early (decision 3.3).
/// </para>
/// </remarks>
internal sealed class DisposeOnce
{
    // Completed by the first call once its teardown has ended, as the teardown ended.
    private readonly TaskCompletionSource _tornDown = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _begun;

    /// <summary>Whether the first call has begun the teardown. Safe to read on any thread.</summary>
    public bool HasBegun => Volatile.Read(ref _begun) != 0;

    /// <summary>
    /// Runs <paramref name="teardown"/> if this is the first call, and completes once it has ended,
    /// as it ended.
    /// </summary>
    /// <param name="teardown">
    /// The owner's teardown. Only the first call's is run; the others are not called.
    /// </param>
    /// <exception cref="Exception">Whatever the first call's teardown threw, the same instance on every call.</exception>
    public async ValueTask RunAsync(Func<ValueTask> teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);

        if (Interlocked.Exchange(ref _begun, 1) == 0)
        {
            try
            {
                await teardown().ConfigureAwait(false);
                _tornDown.TrySetResult();
            }
            catch (Exception ex)
            {
                _tornDown.TrySetException(ex);
            }
        }

        await _tornDown.Task.ConfigureAwait(false);
    }
}
