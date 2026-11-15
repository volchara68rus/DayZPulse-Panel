using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using DayZPulse.Core;
using DayZPulse.Infrastructure;

namespace DayZPulse.Web;

/// <summary>
/// Транслирует строки консоли всем подключённым клиентам. Каждый клиент
/// подписывается на конкретный сервер через ?server=&lt;id&gt;.
/// </summary>
public sealed class ConsoleHub
{
    private sealed class Client(WebSocket socket, string serverId)
    {
        public WebSocket Socket { get; } = socket;
        public string ServerId { get; } = serverId;
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ServerRegistry _registry;
    private readonly ILogger<ConsoleHub> _logger;

    public ConsoleHub(ServerRegistry registry, ILogger<ConsoleHub> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public int ClientCount => _clients.Count;

    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Ожидалось WebSocket-соединение." });
            return;
        }

        var serverId = context.Request.Query["server"].ToString();
        var runtime = _registry.Get(serverId)
                      ?? _registry.All.FirstOrDefault();

        if (runtime is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "Сервер не найден." });
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _clients[id] = new Client(socket, runtime.Id);
        _logger.LogDebug("Клиент консоли подключён: {Id} ({Server})", id, runtime.Id);

        try
        {
            var backlog = runtime.Console.Snapshot(500);
            await SendAsync(socket, Json.Serialize(new
            {
                type = "backlog",
                server = runtime.Id,
                lines = backlog.Select(l => new { sequence = l.Sequence, timestamp = l.Timestamp, stream = l.Stream, text = l.Text })
            }, indented: false), context.RequestAborted);

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Клиент отключился.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket консоли закрыт с ошибкой.");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch
                {
                    // Игнорируем.
                }
            }
        }
    }

    /// <summary>Отправляет строку клиентам, подписанным на этот сервер.</summary>
    public void Broadcast(string serverId, ConsoleLine line)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var payload = Json.Serialize(new
        {
            type = "line",
            server = serverId,
            line = new { sequence = line.Sequence, timestamp = line.Timestamp, stream = line.Stream, text = line.Text }
        }, indented: false);

        foreach (var (id, client) in _clients)
        {
            if (!string.Equals(client.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (client.Socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            _ = SendAsync(client.Socket, payload, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _clients.TryRemove(id, out _);
                    }
                }, TaskScheduler.Default);
        }
    }

    private static async Task SendAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>Точка входа для маршрута /ws/console.</summary>
    public static async Task Endpoint(HttpContext context)
    {
        var hub = context.RequestServices.GetRequiredService<ConsoleHub>();
        await hub.AcceptAsync(context);
    }
}
