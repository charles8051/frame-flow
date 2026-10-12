using Microsoft.Extensions.Logging;

namespace FrameFlow.Avalonia.Tests;

/// <summary>
/// A logger factory that keeps every entry logged through any logger it made, so a test can ask
/// whether a component's diagnostics reached the factory it was given.
/// </summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    public sealed record Entry(string Category, LogLevel Level, string Message);

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_gate)
                return [.. _entries];
        }
    }

    /// <summary>The messages logged under <typeparamref name="T"/>'s category.</summary>
    public IReadOnlyList<string> MessagesFrom<T>() =>
        [.. Entries.Where(e => e.Category == typeof(T).FullName).Select(e => e.Message)];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose() { }

    private void Add(Entry entry)
    {
        lock (_gate)
            _entries.Add(entry);
    }

    private sealed class Logger(RecordingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => owner.Add(new Entry(category, logLevel, formatter(state, exception)));
    }
}
