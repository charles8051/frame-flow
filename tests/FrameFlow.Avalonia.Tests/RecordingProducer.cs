namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// Stands in for one of the interop view's producers (a converter, a resize's replaced parts or the
/// CPU uploader), which need a GPU. Records its disposal, whether the ring's release had completed
/// by then, and the thread it ran on, and can throw from it.
/// </summary>
/// <param name="release">The release of the ring it filled: what must complete before it is disposed.</param>
/// <param name="throwOnDispose">Whether its dispose throws <see cref="DisposeFault"/>.</param>
internal sealed class RecordingProducer(Task? release = null, bool throwOnDispose = false) : IDisposable
{
    public const string DisposeFault = "The producer failed to release.";

    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether it has been disposed.</summary>
    public bool Disposed => _disposed.Task.IsCompleted;

    /// <summary>Completes once it has been disposed.</summary>
    public Task WhenDisposed => _disposed.Task;

    /// <summary>Whether the ring's release had completed when it was disposed.</summary>
    public bool DisposedAfterRelease { get; private set; }

    /// <summary>The managed thread its dispose ran on.</summary>
    public int DisposedOnThread { get; private set; }

    public void Dispose()
    {
        DisposedAfterRelease = release is null || release.IsCompleted;
        DisposedOnThread = Environment.CurrentManagedThreadId;
        _disposed.TrySetResult();
        if (throwOnDispose)
            throw new InvalidOperationException(DisposeFault);
    }
}
