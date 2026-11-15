using DayZPulse.Infrastructure;

namespace DayZPulse.Logging;

/// <summary>Мост между ILogger и выводом в консоль приложения (для режима трея/отладки).</summary>
public sealed class ConsoleLogProvider : ILoggerProvider
{
    private readonly Action<string> _sink;
    private readonly LogLevel _minimumLevel;

    public ConsoleLogProvider(Action<string> sink, LogLevel minimumLevel = LogLevel.Information)
    {
        _sink = sink;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(this, categoryName);

    public void Dispose()
    {
    }    private sealed class ConsoleLogger(ConsoleLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = $"{DateTimeOffset.Now:HH:mm:ss} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            provider._sink(message);
        }
    }
}
