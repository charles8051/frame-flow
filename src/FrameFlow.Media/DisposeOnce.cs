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
/// <b>Two forms.</b> An owner whose teardown is an <see langword="async"/> method calls it
/// directly, so the analyzers that check a dispose see what it disposes:
/// <code>
/// public ValueTask DisposeAsync() =&gt;
///     _dispose.TryBegin() ? _dispose.EndAsync(TearDownAsync()) : _dispose.WaitAsync();
/// </code>
/// That form needs an <see langword="async"/> teardown. One that throws before it returns its
/// task would skip <see cref="EndAsync"/>, and every later call would wait for good. An owner
/// whose teardown is synchronous, or may throw before its first wait, uses
/// <see cref="RunAsync"/>, which catches that throw too.
/// </para>
/// <para>
/// Where an owner's members read a disposed flag, they read <see cref="HasBegun"/>, which is set
/// as the first call begins, before the teardown has done anything.
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
    // Completed once the first call's teardown has ended, as the teardown ended.
    private readonly TaskCompletionSource _tornDown = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _begun;

    /// <summary>Whether the first call has begun the teardown. Safe to read on any thread.</summary>
    public bool HasBegun => Volatile.Read(ref _begun) != 0;

    /// <summary>
    /// Begins the teardown if no call has. <see langword="true"/> for the first call only, which
    /// must then hand its teardown to <see cref="EndAsync"/>; every other call awaits
    /// <see cref="WaitAsync"/>.
    /// </summary>
    public bool TryBegin() => Interlocked.Exchange(ref _begun, 1) == 0;

    /// <summary>
    /// Waits for the teardown the first call began, and ends as it ended, or as it ends.
    /// </summary>
    /// <exception cref="Exception">What the teardown threw, the same instance on every call.</exception>
    public ValueTask WaitAsync() => new(_tornDown.Task);

    /// <summary>
    /// Waits for <paramref name="teardown"/>, which the call that <see cref="TryBegin"/> let begin
    /// started, records how it ended for every other call, and ends as it did.
    /// </summary>
    /// <exception cref="InvalidOperationException">No call has begun the teardown.</exception>
    /// <exception cref="Exception">What <paramref name="teardown"/> threw.</exception>
    public async ValueTask EndAsync(ValueTask teardown)
    {
        if (!HasBegun)
            throw new InvalidOperationException("EndAsync takes the teardown of the call TryBegin let begin.");

        try
        {
            await teardown.ConfigureAwait(false);
            _tornDown.TrySetResult();
        }
        catch (Exception ex)
        {
            _tornDown.TrySetException(ex);
        }

        await _tornDown.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="teardown"/> if this is the first call, and completes once it has ended,
    /// as it ended. A teardown that throws before it returns its task ends that way too.
    /// </summary>
    /// <param name="teardown">
    /// The owner's teardown. Only the first call's is run; the others are not called.
    /// </param>
    /// <exception cref="Exception">Whatever the first call's teardown threw, the same instance on every call.</exception>
    public ValueTask RunAsync(Func<ValueTask> teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        return TryBegin() ? EndAsync(Start(teardown)) : WaitAsync();
    }

    private static ValueTask Start(Func<ValueTask> teardown)
    {
        try
        {
            return teardown();
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }
}
