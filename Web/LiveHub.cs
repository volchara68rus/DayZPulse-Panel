using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using DayZPulse.Infrastructure;

namespace DayZPulse.Web;

/// <summary>
/// Канал событий панели: сервер сам присылает клиентам изменения состояния,
/// поэтому интерфейс работает в реальном времени без периодических запросов.
/// </summary>
public sealed class LiveHub
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly ILogger<LiveHub> _logger;

    public LiveHub(ILogger<LiveHub> logger)
    {
        _logger = logger;
    }

    public int ClientCount => _clients.Count;

    /// <summary>
    /// Снимок текущего состояния. Отправляется клиенту сразу при подключении,
    /// иначе интерфейс оставался бы пустым до первого изменения.
    /// </summary>
    public Func<IEnumerable<LiveMessage>>? Snapshot { get; set; }

    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Ожидалось WebSocket-соединение." });
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _clients[id] = socket;
        _logger.LogDebug("Клиент событий подключён: {Id}", id);

        // Сразу отдаём текущее состояние, чтобы панель не ждала изменений.
        await SendSnapshotAsync(socket, context.RequestAborted);

        try
        {
            var buffer = new byte[4096];

            // Держим соединение открытым: сервер только отправляет, но входящие
            // кадры нужно вычитывать, иначе соединение закроется по таймауту.
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
            // Браузер закрыл вкладку.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Канал событий закрыт с ошибкой.");
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    /// <summary>Отправляет новому клиенту текущее состояние панели.</summary>
    private async Task SendSnapshotAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot;
        if (snapshot is null)
        {
            return;
        }

        foreach (var message in snapshot())
        {
            try
            {
                var bytes = Encode(message);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketException)
            {
                return;
            }
        }
    }

    private static byte[] Encode(LiveMessage message) =>
        Encoding.UTF8.GetBytes(Json.Serialize(new { type = message.Type, data = message.Data }, indented: false));

    /// <summary>
    /// Закрывает все соединения (при остановке приложения): хост не должен
    /// ждать их до таймаута.
    /// </summary>
    public void CloseAll(WebSocketCloseStatus status, string reason)
    {
        foreach (var (id, socket) in _clients)
        {
            _clients.TryRemove(id, out _);
            Close(socket, status, reason);
        }
    }

    /// <summary>Безопасно закрывает сокет, не дожидаясь ответа клиента.</summary>
    internal static void Close(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // Abort: клиент мог уже уйти, ждать подтверждения закрытия нельзя.
                socket.Abort();
            }
        }
        catch
        {
            // Соединение могло закрыться само.
        }
    }

    /// <summary>Отправляет событие всем подключённым клиентам.</summary>
    public void Broadcast(string type, object payload)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var message = Json.Serialize(new { type, data = payload }, indented: false);
        var bytes = Encoding.UTF8.GetBytes(message);

        foreach (var (id, socket) in _clients)
        {
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            _ = SendAsync(socket, bytes, CancellationToken.None).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _clients.TryRemove(id, out _);
                }
            }, TaskScheduler.Default);
        }
    }

    private static async Task SendAsync(WebSocket socket, byte[] payload, CancellationToken cancellationToken) =>
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    /// <summary>Точка входа для маршрута /ws/events.</summary>
    public static async Task Endpoint(HttpContext context)
    {
        var hub = context.RequestServices.GetRequiredService<LiveHub>();
        await hub.AcceptAsync(context);
    }
}

/// <summary>Событие канала реального времени.</summary>
public sealed record LiveMessage(string Type, object Data);
