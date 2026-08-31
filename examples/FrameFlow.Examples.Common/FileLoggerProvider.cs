using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.Common;

/// <summary>
/// Minimal file-backed logging provider shared by the FrameFlow examples.
/// Writes each log entry as a single line to the configured file, flushing
/// after every write so the log survives a crash mid-run. Pair with
/// <c>--log-file &lt;path&gt;</c>.
/// </summary>
/// <remarks>
/// Debugging aid for the examples only — production hosts should use a proper
/// logging provider (Serilog, NLog, the BCL's ConsoleLoggerProvider, etc.).
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
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
            $"# FrameFlow example log — opened {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}"
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
