using System.Collections.Concurrent;
using System.Text;
using DayZPulse.Infrastructure;

namespace DayZPulse.Logging;

/// <summary>Простой файловый журнал с суточной ротацией и ограничением по возрасту.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly int _retentionDays;
    private readonly LogLevel _minimumLevel;
    private bool _disposed;

    public FileLoggerProvider(string directory, int retentionDays = 14, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _retentionDays = retentionDays;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_directory);
        CleanupOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _disposed = true;

    private bool IsDisposed => _disposed;

    private void CleanupOldFiles()
    {
        try
        {
            var threshold = DateTime.Now.AddDays(-Math.Max(1, _retentionDays));
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Очистка не критична.
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (IsDisposed)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(" [")
            .Append(LevelText(level)).Append("] ").Append(category).Append(" — ").Append(message);

        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        var line = builder.ToString();
        var path = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");

        try
        {
            lock (_gate)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Журналирование не должно ломать приложение.
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
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

            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

/// <summary>Журнал в памяти: последние сообщения доступны в веб-интерфейсе.</summary>
public sealed class InMemoryLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly LogLevel _minimumLevel;
    private const int Capacity = 1000;

    public InMemoryLogProvider(LogLevel minimumLevel = LogLevel.Information)
    {
        _minimumLevel = minimumLevel;
    }

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new MemoryLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string line)
    {
        _entries.Enqueue(line);
        while (_entries.Count > Capacity)
        {
            _entries.TryDequeue(out _);
        }
    }

    private sealed class MemoryLogger(InMemoryLogProvider provider, string category) : ILogger
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

            provider.Add($"{DateTimeOffset.Now:HH:mm:ss} [{logLevel}] {category}: {formatter(state, exception)}");
        }
    }
}
