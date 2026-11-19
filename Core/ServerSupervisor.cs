using System.Diagnostics;
using System.Text;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Tracker;

namespace DayZPulse.Core;

public sealed record ServerOutputEventArgs(string ServerId, ConsoleLine Line);

public sealed record ServerStateChangedEventArgs(string ServerId, ServerStatus Status, ServerState Previous);

public sealed class ServerStartException(string message) : Exception(message);

/// <summary>
/// Жизненный цикл процесса одного сервера DayZ: запуск, остановка, перезапуск,
/// захват вывода, автоперезапуск после сбоя и снятие метрик.
/// </summary>
public sealed class ServerSupervisor
{
    private readonly object _gate = new();
    private readonly ILogger<ServerSupervisor> _logger;
    private readonly SettingsStore _settings;
    private readonly IEncodingResolver _encodings;
    private readonly ConsoleBuffer _buffer;

    private readonly List<DateTimeOffset> _restartHistory = [];
    private readonly Queue<string> _recentErrors = [];
    private readonly SemaphoreSlim _operations = new(1, 1);

    private ServerProfile _profile;
    private Process? _process;
    private ServerStatus _status = new();
    private bool _intentionalStop;
    private string? _lastLaunchCommandLine;
    private CancellationTokenSource? _autoRestartCts;
    private FileStream? _logFile;

    public ServerSupervisor(ILogger<ServerSupervisor> logger, SettingsStore settings,
        IEncodingResolver encodings, ServerProfile profile, ConsoleBuffer buffer)
    {
        _logger = logger;
        _settings = settings;
        _encodings = encodings;
        _profile = profile;
        _buffer = buffer;
    }

    public event EventHandler<ServerOutputEventArgs>? Output;
    public event EventHandler<ServerStateChangedEventArgs>? StateChanged;

    public string Id => _profile.Id;
    public string Name => string.IsNullOrWhiteSpace(_profile.Name) ? _profile.Id : _profile.Name;

    public ServerProfile Profile
    {
        get
        {
            lock (_gate)
            {
                return _profile;
            }
        }
    }

    public void UpdateProfile(ServerProfile profile)
    {
        lock (_gate)
        {
            _profile = profile;
        }
    }

    public ServerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public ConsoleBuffer Console => _buffer;

    public string? LastLaunchCommandLine
    {
        get
        {
            lock (_gate)
            {
                return _lastLaunchCommandLine;
            }
        }
    }

    public IReadOnlyList<string> RecentErrors
    {
        get
        {
            lock (_gate)
            {
                return _recentErrors.ToArray();
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public ServerSettings Options => _profile.Server;

    // ---------------------------------------------------------------- запуск

    /// <summary>
    /// Перед запуском упаковывает существующие логи сервера в zip и переносит
    /// их в резервные копии, чтобы новый запуск начался с пустых логов.
    /// Управляется настройкой <see cref="Models.LogBackupSettings.OnStart"/>.
    /// </summary>
    private void ArchiveLogsBeforeStart(ServerProfile profile)
    {
        try
        {
            var settings = profile.Backups?.Logs;
            if (settings is null || !settings.OnStart)
            {
                return;
            }

            var result = ServerLogArchiver.Archive(profile);
            if (result.HasArchive)
            {
                AddSystemLine($"Логи упакованы в резервные копии: {result.FileName} " +
                              $"({result.FileCount} файл(ов), удалено {result.RemovedCount}).");
                _logger.LogInformation("[{Server}] Стартовая архивация логов: {Message}", Name, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось архивировать логи при запуске.", Name);
        }
    }

    /// <summary>
    /// Обновляет config.json мода перед запуском: актуальный адрес панели
    /// (порт мог смениться) и токен. Иначе мод шлёт данные на устаревший порт,
    /// и «связь с модом» не появляется.
    /// </summary>
    private void WriteModConfigBeforeStart(ServerProfile profile)
    {
        try
        {
            var tracker = profile.Tracker;
            if (tracker is null || !tracker.Enabled || !tracker.AutoConfigureMod)
            {
                return;
            }

            if (ModConfigWriter.TryWrite(_settings, profile, out var error))
            {
                var info = ModConfigWriter.Describe(profile);
                AddSystemLine($"Конфигурация мода обновлена: {info.Path}");
            }
            else if (error is not null)
            {
                AddSystemLine($"Не удалось обновить config.json мода: {error}");
                _logger.LogWarning("[{Server}] {Error}", Name, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось записать config.json мода перед запуском.", Name);
        }
    }

    /// <summary>
    /// Включает в serverDZ.cfg запись метрик движка (FPS, память, игроки,
    /// зомби, животные) — те же данные, что показывает <c>#monitor</c>.
    /// Панель читает их из RPT-лога.
    /// </summary>
    private void EnableEngineMetricsBeforeStart(ServerProfile profile)
    {
        try
        {
            var options = profile.Server;
            var configFile = string.IsNullOrWhiteSpace(options.ConfigFile) ? "serverDZ.cfg" : options.ConfigFile;
            QuickConfig.EnableEngineMetrics(ServerPaths.WorkingDirectory(options), configFile, 5);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Server}] Не удалось включить метрики движка.", Name);
        }
    }

    public async Task<ServerStatus> StartAsync(string actor, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var profile = _profile;
            if (IsRunning)
            {
                throw new ServerStartException("Сервер уже запущен.");
            }

            var options = profile.Server;
            var workingDirectory = ServerPaths.WorkingDirectory(options);

            // Применяем отложенные правки конфигурации до запуска: они сделаны,
            // пока сервер работал, и должны вступить в силу сейчас.
            if (PendingConfig.HasAny(options))
            {
                var applied = PendingConfig.Apply(options);
                SetState(ServerState.Starting, $"Применены отложенные правки ({applied})", null);
            }

            // Обновляем config.json мода перед каждым запуском: там адрес панели
            // (актуальный порт) и токен. Иначе мод подключается к устаревшему порту.
            WriteModConfigBeforeStart(profile);

            // Включаем запись метрик движка в RPT (аналог #monitor): панель
            // читает оттуда FPS, память, игроков, зомби и животных.
            EnableEngineMetricsBeforeStart(profile);

            // Каждый запуск — с чистыми логами: существующие логи упаковываем
            // в zip, переносим в резервные копии и удаляем исходники.
            ArchiveLogsBeforeStart(profile);

            var executable = ResolveExecutable(options);
            var commandLine = BuildArguments(options, executable, workingDirectory);
            var encoding = _encodings.Resolve(options.ConsoleEncoding);

            SetState(ServerState.Starting, "Запуск сервера", null);

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = commandLine,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = options.RedirectStandardInput,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding
            };

            foreach (var (key, value) in options.EnvironmentVariables)
            {
                psi.Environment[key] = value;
            }

            lock (_gate)
            {
                _intentionalStop = false;
                _lastLaunchCommandLine = $"{Quote(executable)} {commandLine}";
            }

            if (options.ClearConsoleOnStart)
            {
                _buffer.Clear();
            }

            AddSystemLine($"Запуск: {Quote(executable)} {commandLine}", ownerOnly: true);
            AddSystemLine($"Рабочий каталог: {workingDirectory}", ownerOnly: true);

            Process process;
            try
            {
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => OnOutput("out", e.Data);
                process.ErrorDataReceived += (_, e) => OnOutput("err", e.Data);
                process.Exited += (_, _) => OnProcessExited(process);

                if (!process.Start())
                {
                    throw new ServerStartException("Не удалось запустить процесс сервера.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                SetState(ServerState.Failed, $"Ошибка запуска: {ex.Message}", ex.Message);
                AddSystemLine($"ОШИБКА запуска: {ex.Message}");
                throw new ServerStartException($"Не удалось запустить сервер: {ex.Message}");
            }

            ApplyPriority(process, options.ProcessPriority);

            lock (_gate)
            {
                _process = process;
                _status = _status with
                {
                    State = ServerState.Running,
                    ProcessId = process.Id,
                    StartedAt = DateTimeOffset.Now,
                    StoppedAt = null,
                    ExitCode = null,
                    LastError = null,
                    LastMessage = "Сервер запущен"
                };
            }

            RaiseStateChanged(ServerState.Starting);
            AddSystemLine($"Сервер запущен, PID {process.Id}.", ownerOnly: true);
            _logger.LogInformation("[{Server}] Сервер запущен (PID {Pid}) пользователем {Actor}. Команда: {Command}",
                Name, process.Id, actor, _lastLaunchCommandLine);

            return Status;
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<ServerStatus> StopAsync(string actor, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Process? process;
            lock (_gate)
            {
                _intentionalStop = true;
                process = _process;
            }

            CancelAutoRestart();

            if (process is null || process.HasExited)
            {
                lock (_gate)
                {
                    _process?.Dispose();
                    _process = null;
                    _status = _status with
                    {
                        State = ServerState.Stopped,
                        ProcessId = null,
                        StoppedAt = DateTimeOffset.Now,
                        LastMessage = "Сервер остановлен",
                        CpuPercent = 0,
                        WorkingSetBytes = 0,
                        PrivateBytes = 0,
                        ThreadCount = 0,
                        LastSampleAt = null
                    };
                }

                AddSystemLine("Сервер уже остановлен.");
                RaiseStateChanged(ServerState.Stopping);
                return Status;
            }

            var options = _profile.Server;
            SetState(ServerState.Stopping, "Остановка сервера", null);

            if (options.UseStopCommand && options.RedirectStandardInput && !string.IsNullOrWhiteSpace(options.StopCommand))
            {
                try
                {
                    await process.StandardInput.WriteLineAsync(options.StopCommand);
                    await process.StandardInput.FlushAsync(cancellationToken);
                    AddSystemLine($"Отправлена команда остановки: {options.StopCommand}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Server}] Не удалось отправить команду остановки.", Name);
                }
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1, options.GracefulStopSeconds));

            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, CancellationToken.None);
                AddSystemLine("Процесс сервера завершился корректно.");
            }
            catch (TimeoutException)
            {
                AddSystemLine($"Сервер не завершился за {timeout.TotalSeconds:0} с, принудительное завершение.");
                KillTree(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Server}] Процесс не завершился даже после Kill.", Name);
                }
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                throw;
            }

            lock (_gate)
            {
                _process?.Dispose();
                _process = null;
                _status = _status with
                {
                    State = ServerState.Stopped,
                    ProcessId = null,
                    StoppedAt = DateTimeOffset.Now,
                    LastMessage = "Сервер остановлен",
                    CpuPercent = 0,
                    WorkingSetBytes = 0,
                    PrivateBytes = 0,
                    ThreadCount = 0,
                    LastSampleAt = null
                };
            }

            _logger.LogInformation("[{Server}] Сервер остановлен пользователем {Actor}.", Name, actor);
            RaiseStateChanged(ServerState.Stopping);
            return Status;
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<ServerStatus> RestartAsync(string actor, CancellationToken cancellationToken)
    {
        SetState(ServerState.Restarting, "Перезапуск сервера", null);
        await StopAsync(actor, cancellationToken);
        AddSystemLine("Перезапуск: запуск новой копии сервера.");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Запускаем, даже если хост останавливается.
        }

        return await StartAsync(actor, cancellationToken);
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException("Сервер не запущен, отправить команду невозможно.");
        }

        if (!_profile.Server.RedirectStandardInput)
        {
            throw new InvalidOperationException("Перенаправление ввода отключено в настройках, отправка команд недоступна.");
        }

        await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        AddSystemLine($">> {command}");
    }

    // ------------------------------------------------------------- обработчики

    private void OnOutput(string stream, string? data)
    {
        if (data is null)
        {
            return;
        }

        // Служебные строки Steam/движка содержат SteamID и пути — их не
        // показываем администраторам с ограниченными правами.
        var line = _buffer.Add(stream, data, ownerOnly: IsTechnicalOutput(data));
        AppendToLogFile(line);

        if (stream == "err" || LooksLikeError(data))
        {
            lock (_gate)
            {
                _recentErrors.Enqueue($"{line.Timestamp:HH:mm:ss} {data}");
                while (_recentErrors.Count > 50)
                {
                    _recentErrors.Dequeue();
                }
            }
        }

        Output?.Invoke(this, new ServerOutputEventArgs(Id, line));
    }

    /// <summary>
    /// Технические строки движка и Steam (SteamID, breakpad, minidump):
    /// видны только владельцу панели.
    /// </summary>
    private static bool IsTechnicalOutput(string text) =>
        text.Contains("SteamInternal_", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("SteamID", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("breakpad", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("minidump", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeError(string text) =>
        text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("fatal", StringComparison.OrdinalIgnoreCase);

    private void OnProcessExited(Process process)
    {
        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        bool intentional;
        int restartsInWindow;
        ServerSettings options;
        lock (_gate)
        {
            intentional = _intentionalStop;
            options = _profile.Server;
            _restartHistory.RemoveAll(t => DateTimeOffset.Now - t >
                TimeSpan.FromMinutes(Math.Max(1, options.AutoRestartWindowMinutes)));
            restartsInWindow = _restartHistory.Count;
        }

        var crashed = !intentional && exitCode != 0;
        var previousState = _status.State;

        AddSystemLine($"Процесс сервера завершился с кодом {exitCode}.");

        var willRestart = false;
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
            _status = _status with
            {
                State = intentional ? ServerState.Stopped : ServerState.Crashed,
                ProcessId = null,
                StoppedAt = DateTimeOffset.Now,
                ExitCode = exitCode,
                LastError = crashed ? $"Процесс завершился с кодом {exitCode}" : null,
                LastMessage = intentional ? "Сервер остановлен" : $"Сервер завершился (код {exitCode})",
                CpuPercent = 0,
                WorkingSetBytes = 0,
                PrivateBytes = 0,
                ThreadCount = 0,
                LastSampleAt = null
            };

            if (!intentional && options.AutoRestartOnCrash)
            {
                if (restartsInWindow >= Math.Max(1, options.MaxAutoRestarts))
                {
                    _status = _status with
                    {
                        LastError = $"Автоперезапуск остановлен: превышен лимит ({options.MaxAutoRestarts} за {options.AutoRestartWindowMinutes} мин)."
                    };
                }
                else
                {
                    willRestart = true;
                    _status = _status with { AutoRestartArmed = true };
                }
            }
        }

        RaiseStateChanged(previousState);

        if (willRestart)
        {
            _logger.LogWarning("[{Server}] Сервер завершился неожиданно (код {Code}), запланирован автоперезапуск.",
                Name, exitCode);
            ScheduleAutoRestart(options.AutoRestartDelaySeconds);
        }
        else if (!intentional)
        {
            _logger.LogWarning("[{Server}] Сервер завершился (код {Code}), автоперезапуск не будет выполнен.", Name, exitCode);
        }
    }

    private void ScheduleAutoRestart(int delaySeconds)
    {
        CancelAutoRestart(clearArmed: false);
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _autoRestartCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                AddSystemLine($"Автоперезапуск через {delaySeconds} с...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delaySeconds)), cts.Token);

                lock (_gate)
                {
                    if (_intentionalStop || !_status.AutoRestartArmed)
                    {
                        return;
                    }

                    _restartHistory.Add(DateTimeOffset.Now);
                    _status = _status with { AutoRestartArmed = false, AutoRestartsInWindow = _restartHistory.Count };
                }

                AddSystemLine("Выполняется автоперезапуск сервера.");
                await StartAsync("автоперезапуск", cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Отменено вручную.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Server}] Автоперезапуск не удался.", Name);
                AddSystemLine($"Автоперезапуск не удался: {ex.Message}");
                lock (_gate)
                {
                    _status = _status with { AutoRestartArmed = false, LastError = ex.Message };
                }
            }
        }, CancellationToken.None);
    }

    private void CancelAutoRestart(bool clearArmed = true)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _autoRestartCts;
            _autoRestartCts = null;

            if (clearArmed)
            {
                _status = _status with { AutoRestartArmed = false };
            }
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    // ---------------------------------------------------------------- метрики

    public void SampleResources()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                // Процесс завершился, но метрики в статусе остались от прошлого
                // замера — обнуляем, иначе «Ресурсы» висели бы на последних
                // значениях (память, потоки, CPU) при остановленном сервере.
                ClearResourceMetrics();
                return;
            }

            process.Refresh();
            double cpu = 0;
            try
            {
                cpu = CpuSampler.Sample(process);
            }
            catch
            {
                // Некоторые системы запрещают чтение счётчиков.
            }

            lock (_gate)
            {
                _status = _status with
                {
                    CpuPercent = cpu,
                    WorkingSetBytes = process.WorkingSet64,
                    PrivateBytes = process.PrivateMemorySize64,
                    ThreadCount = process.Threads.Count,
                    LastSampleAt = DateTimeOffset.Now
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Server}] Не удалось снять метрики процесса.", Name);
            ClearResourceMetrics();
        }
    }

    /// <summary>Обнуляет метрики процесса (память, потоки, CPU) при остановке.</summary>
    private void ClearResourceMetrics()
    {
        lock (_gate)
        {
            if (_status.CpuPercent == 0 && _status.WorkingSetBytes == 0 &&
                _status.PrivateBytes == 0 && _status.ThreadCount == 0)
            {
                return;
            }

            _status = _status with
            {
                CpuPercent = 0,
                WorkingSetBytes = 0,
                PrivateBytes = 0,
                ThreadCount = 0,
                LastSampleAt = null
            };
        }
    }

    // ------------------------------------------------------------- вспомогательное

    private void SetState(ServerState state, string message, string? error)
    {
        ServerState previous;
        lock (_gate)
        {
            previous = _status.State;
            _status = _status with { State = state, LastMessage = message, LastError = error };
        }

        RaiseStateChanged(previous);
    }

    private void RaiseStateChanged(ServerState previous) =>
        StateChanged?.Invoke(this, new ServerStateChangedEventArgs(Id, Status, previous));

    public void ClearConsole() => _buffer.Clear();

    private void AddSystemLine(string text, bool ownerOnly = false)
    {
        var line = _buffer.Add("sys", text, ownerOnly);
        AppendToLogFile(line);
        Output?.Invoke(this, new ServerOutputEventArgs(Id, line));
    }

    private static string ResolveExecutable(ServerSettings options)
    {
        var path = ServerPaths.Executable(options);

        if (!File.Exists(path))
        {
            throw new ServerStartException(
                $"Исполняемый файл сервера не найден: {path}. " +
                "Проверьте базовую папку и имя файла в настройках.");
        }

        return path;
    }

    /// <summary>
    /// Собирает строку аргументов запуска из настроек сервера. Используется и
    /// при запуске, и для предпросмотра «Итоговой команды» в панели.
    /// </summary>
    public static string BuildArguments(ServerSettings options, string executable, string workingDirectory)
    {
        // Конфиг обычно лежит рядом с исполняемым файлом сервера.
        var exeDirectory = string.IsNullOrWhiteSpace(executable)
            ? workingDirectory
            : Path.GetDirectoryName(executable) ?? workingDirectory;

        var config = options.ConfigFile;
        var configFull = string.IsNullOrWhiteSpace(config)
            ? ""
            : Path.IsPathRooted(config)
                ? config
                : ResolveNextToExe(config, workingDirectory, exeDirectory);

        var profiles = string.IsNullOrWhiteSpace(options.ProfilesDirectory)
            ? Path.Combine(workingDirectory, "profiles")
            : Path.IsPathRooted(options.ProfilesDirectory)
                ? options.ProfilesDirectory
                : Path.Combine(workingDirectory, options.ProfilesDirectory);

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{exe}"] = executable,
            ["{workingDir}"] = workingDirectory,
            ["{port}"] = options.Port.ToString(),
            ["{config}"] = configFull,
            ["{profiles}"] = profiles,
            ["{mods}"] = options.Mods,
            ["{servermods}"] = options.ServerMods
        };

        var arguments = options.Arguments ?? "";
        foreach (var (token, value) in tokens)
        {
            arguments = arguments.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(options.Mods) &&
            !arguments.Contains("-mod=", StringComparison.OrdinalIgnoreCase))
        {
            arguments = $"{arguments} -mod={options.Mods}".Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.ServerMods) &&
            !arguments.Contains("-serverMod=", StringComparison.OrdinalIgnoreCase))
        {
            arguments = $"{arguments} -serverMod={options.ServerMods}".Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
        {
            arguments = $"{arguments} {options.ExtraArguments}".Trim();
        }

        return arguments.Trim();
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>
    /// Ищет файл рядом с исполняемым файлом сервера, затем в рабочем каталоге.
    /// Конфиг DayZ обычно лежит в папке с DayZServer_x64.exe.
    /// </summary>
    private static string ResolveNextToExe(string fileName, string workingDirectory, string exeDirectory)
    {
        var nearExe = Path.Combine(exeDirectory, fileName);
        if (File.Exists(nearExe))
        {
            return nearExe;
        }

        var inWorking = Path.Combine(workingDirectory, fileName);
        if (File.Exists(inWorking))
        {
            return inWorking;
        }

        // Файла ещё нет — считаем, что он появится рядом с exe.
        return nearExe;
    }

    private void ApplyPriority(Process process, string priority)
    {
        try
        {
            process.PriorityClass = priority?.ToLowerInvariant() switch
            {
                "high" => ProcessPriorityClass.High,
                "abovenormal" => ProcessPriorityClass.AboveNormal,
                "belownormal" => ProcessPriorityClass.BelowNormal,
                "idle" or "low" => ProcessPriorityClass.Idle,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Server}] Не удалось установить приоритет процесса.", Name);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Процесс мог уже завершиться.
        }
    }

    private void OpenLogFile()
    {
        try
        {
            var directory = ServerPaths.ServerLogsDirectory(_profile.Server);
            Directory.CreateDirectory(directory);

            var safe = Id.Length > 0 ? Id : "server";
            var path = Path.Combine(directory, $"server-{safe}-{DateTime.Now:yyyyMMdd}.log");
            _logFile = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось открыть файл журнала сервера.", Name);
        }
    }

    private void AppendToLogFile(ConsoleLine line)
    {
        if (_logFile is null)
        {
            return;
        }

        try
        {
            var text = $"{line.Timestamp:yyyy-MM-dd HH:mm:ss} [{line.Stream}] {line.Text}{Environment.NewLine}";
            _logFile.Write(Encoding.UTF8.GetBytes(text));
            _logFile.Flush();
        }
        catch
        {
            // Журналирование не должно ломать работу.
        }
    }

    /// <summary>Подготавливает журнал и выполняет автозапуск, если он включён в профиле.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_settings.Current.Logging.WriteServerLogFile)
        {
            OpenLogFile();
        }

        if (_profile.Server.StartOnLaunch)
        {
            try
            {
                await StartAsync("автозапуск при старте приложения", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Server}] Не удалось выполнить автозапуск.", Name);
                AddSystemLine($"Автозапуск не удался: {ex.Message}");
            }
        }
    }

    /// <summary>Корректно останавливает сервер при завершении приложения.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)    {
        CancelAutoRestart();

        try
        {
            if (IsRunning)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(_profile.Server.GracefulStopSeconds + 20));
                await StopAsync("остановка панели", cts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось корректно остановить сервер.", Name);

            Process? process;
            lock (_gate)
            {
                process = _process;
            }

            if (process is not null)
            {
                KillTree(process);
            }
        }

        _logFile?.Dispose();
        _logFile = null;
    }

    /// <summary>
    /// Закрывает файл журнала сервера. Нужно перед удалением папки сервера:
    /// иначе Windows не даст удалить файл, который держит открытым панель.
    /// </summary>
    public void CloseLogFile()
    {
        try
        {
            _logFile?.Dispose();
        }
        catch
        {
            // Файл мог быть уже закрыт.
        }
        finally
        {
            _logFile = null;
        }
    }
}
