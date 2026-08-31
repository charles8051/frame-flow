namespace FrameFlow.Examples.Multicast.Dml;

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> — invokes the callback inline on the
/// thread that calls <see cref="Report"/>, unlike <see cref="System.Progress{T}"/>
/// which posts to the captured <see cref="System.Threading.SynchronizationContext"/>.
/// </summary>
/// <remarks>
/// Used to surface inference load sub-phases on the <see cref="StartupClock"/>:
/// the load is awaited from the UI thread, so a <c>Progress&lt;T&gt;</c> callback
/// would queue behind the in-flight load and only run once it completes —
/// collapsing every phase mark to "after warmup". Reporting inline keeps each
/// mark at the instant its phase occurs. <see cref="StartupClock.Mark"/> is
/// thread-safe, so inline invocation off the load thread is fine.
/// </remarks>
internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
