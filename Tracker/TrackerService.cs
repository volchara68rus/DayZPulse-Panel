using DayZPulse.Core;
using DayZPulse.Infrastructure;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>
/// Обслуживает обмен с модом DayZPulse для всех серверов: принимает данные,
/// ведёт очереди команд, читает админ-логи и периодически сохраняет состояние.
/// </summary>
public sealed class TrackerService : BackgroundService
{
    private readonly SettingsStore _settings;
    private readonly ServerRegistry _registry;
    private readonly ILogger<TrackerService> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastConnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastSave = new(StringComparer.OrdinalIgnoreCase);

    public TrackerService(SettingsStore settings, ServerRegistry registry, ModManager mods,
        ILogger<TrackerService> logger)
    {
        _settings = settings;
        _registry = registry;
        _mods = mods;
        _logger = logger;
    }

    private readonly ModManager _mods;

    public SettingsStore Settings => _settings;
    public ServerRegistry Registry => _registry;

    /// <summary>
    /// Читает maxPlayers из serverDZ.cfg сервера (число слотов).
    /// Возвращает null, если файл или параметр не найдены.
    /// </summary>
    public static int? ReadMaxPlayers(ServerSettings server)
    {
        try
        {
            var directory = ServerPaths.WorkingDirectory(server);
            var file = Path.Combine(directory, server.ConfigFile.Length > 0 ? server.ConfigFile : "serverDZ.cfg");

            if (!File.Exists(file))
            {
                return null;
            }

            // Ищем строку вида: maxPlayers = 60;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var match = System.Text.RegularExpressions.Regex.Match(
                    line, @"^\s*maxPlayers\s*=\s*(\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success && int.TryParse(match.Groups[1].Value, out var value) && value > 0)
                {
                    return value;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Находит сервер по токену из заголовка X-Api-Token.</summary>
    public ServerRuntime? Authorize(HttpContext context)
    {
        var token = context.Request.Headers["X-Api-Token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            token = context.Request.Query["token"].ToString();
        }

        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return _registry.ByToken(token);
    }

    /// <summary>Читает тело запроса как JSON.</summary>
    public async Task<System.Text.Json.JsonElement?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                context.Request.Body, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Мод прислал некорректный JSON.");
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Токен нужен моду, поэтому создаём его сразу при первом запуске.
        EnsureTokens();
        WriteModConfigs();

        LoadStates();

        // Восстанавливаем демо-игрока, если он был включён.
        foreach (var runtime in _registry.All)
        {
            if (runtime.Profile.Tracker.DemoPlayer)
            {
                runtime.Tracker.SetDemoPlayer(true, DateTimeOffset.Now);
            }

            // Пересобираем -mod= / -serverMod= из списка модов и наличия @DayZPulse.
            try { _mods.RebuildLaunchMods(runtime.Id); } catch { /* не критично */ }
        }

        // Рантаймы перечитывают профили (в них обновились -mod= и -serverMod=).
        _registry.Sync();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        // Файловый режим: мод пишет снимок позиций в файл, панель читает его раз
        // в секунду. Так POST из игры (он синхронный и бил по FPS) не нужен для
        // позиций — по HTTP остаются только команды.
        var reader = new ServerSnapshotFileReader(_logger);
        _ = Task.Run(() => SnapshotFileLoopAsync(reader, stoppingToken), stoppingToken);

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
                Tick();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обслуживания трекера.");
            }
        }

        SaveAll();
    }

    /// <summary>
    /// Читает снимки позиций из файлов, которые пишет мод (файловый режим).
    /// Раз в секунду проверяет файл каждого запущенного сервера и, если он
    /// обновился, применяет снимок — как если бы он пришёл по HTTP.
    /// </summary>
    private async Task SnapshotFileLoopAsync(ServerSnapshotFileReader reader, CancellationToken stoppingToken)
    {
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

            foreach (var runtime in _registry.All)
            {
                try
                {
                    reader.TryRead(runtime, this);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{Server}] Не удалось прочитать файл снимка.", runtime.Name);
                }
            }
        }
    }

    /// <summary>Применяет снимок из файла (используется файловым режимом мода).</summary>
    public void ApplyFileSnapshot(ServerRuntime runtime, TrackedServerState server, List<LivePlayer> players)
    {
        runtime.Tracker.ApplySnapshot(server, players, DateTimeOffset.Now);
        MarkConnected(runtime, players.Count);
    }

    private void Tick()
    {
        foreach (var runtime in _registry.All)
        {
            var options = runtime.Profile.Tracker;

            runtime.Commands.Expire();

            if (options.ReadAdminLogs)
            {
                runtime.AdminLogs.Poll();
            }

            var connected = runtime.Tracker.IsConnected(options.StaleSeconds);
            var wasConnected = _lastConnected.GetValueOrDefault(runtime.Id, DateTimeOffset.MinValue) != DateTimeOffset.MinValue;

            if (wasConnected && !connected)
            {
                _lastConnected[runtime.Id] = DateTimeOffset.MinValue;
                _logger.LogWarning("[{Server}] Связь с модом DayZPulse потеряна.", runtime.Name);
            }

            // Сервер не работает, а живой снимок ещё висит — обнуляем.
            // Но если мод только что прислал данные (файловый режим, сервер мог
            // управляться извне), даём снимку устареть по staleSeconds.
            if (!runtime.Supervisor.IsRunning && runtime.Tracker.HasLive && !connected)
            {
                runtime.Tracker.ClearLive();
            }

            if (runtime.Tracker.ConsumeDirty())
            {
                SaveState(runtime);
            }
        }
    }

    /// <summary>Отмечает, что мод впервые вышел на связь (для журнала).</summary>
    public void MarkConnected(ServerRuntime runtime, int playerCount)
    {
        if (_lastConnected.GetValueOrDefault(runtime.Id, DateTimeOffset.MinValue) == DateTimeOffset.MinValue)
        {
            _lastConnected[runtime.Id] = DateTimeOffset.Now;
            _logger.LogInformation("[{Server}] Мод DayZPulse подключился: {Players} игроков онлайн.",
                runtime.Name, playerCount);
        }
    }

    // ------------------------------------------------------------- хранение

    /// <summary>Путь к базе данных трекера этого сервера (SQLite).</summary>
    private string DbPath(string serverId)
    {
        var runtime = _registry.Get(serverId);
        return runtime is null
            ? System.IO.Path.Combine(AppPaths.DataRoot, $"tracker-{serverId}.db")
            : ServerPaths.StateFile(runtime.ServerSettings, "tracker.db");
    }

    /// <summary>Старый JSON-файл состояния (до перехода на базу данных).</summary>
    private string LegacyJsonPath(string serverId)
    {
        var runtime = _registry.Get(serverId);
        return runtime is null
            ? System.IO.Path.Combine(AppPaths.DataRoot, $"tracker-{serverId}.json")
            : ServerPaths.StateFile(runtime.ServerSettings, "tracker.json");
    }

    private readonly Dictionary<string, TrackerDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>База данных трекера сервера (создаётся при первом обращении).</summary>
    public TrackerDatabase Database(ServerRuntime runtime)
    {
        lock (_databases)
        {
            if (!_databases.TryGetValue(runtime.Id, out var database))
            {
                database = new TrackerDatabase(DbPath(runtime.Id));
                _databases[runtime.Id] = database;
            }

            return database;
        }
    }

    public void SaveState(ServerRuntime runtime)
    {
        try
        {
            var state = runtime.Tracker.Export(runtime.Profile.Tracker.RetentionDays);
            Database(runtime).Sync(state, runtime.Profile.Tracker.RetentionDays);
            _lastSave[runtime.Id] = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось сохранить состояние трекера.", runtime.Name);
        }
    }

    private void LoadStates()
    {
        foreach (var runtime in _registry.All)
        {
            try
            {
                var database = Database(runtime);
                var retention = runtime.Profile.Tracker.RetentionDays;

                // Первый запуск после обновления: переносим данные из старого
                // JSON-файла в базу и оставляем файл как резервную копию.
                // Признак «перенос уже был» — сам файл базы, а не его наличие
                // с данными: даже пустая база считается созданной.
                var jsonPath = LegacyJsonPath(runtime.Id);
                if (!File.Exists(database.Path) && File.Exists(jsonPath))
                {
                    var legacy = Json.Deserialize<PersistedState>(File.ReadAllText(jsonPath));
                    if (legacy is not null)
                    {
                        database.Replace(legacy, retention);
                        _logger.LogInformation(
                            "[{Server}] Данные трекера перенесены из tracker.json в базу данных ({Players} игроков).",
                            runtime.Name, legacy.Players.Length);
                    }
                }

                var state = database.Load();
                if (state is not null)
                {
                    runtime.Tracker.Import(state);
                    _logger.LogInformation("[{Server}] Состояние трекера загружено из базы: {Players} игроков.",
                        runtime.Name, state.Players.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Server}] Не удалось загрузить состояние трекера.", runtime.Name);
            }
        }
    }

    private void SaveAll()
    {
        foreach (var runtime in _registry.All)
        {
            SaveState(runtime);
        }
    }

    /// <summary>Создаёт токены для серверов, у которых их ещё нет.</summary>
    public void EnsureTokens()
    {
        var missing = _registry.All
            .Where(rt => rt.Profile.Tracker.Enabled && string.IsNullOrEmpty(rt.Profile.Tracker.ApiToken))
            .Select(rt => rt.Id)
            .ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        _settings.Update(settings =>
        {
            foreach (var id in missing)
            {
                var profile = settings.Servers.FirstOrDefault(p => p.Id == id);
                if (profile is not null)
                {
                    profile.Tracker.ApiToken = NewToken();
                }
            }
        }, notify: false);

        _registry.Sync();

        foreach (var id in missing)
        {
            _logger.LogInformation("[{Server}] Создан токен трекера для мода DayZPulse.", id);
        }
    }

    public static string NewToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Пишет config.json для мода всем серверам с включённой автонастройкой.</summary>
    public void WriteModConfigs()
    {
        foreach (var runtime in _registry.All)
        {
            WriteModConfig(runtime);
        }
    }

    public void WriteModConfig(ServerRuntime runtime)
    {
        var options = runtime.Profile.Tracker;
        if (!options.Enabled || !options.AutoConfigureMod)
        {
            return;
        }

        if (ModConfigWriter.TryWrite(_settings, runtime.Profile, out var error))
        {
            var info = ModConfigWriter.Describe(runtime.Profile);
            _logger.LogInformation("[{Server}] Конфигурация мода записана: {Path}", runtime.Name, info.Path);
        }
        else if (error is not null)
        {
            _logger.LogWarning("[{Server}] Не удалось записать конфигурацию мода: {Error}", runtime.Name, error);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        SaveAll();
        return base.StopAsync(cancellationToken);
    }
}
