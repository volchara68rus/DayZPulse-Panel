using System.Net.WebSockets;

namespace DayZPulse.Web;

/// <summary>
/// Закрывает все WebSocket-соединения при остановке приложения.
///
/// Иначе страница, открытая в браузере, держит канал открытым, и хост ждёт
/// завершения обработчиков до истечения ShutdownTimeout (по умолчанию 30 с).
/// После смены порта (создание владельца) это выглядело как «зависшая»
/// страница: панель не могла перезапуститься, пока висел её же сокет.
/// </summary>
public sealed class WebSocketShutdownService : IHostedService
{
    private readonly LiveHub _live;
    private readonly ConsoleHub _console;
    private readonly ILogger<WebSocketShutdownService> _logger;

    public WebSocketShutdownService(LiveHub live, ConsoleHub console, ILogger<WebSocketShutdownService> logger)
    {
        _live = live;
        _console = console;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _live.CloseAll(WebSocketCloseStatus.EndpointUnavailable, "панель перезапускается");
            _console.CloseAll(WebSocketCloseStatus.EndpointUnavailable, "панель перезапускается");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось закрыть WebSocket-соединения при остановке.");
        }

        return Task.CompletedTask;
    }
}
