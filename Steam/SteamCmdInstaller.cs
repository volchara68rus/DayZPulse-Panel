using DayZPulse.Infrastructure;

namespace DayZPulse.Steam;

/// <summary>
/// Состояние подготовки: SteamCMD рядом с панелью и сервер DayZ в папке сервера.
/// </summary>
public sealed record SteamInstallStatus(
    bool Installed,
    bool Warm,
    bool Running,
    string Directory,
    string Output,
    string? Error,
    int ProgressPercent,
    string Phase,
    bool ServerInstalled = false,
    string ServerDirectory = "",
    string Stage = "idle",
    long? BytesDone = null,
    long? BytesTotal = null,
    double? BytesPerSecond = null);

/// <summary>
/// Готовит окружение панели: скачивает SteamCMD в папку панели и, по запросу
/// панели, загружает сервер DayZ в папку сервера через SteamCMD.
/// </summary>
public sealed class SteamCmdInstaller : BackgroundService
{
    private readonly SettingsStore _settings;
    private readonly SteamCmdService _steam;
    private readonly ILogger<SteamCmdInstaller> _logger;
    private readonly object _gate = new();

    private SteamInstallStatus _status = new(false, false, false, AppPaths.SteamCmdDirectory, "", null, 0, "", false, "", "idle");
    private Task<SteamCommandResult>? _running;
    private Task<SteamCommandResult>? _runningServer;

    public SteamCmdInstaller(SettingsStore settings, SteamCmdService steam,
        ILogger<SteamCmdInstaller> logger)
    {
        _settings = settings;
        _steam = steam;
        _logger = logger;
    }

    /// <summary>Состояние подготовки для страницы создания владельца и панели.</summary>
    public SteamInstallStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Каталог SteamCMD: всегда рядом с панелью.</summary>
    public string SteamDirectory => AppPaths.SteamCmdDirectory;

    /// <summary>В папке уже лежит готовый сервер DayZ?</summary>
    public bool IsServerReady(string? directory) => _steam.IsServerInstalled(directory);

    // ------------------------------------------------------------------ SteamCMD

    /// <summary>
    /// Запускает установку SteamCMD, не дожидаясь завершения.
    /// Нужна запросам из браузера: иначе ответ ждал бы всю загрузку.
    /// </summary>
    public bool StartInstall()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return false;
            }

            _running = Task.Run(() => InstallSteamCmdAsync(CancellationToken.None));
            return true;
        }
    }

    /// <summary>Запускает установку SteamCMD, если она ещё не идёт.</summary>
    public Task<SteamCommandResult> EnsureInstallingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return _running;
            }

            _running = InstallSteamCmdAsync(cancellationToken);
            return _running;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Автоматической загрузки при старте нет: SteamCMD ставится со страницы
        // создания владельца, сервер DayZ — из панели после входа в Steam.
        return Task.CompletedTask;
    }

    /// <summary>Скачивает SteamCMD в папку панели и выполняет анонимный прогрев.</summary>
    private async Task<SteamCommandResult> InstallSteamCmdAsync(CancellationToken cancellationToken)
    {
        var directory = AppPaths.SteamCmdDirectory;
        var present = _steam.IsInstalled(directory);

        SetStatus(new SteamInstallStatus(present, _steam.IsWarm(directory), true, directory, "", null,
            present ? 60 : 0, present ? "Проверка SteamCMD" : "Скачивание SteamCMD",
            _steam.IsServerInstalled(_settings.Current.DefaultServerDirectory),
            _settings.Current.DefaultServerDirectory ?? "", "steamcmd"));

        if (present && _steam.IsWarm(directory))
        {
            PersistInstalled(directory);
            SetStatus(Status with { Running = false, ProgressPercent = 100, Phase = "Готово", Stage = "steamcmd" });
            _logger.LogInformation("SteamCMD готов: {Path}", _steam.ExecutablePath(directory));
            return new SteamCommandResult(true, 0, "SteamCMD уже установлен.", null);
        }

        _logger.LogInformation("Скачивание SteamCMD в {Directory}…", directory);

        var progress = new SynchronousProgress<SteamDownloadProgress>(p =>
            SetStatus(Status with
            {
                Running = true,
                ProgressPercent = p.Percent,
                Phase = p.Phase,
                Stage = "steamcmd"
            }));

        var result = await _steam.InstallAsync(directory, progress, cancellationToken);

        if (result.Success)
        {
            PersistInstalled(directory);
            SetStatus(new SteamInstallStatus(true, _steam.IsWarm(directory), false, directory,
                result.Output, null, 100, "Готово",
                _steam.IsServerInstalled(_settings.Current.DefaultServerDirectory),
                _settings.Current.DefaultServerDirectory ?? "", "steamcmd"));
            _logger.LogInformation("SteamCMD установлен и прогрет: {Path}", _steam.ExecutablePath(directory));
        }
        else
        {
            _settings.Update(s => s.Steam.Installed = false, notify: false);
            SetStatus(new SteamInstallStatus(false, false, false, directory, result.Output,
                result.Error ?? "Не удалось установить SteamCMD.", 0, "Ошибка",
                false, _settings.Current.DefaultServerDirectory ?? "", "steamcmd"));
            _logger.LogWarning("Не удалось установить SteamCMD: {Error}", result.Error);
        }

        return result;
    }

    // -------------------------------------------------------------- сервер DayZ

    /// <summary>
    /// Запускает загрузку сервера DayZ в папку сервера, не дожидаясь завершения.
    /// Требует установленного SteamCMD и входа в Steam (иначе Steam отклонит загрузку).
    /// </summary>
    public bool StartServerInstall(string serverDirectory)
    {
        lock (_gate)
        {
            if (_runningServer is { IsCompleted: false })
            {
                return false;
            }

            _runningServer = Task.Run(() => InstallServerCoreAsync(serverDirectory, CancellationToken.None));
            return true;
        }
    }

    public Task<SteamCommandResult> EnsureServerInstallingAsync(string serverDirectory, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_runningServer is { IsCompleted: false })
            {
                return _runningServer;
            }

            _runningServer = InstallServerCoreAsync(serverDirectory, cancellationToken);
            return _runningServer;
        }
    }

    /// <summary>Скачивает сервер DayZ (app 223350) под текущим аккаунтом Steam.</summary>
    private async Task<SteamCommandResult> InstallServerCoreAsync(string serverDirectory, CancellationToken cancellationToken)
    {
        var steamDirectory = AppPaths.SteamCmdDirectory;

        if (!_steam.IsInstalled(steamDirectory))
        {
            SetStatus(Status with
            {
                Running = false,
                ProgressPercent = 0,
                Phase = "Ошибка",
                Stage = "server",
                ServerDirectory = serverDirectory,
                Error = "SteamCMD не установлен."
            });
            return new SteamCommandResult(false, -1, "", "SteamCMD не установлен.");
        }

        if (_steam.IsServerInstalled(serverDirectory))
        {
            SetStatus(Status with
            {
                Running = false,
                ProgressPercent = 100,
                Phase = "Готово",
                Stage = "server",
                ServerInstalled = true,
                ServerDirectory = serverDirectory,
                Error = null
            });
            return new SteamCommandResult(true, 0, "Сервер DayZ уже установлен.", null);
        }

        var username = (_settings.Current.Steam.Username ?? "").Trim();

        if (username.Length == 0)
        {
            SetStatus(Status with
            {
                Running = false,
                ProgressPercent = 0,
                Phase = "Ошибка",
                Stage = "server",
                ServerDirectory = serverDirectory,
                Error = "Сначала войдите в Steam."
            });
            return new SteamCommandResult(false, -1, "", "Сначала войдите в Steam.");
        }

        _logger.LogInformation("Скачивание сервера DayZ в {Directory}…", serverDirectory);

        var progress = new SynchronousProgress<SteamDownloadProgress>(p =>
            SetStatus(Status with
            {
                Running = true,
                ProgressPercent = p.Percent,
                Phase = p.Phase,
                Stage = "server",
                ServerDirectory = serverDirectory,
                ServerInstalled = false,
                Error = null,
                BytesDone = p.BytesDone,
                BytesTotal = p.BytesTotal,
                BytesPerSecond = p.BytesPerSecond
            }));

        var result = await _steam.InstallServerAsync(steamDirectory, serverDirectory, username, progress, cancellationToken);

        if (result.Success)
        {
            SetStatus(Status with
            {
                Running = false,
                ProgressPercent = 100,
                Phase = "Готово",
                Stage = "server",
                ServerInstalled = true,
                ServerDirectory = serverDirectory,
                Error = null
            });
            _logger.LogInformation("Сервер DayZ установлен: {Directory}", serverDirectory);
        }
        else
        {
            SetStatus(Status with
            {
                Running = false,
                ProgressPercent = 0,
                Phase = "Ошибка",
                Stage = "server",
                ServerInstalled = false,
                ServerDirectory = serverDirectory,
                Error = result.Error ?? "Не удалось установить сервер DayZ."
            });
            _logger.LogWarning("Не удалось установить сервер DayZ: {Error}", result.Error);
        }

        return result;
    }

    private void PersistInstalled(string directory) =>
        _settings.Update(s =>
        {
            s.Steam.Installed = true;
            s.Steam.InstalledAt = DateTimeOffset.Now;
            s.Steam.Directory = directory;
        }, notify: false);

    private void SetStatus(SteamInstallStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }
    }
}

/// <summary>
/// Прогресс без асинхронной доставки: обработчик вызывается в том же потоке,
/// где пришёл отчёт. Так порядок обновлений сохраняется и итоговое
/// состояние не перетирается отложенными сообщениями.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
