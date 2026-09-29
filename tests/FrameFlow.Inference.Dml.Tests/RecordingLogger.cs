using Microsoft.Extensions.Logging;

namespace FrameFlow.Inference.Dml.Tests;

/// <summary>Keeps every entry a session logs, at every level.</summary>
internal sealed class RecordingLogger : ILogger<DmlInferenceSession>
{
    public List<(LogLevel Level, string? Event, string Message)> Entries { get; } = [];

    /// <summary>The messages logged under the event named <paramref name="eventName"/>.</summary>
    public IReadOnlyList<string> MessagesOf(string eventName) =>
        [.. Entries.Where(e => e.Event == eventName).Select(e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, eventId.Name, formatter(state, exception)));
}
