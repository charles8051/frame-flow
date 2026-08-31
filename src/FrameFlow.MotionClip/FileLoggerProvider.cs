// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;

namespace FrameFlow.MotionClip;

/// <summary>
/// Minimal file-backed logging provider for the <c>--log-file</c> sink. Writes
/// each entry as a single line, flushing after every write so the log survives a
/// crash mid-run. MotionClip is a standalone tool (not an example), so it owns
/// this rather than sharing the examples' support project.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string path, LogLevel minLevel = LogLevel.Debug)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
        )
        {
            AutoFlush = true,
        };
        _minLevel = minLevel;
        _writer.WriteLine(
            $"# MotionClip log — opened {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}"
        );
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private void Write(string line)
    {
        lock (_gate)
            _writer.WriteLine(line);
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        private readonly FileLoggerProvider _owner = owner;
        private readonly string _category = GetShortCategory(category);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _owner._minLevel;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

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

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???",
            };

            _owner.Write($"[{timestamp}] {level} [{_category}] {formatter(state, exception)}");

            if (exception is not null)
                _owner.Write(
                    $"  {exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}"
                );
        }

        private static string GetShortCategory(string category)
        {
            var lastDot = category.LastIndexOf('.');
            return lastDot >= 0 ? category[(lastDot + 1)..] : category;
        }
    }
}
