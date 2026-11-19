using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Tracker;

namespace DayZPulse.Core;

/// <summary>Строка консоли сервера, полученная через RCON (пакет SERVERMESSAGE).</summary>
public sealed record RconLine(DateTimeOffset At, string Text);

/// <summary>Снимок игрока из RCON-команды players (пинг, IP, номер в BattlEye).</summary>
public sealed record RconPlayerSnapshot(int Number, string Name, string Ip, int Port, int Ping, string Guid);

/// <summary>
/// Управляет одним RCON-подключением к серверу: вход, команды, живая консоль,
/// список игроков. Хранит последние строки и состояние связи.
/// </summary>
public sealed class RconSession : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Queue<RconLine> _lines = new();

    private RconClient? _client;
    private string _password = "";
    private int _port;
    private string _host = "127.0.0.1";

    private const int MaxLines = 2000;

    public RconSession(string serverId, ILogger logger)
    {
        ServerId = serverId;
        _logger = logger;
    }

    public string ServerId { get; }

    public bool IsConnected => _client?.IsConnected == true;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastMessageAt => _client?.LastMessageAt;

    /// <summary>Новая строка консоли (для WebSocket-рассылки).</summary>
    public event Action<RconLine>? Line;

    /// <summary>Строки консоли, накопленные с момента подключения.</summary>
    public IReadOnlyList<RconLine> Snapshot(int max)
    {
        lock (_gate)
        {
            return _lines.TakeLast(Math.Clamp(max, 1, MaxLines)).ToArray();
        }
    }

    /// <summary>Подключается к RCON. Если уже подключено — ничего не делает.</summary>
    public async Task<bool> ConnectAsync(string host, int port, string password, CancellationToken ct)
    {
        if (_client?.IsConnected == true && _port == port && _password == password)
        {
            return true;
        }

        await DisconnectAsync();

        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        _port = port;
        _password = password ?? "";

        var client = new RconClient(_host, _port, _logger);
        client.ServerMessage += text =>
        {
            var line = new RconLine(DateTimeOffset.Now, text);
            Append(line);
        };

        var ok = await client.LoginAsync(_password, TimeSpan.FromSeconds(3), ct);

        _client = client;
        LastError = client.LastError;

        if (ok)
        {
            Append(new RconLine(DateTimeOffset.Now,
                $"Подключено к RCON {_host}:{_port}"));
        }

        return ok;
    }

    public async Task DisconnectAsync()
    {
        var client = _client;
        _client = null;

        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    /// <summary>Отправляет команду серверу через RCON.</summary>
    public async Task<string> SendAsync(string command, CancellationToken ct, bool echo = true)
    {
        var client = _client;
        if (client is null || !client.IsConnected)
        {
            throw new InvalidOperationException(LastError ?? "RCON не подключён.");
        }

        // Свою же команду показываем в консоли как отправленную. Служебный
        // автополлинг (например, players для пинга) не показываем — иначе
        // консоль засоряется одной и той же строкой каждые несколько секунд.
        if (echo)
        {
            Append(new RconLine(DateTimeOffset.Now, "> " + command));
        }

        var response = await client.SendCommandAsync(command, TimeSpan.FromSeconds(8), ct);
        return response;
    }

    /// <summary>Список игроков от RCON-команды players (служебный опрос, без эха).</summary>
    public async Task<IReadOnlyList<RconPlayer>> PlayersAsync(CancellationToken ct)
    {
        var response = await SendAsync("players", ct, echo: false);
        return RconClient.ParsePlayers(response);
    }

    private void Append(RconLine line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }

        try { Line?.Invoke(line); } catch { /* подписчик не должен ронять сессию */ }
    }
}

/// <summary>
/// RCON для всех серверов панели: держит сессии, готовит beserver_x64.cfg
/// (пароль и порт) и переподключается при необходимости.
/// </summary>
public sealed class RconService : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly SettingsStore _settings;
    private readonly ILogger<RconService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly ConcurrentDictionary<string, RconSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    // Последний список игроков от RCON: пинг и IP (их нет в данных мода).
    private readonly ConcurrentDictionary<string, RconPlayerSnapshot[]> _recentPlayers = new(StringComparer.OrdinalIgnoreCase);

    public RconService(ServerRegistry registry, SettingsStore settings, ILoggerFactory loggerFactory,
        ILogger<RconService> logger)
    {
        _registry = registry;
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public RconSession Session(string serverId) =>
        _sessions.GetOrAdd(serverId, id => new RconSession(id, _loggerFactory.CreateLogger<RconSession>()));

    /// <summary>Последний список игроков от RCON (пинг, IP, номер). Может быть пустым.</summary>
    public IReadOnlyList<RconPlayerSnapshot> RecentPlayers(string serverId) =>
        _recentPlayers.TryGetValue(serverId, out var players) ? players : [];

    /// <summary>Порт RCON сервера: из настроек или порт игры + 4.</summary>
    public static int PortFor(ServerSettings server) =>
        server.RconPort is > 0 and <= 65535 ? server.RconPort : server.Port + 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Периодически подключаемся к тем серверам, что запущены, и отключаемся
        // от остановленных. Неудачные попытки повторяем нечасто.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

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
                await SyncAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ошибка синхронизации RCON.");
            }
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        foreach (var runtime in _registry.All)
        {
            var server = runtime.ServerSettings;
            var session = Session(runtime.Id);

            if (!server.RconEnabled)
            {
                if (session.IsConnected)
                {
                    await session.DisconnectAsync();
                }

                continue;
            }

            // Конфиг BattlEye готовим всегда, даже для остановленного сервера:
            // иначе сервер стартует без RCON-порта и подключиться будет некуда.
            var password = EnsureConfig(runtime);
            var port = PortFor(server);

            if (!runtime.Supervisor.IsRunning)
            {
                if (session.IsConnected)
                {
                    await session.DisconnectAsync();
                }

                continue;
            }

            if (session.IsConnected)
            {
                // Список игроков нужен для пинга и IP в карточке игрока.
                await RefreshPlayersAsync(runtime.Id, session, ct);
                continue;
            }

            await session.ConnectAsync("127.0.0.1", port, password, ct);

            if (session.IsConnected)
            {
                await RefreshPlayersAsync(runtime.Id, session, ct);
            }
        }
    }

    /// <summary>Опрашивает RCON-команду players и кэширует список (пинг, IP).</summary>
    private async Task RefreshPlayersAsync(string serverId, RconSession session, CancellationToken ct)
    {
        try
        {
            var players = await session.PlayersAsync(ct);
            _recentPlayers[serverId] = players
                .Select(p => new RconPlayerSnapshot(p.Number, p.Name, p.Ip, p.Port, p.Ping, p.Guid))
                .ToArray();
        }
        catch
        {
            // Сервер мог ещё не ответить — оставляем прошлый снимок.
        }
    }

    /// <summary>
    /// Готовит <c>beserver_x64.cfg</c>: пароль и порт RCON.
    /// Возвращает пароль, который сейчас действует.
    ///
    /// Файл пишется в ДВА места: рядом с сервером (<c>&lt;server&gt;\battleye</c>) и
    /// в каталоге профилей (<c>&lt;profiles&gt;\BattlEye</c>). Именно второе —
    /// рабочее: BattlEye запускается из папки профилей и читает конфиг оттуда.
    /// Если писать только рядом с сервером, RCON не стартует.
    /// </summary>
    public string EnsureConfig(ServerRuntime runtime)
    {
        var server = runtime.ServerSettings;
        var working = ServerPaths.WorkingDirectory(server);
        var profiles = AdminLogService.ProfilesDirectoryOf(server);

        var nearServer = Path.Combine(working, "battleye", "beserver_x64.cfg");
        var inProfiles = Path.Combine(profiles, "BattlEye", "beserver_x64.cfg");

        try
        {
            // Если предыдущий запуск убили, BattlEye оставил конфиг под именем
            // BEServer_x64_active_<pid>.cfg. Возвращаем его на место, иначе
            // следующий старт не увидит настроек RCON.
            RestoreActiveConfig(Path.Combine(profiles, "BattlEye"));

            // Основой берём существующий конфиг из любого места.
            var source = File.Exists(inProfiles) ? inProfiles
                : File.Exists(nearServer) ? nearServer
                : null;

            var text = source is not null ? File.ReadAllText(source) : DefaultConfig();

            // Пароль: из настроек, иначе — уже записанный в файле, иначе новый.
            var password = server.RconPassword;
            if (string.IsNullOrWhiteSpace(password) || IsPlaceholder(password))
            {
                var existing = ReadValue(text, "RConPassword");
                if (string.IsNullOrWhiteSpace(existing) || IsPlaceholder(existing))
                {
                    existing = GeneratePassword();

                    // Сохраняем в настройках, чтобы пароль не менялся при перезапуске.
                    _settings.UpdateServer(runtime.Id, profile =>
                        profile.Server.RconPassword = existing, notify: false);
                }

                password = existing;
            }

            var port = PortFor(server);

            text = SetValue(text, "RConPassword", password);
            text = SetValue(text, "RConPort", port.ToString(CultureInfo.InvariantCulture));

            // Панель использует kick/ban, для них RestrictRCon должен быть 0.
            if (!string.Equals(ReadValue(text, "RestrictRCon"), "0", StringComparison.Ordinal))
            {
                text = SetValue(text, "RestrictRCon", "0");
            }

            var changed = WriteIfChanged(nearServer, text);
            changed |= WriteIfChanged(inProfiles, text);

            if (changed)
            {
                _logger.LogInformation("[{Server}] RCON настроен: порт {Port} ({Path})",
                    runtime.Name, port, inProfiles);
            }

            return password;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось подготовить beserver_x64.cfg.", runtime.Name);
            return server.RconPassword;
        }
    }

    /// <summary>Пишет файл, только если содержимое отличается. Возвращает true при записи.</summary>
    private static bool WriteIfChanged(string path, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(path) && File.ReadAllText(path) == text)
            {
                return false;
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Возвращает конфиг BattlEye к имени <c>beserver_x64.cfg</c>, если прошлый
    /// запуск оставил его как <c>BEServer_x64_active_&lt;pid&gt;.cfg</c>.
    /// </summary>
    private static void RestoreActiveConfig(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var target = Path.Combine(directory, "beserver_x64.cfg");
            if (File.Exists(target))
            {
                return;
            }

            var active = Directory.EnumerateFiles(directory, "BEServer_x64_active_*.cfg").FirstOrDefault();
            if (active is not null)
            {
                File.Move(active, target, overwrite: true);
            }
        }
        catch
        {
            // Не критично: ниже конфиг всё равно будет записан заново.
        }
    }

    private static string DefaultConfig() =>
        string.Join(Environment.NewLine,
        [
            "RConPassword changeme",
            "RConPort 2306",
            "RestrictRCon 0",
            "RConIP 127.0.0.1",
            ""
        ]);

    /// <summary>Заглушка-пароль из шаблона: её нужно заменить на сгенерированный.</summary>
    private static bool IsPlaceholder(string password) =>
        password.Equals("changeme", StringComparison.OrdinalIgnoreCase) ||
        password.Equals("password", StringComparison.OrdinalIgnoreCase);

    public static string GeneratePassword()
    {
        // Читаемый, но стойкий пароль: 24 символа из безопасного алфавита.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(24);
        var builder = new StringBuilder(24);

        foreach (var b in bytes)
        {
            builder.Append(alphabet[b % alphabet.Length]);
        }

        return builder.ToString();
    }

    private static string? ReadValue(string text, string key)
    {
        var m = Regex.Match(text,
            $@"(?im)^[ \t]*{Regex.Escape(key)}[ \t]*(?:=[ \t]*)?(?<v>[^\r\n#;]*)");
        return m.Success ? m.Groups["v"].Value.Trim().Trim('"') : null;
    }

    private static string SetValue(string text, string key, string value)
    {
        var pattern = $@"(?im)^([ \t]*{Regex.Escape(key)}[ \t]*(?:=[ \t]*)?)[^\r\n]*";
        if (Regex.IsMatch(text, pattern))
        {
            return Regex.Replace(text, pattern, m => m.Groups[1].Value + value);
        }

        return text.TrimEnd() + Environment.NewLine + key + " " + value + Environment.NewLine;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.Values)
        {
            try { await session.DisconnectAsync(); }
            catch { /* завершаем работу */ }
        }

        await base.StopAsync(cancellationToken);
    }
}
