// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameFlow.Avalonia;

/// <summary>
/// A logger whose destination can be changed after it has been handed out. A video view builds
/// the sink it owns with one of these and keeps the handle, so a logger factory it is given after
/// the sink exists still reaches the sink (#630).
/// </summary>
/// <remarks>
/// <see cref="Target"/> is set on the UI thread and read wherever the holder logs, which for a sink
/// is the presenting thread. It is one volatile reference: a log call that straddles a change goes
/// to the old destination or the new one, never neither.
/// </remarks>
internal sealed class SwitchableLogger<T> : ILogger<T>
{
    private volatile ILogger _target = NullLogger.Instance;

    /// <summary>Where log calls go. Null means nowhere.</summary>
    public ILogger Target
    {
        get => _target;
        set => _target = value ?? NullLogger.Instance;
    }

    /// <summary>Sends log calls to <paramref name="loggerFactory"/>, under <typeparamref name="T"/>'s category.</summary>
    public void Use(ILoggerFactory loggerFactory) => Target = loggerFactory.CreateLogger<T>();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _target.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _target.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => _target.Log(logLevel, eventId, state, exception, formatter);
}
