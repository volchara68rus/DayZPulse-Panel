using DayZPulse.Core;
using DayZPulse.Tracker;

namespace DayZPulse.Web;

/// <summary>
/// Публикует изменения в канал реального времени: состояние серверов,
/// прогресс SteamCMD и метрики. Благодаря этому панель обновляется сама,
/// без периодических запросов от браузера.
/// </summary>
public sealed class LivePublisher : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly LiveHub _hub;
    private readonly Steam.SteamCmdInstaller _steam;
    private readonly ILogger<LivePublisher> _logger;

    /// <summary>Снимок прошлого состояния, чтобы отправлять только изменения.</summary>
    private readonly Dictionary<string, ServerRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _lastSignature = new(StringComparer.OrdinalIgnoreCase);

    public LivePublisher(ServerRegistry registry, LiveHub hub,
        Steam.SteamCmdInstaller steam, ILogger<LivePublisher> logger)
    {
        _registry = registry;
        _hub = hub;
        _steam = steam;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AttachSupervisors();

        // Новый клиент сразу получает текущее состояние, а не ждёт изменений.
        _hub.Snapshot = BuildSnapshot;

        // Раз в секунду сравниваем состояние и рассылаем изменения.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

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

            try
            {
                PublishServers();
                PublishSteam();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ошибка отправки событий.");
            }
        }
    }

    /// <summary>Подписка на события супервизоров: смена состояния и строки консоли.</summary>
    private void AttachSupervisors()
    {
        foreach (var runtime in _registry.All)
        {
            if (_runtimes.ContainsKey(runtime.Id))
            {
                continue;
            }

            var id = runtime.Id;

            runtime.Supervisor.StateChanged += (_, e) =>
            {
                // Сервер остановлен — сбрасываем живое состояние трекера,
                // иначе «сейчас онлайн» и связь с модом оставались бы висеть.
                if (e.Status.State is ServerState.Stopped or ServerState.Crashed or ServerState.Failed)
                {
                    runtime.Tracker.ClearLive();
                }

                _hub.Broadcast("server-state", new { serverId = e.ServerId, status = Tracker.ServerDtos.Status(runtime) });
            };

            _runtimes[id] = runtime;
        }
    }

    /// <summary>Текущее состояние для клиента, только что подключившегося к каналу.</summary>
    private IEnumerable<LiveMessage> BuildSnapshot()
    {
        yield return new LiveMessage("servers", _registry.Summary());
        yield return new LiveMessage("steam", SteamPayload());
    }

    /// <summary>Отправляет сводку по серверам, если что-то изменилось.</summary>
    private void PublishServers()
    {
        AttachSupervisors();

        var summary = _registry.Summary();
        var json = Infrastructure.Json.Serialize(summary, indented: false);

        if (_lastSignature.GetValueOrDefault("servers") == json)
        {
            return;
        }

        _lastSignature["servers"] = json;
        _hub.Broadcast("servers", summary);
    }

    /// <summary>Отправляет прогресс SteamCMD, пока он меняется.</summary>
    private void PublishSteam()
    {
        var status = _steam.Status;

        var signature = $"{status.Running}|{status.Installed}|{status.Warm}|{status.ProgressPercent}|" +
                        $"{status.Phase}|{status.Stage}|{status.ServerInstalled}|{status.ServerDirectory}|" +
                        $"{status.BytesDone}|{status.BytesPerSecond}|{status.Error}";
        if (_lastSignature.GetValueOrDefault("steam") == signature)
        {
            return;
        }

        _lastSignature["steam"] = signature;
        _hub.Broadcast("steam", SteamPayload());
    }

    private object SteamPayload()
    {
        var status = _steam.Status;

        return new
        {
            running = status.Running,
            installed = status.Installed,
            warm = status.Warm,
            progress = status.ProgressPercent,
            phase = status.Phase,
            stage = status.Stage,
            directory = status.Directory,
            serverInstalled = status.ServerInstalled,
            serverDirectory = status.ServerDirectory,
            bytesDone = status.BytesDone,
            bytesTotal = status.BytesTotal,
            bytesPerSecond = status.BytesPerSecond,
            error = status.Error
        };
    }
}
