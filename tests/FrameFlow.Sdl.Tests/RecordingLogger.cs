// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Sdl.Tests;

/// <summary>A logger that keeps every entry, from any thread.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => _entries.Enqueue(new LogEntry(logLevel, exception, formatter(state, exception)));
}

internal sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
