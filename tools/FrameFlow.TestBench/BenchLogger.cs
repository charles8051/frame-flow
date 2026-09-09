// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.TestBench;

/// <summary>
/// A logger factory that writes to the bench's own output, so library log lines
/// land in the transcript beside the commands that caused them.
/// </summary>
/// <remarks>
/// The bench runs on <see cref="Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory"/>
/// by default and should keep doing so: a decode loop at 60 fps produces enough
/// debug logging to change what is being measured. This exists for the runs that
/// want a specific library decision explained — the lateness-recovery walk, whose
/// every move is a log line and whose behaviour is the thing under test.
/// </remarks>
internal sealed class BenchLoggerFactory(TextWriter output, LogLevel minimum) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) =>
        new BenchLogger(output, minimum, categoryName);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class BenchLogger(TextWriter output, LogLevel minimum, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
                return;

            // Indented and marked, so a library line is never mistaken for the
            // bench's own report of a command.
            var name = category[(category.LastIndexOf('.') + 1)..];
            lock (output)
            {
                output.WriteLine($"  log       [{name}] {formatter(state, exception)}");
                if (exception is not null)
                    output.WriteLine($"  log       {exception}");
            }
        }
    }
}
