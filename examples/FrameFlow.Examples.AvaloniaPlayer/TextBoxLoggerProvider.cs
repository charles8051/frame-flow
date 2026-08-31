using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace FrameFlow.Examples.AvaloniaPlayer;

/// <summary>
/// Logging provider that appends log messages to an Avalonia
/// <see cref="TextBox"/>. Messages are marshalled to the UI thread
/// via <see cref="Dispatcher.UIThread"/>.
/// </summary>
internal sealed class TextBoxLoggerProvider : ILoggerProvider
{
    private readonly TextBox _textBox;
    private readonly LogLevel _minLevel;

    public TextBoxLoggerProvider(TextBox textBox, LogLevel minLevel = LogLevel.Debug)
    {
        _textBox = textBox;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) =>
        new TextBoxLogger(_textBox, categoryName, _minLevel);

    public void Dispose() { }

    private sealed class TextBoxLogger(TextBox textBox, string category, LogLevel minLevel)
        : ILogger
    {
        private readonly TextBox _textBox = textBox;
        private readonly string _category = GetShortCategory(category);
        private readonly LogLevel _minLevel = minLevel;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

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

            var message = $"[{timestamp}] {level} [{_category}] {formatter(state, exception)}";
            if (exception is not null)
                message += $"\n  {exception.GetType().Name}: {exception.Message}";

            Dispatcher.UIThread.Post(
                () =>
                {
                    _textBox.Text = string.IsNullOrEmpty(_textBox.Text)
                        ? message
                        : _textBox.Text + "\n" + message;
                },
                DispatcherPriority.Background
            );
        }

        private static string GetShortCategory(string category)
        {
            // "FrameFlow.Audio.OpenAL.OpenAlAudioSink" → "OpenAlAudioSink"
            var lastDot = category.LastIndexOf('.');
            return lastDot >= 0 ? category[(lastDot + 1)..] : category;
        }
    }
}
