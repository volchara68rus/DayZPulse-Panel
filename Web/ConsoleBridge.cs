using DayZPulse.Core;

namespace DayZPulse.Web;

/// <summary>Связывает вывод процессов серверов и RCON с WebSocket-клиентами консоли.</summary>
public sealed class ConsoleBridge : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly ConsoleHub _hub;
    private readonly RconService _rcon;
    private readonly ILogger<ConsoleBridge> _logger;
    private readonly Dictionary<string, EventHandler<ServerOutputEventArgs>> _outputHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EventHandler<ServerStateChangedEventArgs>> _stateHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<RconLine>> _rconHandlers = new(StringComparer.OrdinalIgnoreCase);

    public ConsoleBridge(ServerRegistry registry, ConsoleHub hub, RconService rcon, ILogger<ConsoleBridge> logger)
    {
        _registry = registry;
        _hub = hub;
        _rcon = rcon;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Attach();

        // Список серверов может меняться: обновляем подписки.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Attach();
        }
    }

    private void Attach()
    {
        foreach (var runtime in _registry.All)
        {
            if (_outputHandlers.ContainsKey(runtime.Id))
            {
                continue;
            }

            var id = runtime.Id;

            EventHandler<ServerOutputEventArgs> output = (_, e) =>
            {
                try
                {
                    _hub.Broadcast(e.ServerId, e.Line);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Не удалось разослать строку консоли.");
                }
            };

            EventHandler<ServerStateChangedEventArgs> state = (_, e) =>
            {
                _hub.Broadcast(e.ServerId, new ConsoleLine(0, DateTimeOffset.Now, "state",
                    $"Состояние сервера: {Tracker.ServerDtos.StatusText(e.Status.State)}"));
            };

            // Живая консоль сервера из RCON (пакет SERVERMESSAGE).
            var session = _rcon.Session(id);
            Action<RconLine> rcon = line =>
            {
                try
                {
                    _hub.Broadcast(id, new ConsoleLine(0, line.At, "rcon", line.Text));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Не удалось разослать строку RCON.");
                }
            };

            session.Line += rcon;

            runtime.Supervisor.Output += output;
            runtime.Supervisor.StateChanged += state;

            _outputHandlers[id] = output;
            _stateHandlers[id] = state;
            _rconHandlers[id] = rcon;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in _registry.All)
        {
            if (_outputHandlers.TryGetValue(runtime.Id, out var output))
            {
                runtime.Supervisor.Output -= output;
            }

            if (_stateHandlers.TryGetValue(runtime.Id, out var state))
            {
                runtime.Supervisor.StateChanged -= state;
            }

            if (_rconHandlers.TryGetValue(runtime.Id, out var rcon))
            {
                _rcon.Session(runtime.Id).Line -= rcon;
            }
        }

        _outputHandlers.Clear();
        _stateHandlers.Clear();
        _rconHandlers.Clear();

        return base.StopAsync(cancellationToken);
    }
}
