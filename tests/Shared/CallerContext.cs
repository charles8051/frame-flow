using Xunit;

namespace FrameFlow.Tests.Shared;

/// <summary>
/// Marks the work an async method does on its caller's behalf: what runs inside the call, on the
/// caller's thread, and what it posts back to the caller's <see cref="SynchronizationContext"/>
/// afterwards. A test that makes the call through <see cref="Call{T}"/> and checks
/// <see cref="ThrowIfOnTheCaller"/> in the work that blocks shows the method moved that work off
/// both, which is what an awaiting UI thread needs (#626).
/// </summary>
/// <remarks>
/// The context runs what is posted to it on a pool thread rather than on a loop of its own, so a
/// method that resumes on it fails the check instead of deadlocking the test.
/// </remarks>
internal static class CallerContext
{
    [ThreadStatic]
    private static bool t_onTheCaller;

    /// <summary>
    /// Calls <paramref name="call"/> on this thread under a synchronization context that stands for
    /// the caller's, and returns the task it returned.
    /// </summary>
    public static Task<T> Call<T>(Func<Task<T>> call)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new CallersContext());
        t_onTheCaller = true;
        try
        {
            return call();
        }
        finally
        {
            t_onTheCaller = false;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Throws when it runs inside a <see cref="Call{T}"/>, or in work posted to that call's
    /// context.
    /// </summary>
    public static void ThrowIfOnTheCaller(string what)
    {
        if (t_onTheCaller)
            throw new InvalidOperationException($"{what} ran on the caller's thread or its synchronization context.");
    }

    private sealed class CallersContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => RunAsTheCaller(d, state));

        public override void Send(SendOrPostCallback d, object? state) => RunAsTheCaller(d, state);

        public override SynchronizationContext CreateCopy() => this;

        private void RunAsTheCaller(SendOrPostCallback d, object? state)
        {
            var previousContext = Current;
            var previousFlag = t_onTheCaller;
            SetSynchronizationContext(this);
            t_onTheCaller = true;
            try
            {
                d(state);
            }
            finally
            {
                t_onTheCaller = previousFlag;
                SetSynchronizationContext(previousContext);
            }
        }
    }
}

/// <summary>
/// A gate that a session open waits on, and the signal that the open has reached it. The test
/// opens the gate only after it has seen the task still running while the open waits, so the
/// open is in progress, and cannot finish, when the test looks.
/// </summary>
internal sealed class OpenGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(0);
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called by the open: signals that it has reached the gate, then waits for it to open.</summary>
    public void Wait()
    {
        _reached.TrySetResult();
        _gate.Wait();
    }

    /// <summary>
    /// Waits until the open has reached the gate, or <paramref name="task"/> has finished without
    /// it, asserts that <paramref name="task"/> is still running, then opens the gate.
    /// </summary>
    public async Task AssertRunningWhileTheOpenWaitsAsync(Task task)
    {
        try
        {
            await Task.WhenAny(_reached.Task, task);
            Assert.False(
                task.IsCompleted,
                $"The call's task was {task.Status} before the gate opened: "
                    + task.Exception?.InnerException?.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
