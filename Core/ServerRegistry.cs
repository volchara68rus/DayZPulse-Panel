using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Tracker;

namespace DayZPulse.Core;

/// <summary>
/// Реестр серверов DayZ: создаёт рантаймы по профилям, следит за процессами
/// и снимает метрики. У каждого сервера своя папка и свой набор портов.
/// </summary>
public sealed class ServerRegistry : BackgroundService
{
    private readonly SettingsStore _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ServerRegistry> _logger;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, ServerRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScheduleRunner> _schedules = new(StringComparer.OrdinalIgnoreCase);

    public ServerRegistry(SettingsStore settings, ILoggerFactory loggerFactory, ILogger<ServerRegistry> logger)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public IReadOnlyList<ServerRuntime> All
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _runtimes.Values.ToArray();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public ServerRuntime? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        _lock.EnterReadLock();
        try
        {
            return _runtimes.GetValueOrDefault(id);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Сервер, которому принадлежит токен мода (для маршрутизации трекера).</summary>
    public ServerRuntime? ByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        _lock.EnterReadLock();
        try
        {
            return _runtimes.Values.FirstOrDefault(rt =>
                !string.IsNullOrEmpty(rt.Profile.Tracker.ApiToken) &&
                string.Equals(rt.Profile.Tracker.ApiToken, token, StringComparison.Ordinal));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Сервер, чей токен совпадает, либо единственный сервер (когда токен ещё не задан).</summary>
    public ServerRuntime? ResolveForToken(string token) => ByToken(token);

    /// <summary>Сервер по идентификатору или идентификатору трекера (SERVER_ID из мода).</summary>
    public ServerRuntime? ResolveByTrackerId(string? trackerId)
    {
        if (string.IsNullOrWhiteSpace(trackerId))
        {
            return null;
        }

        _lock.EnterReadLock();
        try
        {
            return _runtimes.Values.FirstOrDefault(rt =>
                       string.Equals(rt.Profile.Tracker.ServerId, trackerId, StringComparison.OrdinalIgnoreCase))
                   ?? _runtimes.GetValueOrDefault(trackerId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Синхронизирует рантаймы с профилями из настроек.</summary>
    public void Sync()
    {
        var profiles = _settings.Current.Servers;

        _lock.EnterWriteLock();
        try
        {
            // Убираем рантаймы удалённых серверов.
            foreach (var id in _runtimes.Keys.Where(id => profiles.All(p => p.Id != id)).ToArray())
            {
                _runtimes.Remove(id);
                _schedules.Remove(id);
            }

            foreach (var profile in profiles)
            {
                if (_runtimes.TryGetValue(profile.Id, out var runtime))
                {
                    runtime.UpdateProfile(profile);
                }
                else
                {
                    // Рантайм создаёт собственное расписание.
                    var created = new ServerRuntime(profile, _settings, _loggerFactory);
                    _runtimes[profile.Id] = created;
                    _schedules[profile.Id] = created.Schedule;
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public ScheduleRunner? Schedule(string id)
    {
        _lock.EnterReadLock();
        try
        {
            return _schedules.GetValueOrDefault(id);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<ScheduleRunner> AllSchedules
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _schedules.Values.ToArray();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public ServerRuntime Add(ServerProfile profile)
    {
        _lock.EnterWriteLock();
        try
        {
            var runtime = new ServerRuntime(profile, _settings, _loggerFactory);
            _runtimes[profile.Id] = runtime;
            return runtime;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Remove(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_runtimes.TryGetValue(id, out var runtime))
            {
                return false;
            }

            // Закрываем журнал сервера: иначе его файл в папке сервера
            // останется открытым и папку нельзя будет удалить.
            runtime.Supervisor.CloseLogFile();
            return _runtimes.Remove(id);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Sync();

        // Автозапуск серверов, помеченных соответствующей настройкой.
        foreach (var runtime in All)
        {
            try
            {
                await runtime.InitializeAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Server}] Ошибка инициализации.", runtime.Name);
            }
        }

        var resourceTimer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var scheduleTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        var resources = RunResourcesLoopAsync(resourceTimer, stoppingToken);
        var schedules = RunSchedulesLoopAsync(scheduleTimer, stoppingToken);

        await Task.WhenAll(resources, schedules);
    }

    private async Task RunResourcesLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
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

            foreach (var runtime in All)
            {
                runtime.Supervisor.SampleResources();
            }
        }
    }

    private async Task RunSchedulesLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
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

            foreach (var schedule in AllSchedules)
            {
                try
                {
                    await schedule.EvaluateAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Server}] Ошибка выполнения расписания.", schedule.ServerId);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in All)
        {
            try
            {
                await runtime.Supervisor.ShutdownAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Server}] Ошибка остановки.", runtime.Name);
            }
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>Сводка по всем серверам для главной страницы.</summary>
    public object Summary()
    {
        var items = All.Select(runtime =>
        {
            var status = runtime.Supervisor.Status;
            return new
            {
                id = runtime.Id,
                name = runtime.Name,
                color = runtime.Profile.Color,
                map = ResolveMapKey(runtime),
                mapLabel = MapLabel(ResolveMapKey(runtime)),
                enabled = runtime.Profile.Enabled,
                state = status.State.ToString(),
                stateText = ApiEndpointsStatusText(status.State),
                running = runtime.Supervisor.IsRunning,
                processId = status.ProcessId,
                port = runtime.ServerSettings.Port,
                players = runtime.Tracker.LivePlayers().Count,
                playersAlive = runtime.Tracker.LivePlayers().Count(p => p.Alive),
                trackerConnected = runtime.Tracker.IsConnected(runtime.TrackerSettings.StaleSeconds),
                cpuPercent = status.CpuPercent,
                workingSetBytes = status.WorkingSetBytes,
                privateBytes = status.PrivateBytes,
                threadCount = status.ThreadCount,
                startedAt = status.StartedAt,
                stoppedAt = status.StoppedAt,
                exitCode = status.ExitCode,
                lastError = status.LastError,
                lastMessage = status.LastMessage,
                autoRestartArmed = status.AutoRestartArmed,
                autoRestartsInWindow = status.AutoRestartsInWindow,
                uptimeSeconds = status.Uptime?.TotalSeconds,
                launchCommand = runtime.Supervisor.LastLaunchCommandLine,
                executablePath = SafeExecutable(runtime.ServerSettings),
                consoleLines = runtime.Console.Count,
                directory = ServerPaths.WorkingDirectory(runtime.ServerSettings),
                baseDirectory = ServerPaths.BaseDirectory(runtime.ServerSettings),
                installed = File.Exists(SafeExecutable(runtime.ServerSettings)),
                modInstalled = ModStatusService.Describe(runtime.Profile,
                    runtime.Tracker.LastIngest != DateTimeOffset.MinValue).Installed,
                mods = runtime.ServerSettings.Mods
            };
        }).ToArray();

        return new
        {
            servers = items,
            total = items.Length,
            running = items.Count(s => s.running),
            players = items.Sum(s => s.playersAlive)
        };
    }

    private static string ApiEndpointsStatusText(ServerState state) => state switch
    {
        ServerState.Stopped => "Остановлен",
        ServerState.Starting => "Запускается",
        ServerState.Running => "Работает",
        ServerState.Stopping => "Останавливается",
        ServerState.Restarting => "Перезапускается",
        ServerState.Crashed => "Аварийно завершился",
        ServerState.Failed => "Ошибка запуска",
        _ => state.ToString()
    };

    /// <summary>
    /// Карта сервера. Карта больше не выбирается при создании: её определяет
    /// миссия из serverDZ.cfg (или первая папка в mpmissions), а поле профиля —
    /// только запасной вариант.
    /// </summary>
    public static string ResolveMapKey(ServerRuntime runtime)
    {
        var mission = QuickConfig.MissionTemplateOr(runtime.ServerSettings);
        return mission.Length > 0 ? mission : runtime.Profile.Map;
    }

    /// <summary>Человеческое название карты для интерфейса.</summary>
    public static string MapLabel(string? map)
    {
        // Миссия вида «dayzOffline.chernarusplus» или «dayzOffline.new_world»:
        // отбрасываем префикс, чтобы не показывать его в интерфейсе.
        var value = (map ?? "").Trim();
        if (value.StartsWith("dayzoffline.", StringComparison.OrdinalIgnoreCase))
        {
            value = value["dayzoffline.".Length..];
        }

        return value.ToLowerInvariant() switch
        {
            "chernarusplus" or "chernarus" => "Chernarus+",
            "enoch" or "livonia" => "Livonia",
            "sakhal" or "sakhalexp" => "Sakhal",
            "namalsk" => "Namalsk",
            "takistan" or "takistanplus" => "Takistan",
            "nasdara" => "Nasdara",
            "deerisle" or "deerisleexp" => "Deer Isle",
            "banov" => "Banov",
            "esseker" => "Esseker",
            "chiemsee" => "Chiemsee",
            "valning" => "Valning",
            "rostow" => "Rostow",
            "pripyat" or "nhchernobyl" => "Pripyat",
            "namalskplus" => "Namalsk+",
            // Служебные миры-заготовки DayZ.
            "new_world" or "empty" or "test" => "Chernarus+",
            "" => "Chernarus+",
            // Неизвестная карта: отдаём имя как есть, но с заглавной буквы.
            _ => char.ToUpperInvariant(value[0]) + value[1..]
        };
    }

    /// <summary>Путь к исполняемому файлу сервера; не бросает исключений для интерфейса.</summary>
    private static string? SafeExecutable(ServerSettings options)
    {
        try
        {
            return ServerPaths.Executable(options);
        }
        catch
        {
            return options.ExecutablePath;
        }
    }
}
