using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DayZPulse.Infrastructure;

namespace DayZPulse.Steam;

public sealed record SteamCommandResult(bool Success, int ExitCode, string Output, string? Error);

/// <summary>Прогресс скачивания SteamCMD для страницы создания владельца.</summary>
public sealed record SteamDownloadProgress(
    int Percent,
    string Phase,
    string? Message = null,
    long? BytesDone = null,
    long? BytesTotal = null,
    double? BytesPerSecond = null);

public sealed record SteamLoginResult(bool Success, bool NeedsGuardCode, string? Output, string? Error);

/// <summary>Состояние фонового входа в Steam.</summary>
public enum SteamLoginState
{
    Idle,
    Running,

    /// <summary>Steam просит подтвердить вход в мобильном приложении.</summary>
    WaitingConfirm,

    /// <summary>Steam просит ввести код Steam Guard.</summary>
    WaitingGuard,

    Done,
    Failed
}

public sealed record SteamLoginStatus(
    SteamLoginState State,
    string? Error,
    string Output,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Скачивает SteamCMD и выполняет вход в Steam.
///
/// Особенности SteamCMD, которые здесь учтены:
/// <list type="bullet">
/// <item>при первом запуске он обновляется и перезапускает себя, теряя аргументы;</item>
/// <item>после команды он остаётся в интерактивном режиме, поэтому нужен <c>+quit</c>;</item>
/// <item>пароль и код Steam Guard читаются из stdin, о чём он сообщает в свой лог.</item>
/// </list>
/// Поэтому сначала выполняется анонимный прогрев, а вход читает лог SteamCMD.
/// </summary>
public sealed class SteamCmdService
{
    private static readonly string[] DownloadUrls =
    [
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip",
        "https://media.steampowered.com/client/installer/steamcmd.zip"
    ];

    private readonly ILogger<SteamCmdService> _logger;
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();

    private SteamLoginState _state = SteamLoginState.Idle;
    private string? _error;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private Process? _process;
    private string? _pendingGuardCode;
    private DateTimeOffset _lastCachedCheck = DateTimeOffset.MinValue;
    private bool _cachedCheckResult;
    private string? _cachedCheckUser;
    private bool _cachedCheckRunning;

    /// <summary>Номер текущей попытки входа: старые воркеры не должны перетирать новую.</summary>
    private int _loginAttempt;

    public SteamCmdService(ILogger<SteamCmdService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Каталог SteamCMD. По умолчанию — подкаталог <c>steamcmd</c> рядом с панелью
    /// (<see cref="Infrastructure.AppPaths.SteamCmdDirectory"/>). Клиент общий для
    /// всех серверов и живёт вместе с панелью, а не в папке сервера.
    /// </summary>
    public static string ResolveDirectory(string? configured, string? defaultServerDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return AppPaths.SteamCmdDirectory;
    }

    public string ExecutablePath(string directory) => Path.Combine(directory, "steamcmd.exe");

    /// <summary>Идентификатор сервера DayZ в Steam (DayZ Dedicated Server, бесплатный).</summary>
    public const string DayZServerAppId = "223350";

    /// <summary>
    /// AppID игры DayZ. Именно к нему привязаны предметы Workshop
    /// (моды), в отличие от app 223350 (сервер).
    /// </summary>
    public const string DayZGameAppId = "221100";

    /// <summary>Имя исполняемого файла сервера DayZ.</summary>
    public const string DayZServerExecutable = "DayZServer_x64.exe";

    /// <summary>В папке уже лежит установленный сервер DayZ?</summary>
    public bool IsServerInstalled(string? installDirectory) =>
        !string.IsNullOrWhiteSpace(installDirectory) &&
        File.Exists(Path.Combine(installDirectory!, DayZServerExecutable));

    public bool IsInstalled(string directory) => File.Exists(ExecutablePath(directory));

    /// <summary>SteamCMD прогрет: анонимный вход уже проходил, обновления скачаны.</summary>
    public bool IsWarm(string directory) => File.Exists(Path.Combine(directory, "config", "config.vdf"));

    private string ConsoleLogPath(string directory) => Path.Combine(directory, "logs", "console_log.txt");

    public SteamLoginStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new SteamLoginStatus(_state, _error, _output.ToString(), _startedAt, _finishedAt);
            }
        }
    }

    /// <summary>
    /// Полный выход из Steam: удаляет сохранённую сессию SteamCMD,
    /// включая sentry-файлы Steam Guard (ssfn*), из-за которых
    /// код не запрашивался повторно.
    /// </summary>
    public async Task<SteamCommandResult> LogoutAsync(string directory, CancellationToken cancellationToken)
    {
        var log = new StringBuilder();

        try
        {
            if (!IsInstalled(directory))
            {
                return new SteamCommandResult(true, 0, "SteamCMD не установлен — выходить не из чего.", null);
            }

            // Просим SteamCMD выйти, если сессия жива.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ExecutablePath(directory),
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                foreach (var argument in new[] { "+logout", "+quit" })
                {
                    psi.ArgumentList.Add(argument);
                }

                using var process = Process.Start(psi);
                if (process is not null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                        log.AppendLine("SteamCMD выполнил выход.");
                    }
                    catch (OperationCanceledException)
                    {
                        TryKill(process);
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("Не удалось вызвать +logout: " + ex.Message);
            }

            // Удаляем сохранённые данные входа: именно они дают вход без кода.
            var removed = ClearSession(directory, log);
            log.AppendLine($"Удалено файлов сессии: {removed}.");
            log.AppendLine("Вход в Steam сброшен. Понадобятся логин, пароль и код Steam Guard.");

            _logger.LogInformation("Сессия SteamCMD очищена ({Count} файлов).", removed);
            return new SteamCommandResult(true, 0, log.ToString(), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка выхода из Steam.");
            return new SteamCommandResult(false, -1, log.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Убирает файлы, в которых SteamCMD хранит данные входа.
    /// Sentry-файлы <c>ssfn*</c> — те самые, что заменяют Steam Guard.
    /// </summary>
    private int ClearSession(string directory, StringBuilder log)
    {
        var removed = 0;

        void TryRemoveFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                    log.AppendLine("Удалён " + Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"Не удалось удалить {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        void TryRemoveDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed++;
                    log.AppendLine("Очищен каталог " + Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"Не удалось очистить {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        // Данные учётной записи.
        TryRemoveFile(Path.Combine(directory, "config", "config.vdf"));
        TryRemoveFile(Path.Combine(directory, "config", "loginusers.vdf"));
        TryRemoveFile(Path.Combine(directory, "config", "config.vdf.tmp"));

        // Sentry-файлы Steam Guard.
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "ssfn*"))
            {
                TryRemoveFile(file);
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("Не удалось перечислить ssfn-файлы: " + ex.Message);
        }

        // Профили и кэши конкретного пользователя.
        TryRemoveDirectory(Path.Combine(directory, "userdata"));
        TryRemoveDirectory(Path.Combine(directory, "appcache"));

        return removed;
    }

    // ---------------------------------------------------------------- установка

    /// <summary>
    /// Скачивает и обновляет сервер DayZ (app 223350) в указанную папку.
    /// Сервер доступен только аккаунту, владеющему DayZ, поэтому вход обязателен.
    /// </summary>
    public async Task<SteamCommandResult> InstallServerAsync(string steamCmdDirectory, string installDirectory,
        string username, IProgress<SteamDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var log = new StringBuilder();

        void Report(int percent, string phase) => progress?.Report(new SteamDownloadProgress(percent, phase));

        void ReportProgress(SteamDownloadProgress value) => progress?.Report(value);

        try
        {
            if (!IsInstalled(steamCmdDirectory))
            {
                return new SteamCommandResult(false, -1, log.ToString(), "SteamCMD не установлен.");
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                return new SteamCommandResult(false, -1, log.ToString(), "Не указан аккаунт Steam.");
            }

            Directory.CreateDirectory(installDirectory);

            Report(2, "Скачивание сервера DayZ");

            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath(steamCmdDirectory),
                WorkingDirectory = steamCmdDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // +force_install_dir должен идти до входа в Steam, иначе SteamCMD
            // предупреждает «Please use force_install_dir before logon!».
            psi.ArgumentList.Add("+force_install_dir");
            psi.ArgumentList.Add(installDirectory);

            // +login с одним логином (без пароля) подгружает сохранённый билет
            // из config.vdf. Без него SteamCMD не авторизуется и отвечает
            // «not online or not logged in to Steam».
            psi.ArgumentList.Add("+login");
            psi.ArgumentList.Add(username);

            foreach (var argument in new[] { "+app_update", DayZServerAppId, "validate", "+quit" })
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new SteamCommandResult(false, -1, log.ToString(), "Не удалось запустить SteamCMD.");
            }

            // Прогресс загрузки SteamCMD пишет в console_log.txt, а не в stdout,
            // поэтому следим за файлом, а вывод лишь вычитываем (иначе канал
            // заполнится и процесс зависнет).
            var consoleLog = ConsoleLogPath(steamCmdDirectory);
            var logOffset = File.Exists(consoleLog) ? new FileInfo(consoleLog).Length : 0;

            var drain = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (line.Length > 0)
                    {
                        log.AppendLine(line);
                    }
                }
            }, cancellationToken);

            var watcher = Task.Run(async () =>
            {
                // Для расчёта скорости: предыдущее значение байт и время замера.
                long lastBytes = 0;
                var lastSample = DateTimeOffset.Now;
                double lastSpeed = 0;
                var verifying = false;

                while (!process.HasExited)
                {
                    await Task.Delay(500, cancellationToken);

                    var tail = ReadLogTail(consoleLog, ref logOffset);
                    if (tail.Length == 0)
                    {
                        continue;
                    }

                    foreach (var line in tail.Split('\n'))
                    {
                        var text = line.Trim();
                        if (text.Length == 0)
                        {
                            continue;
                        }

                        log.AppendLine(text);

                        // "Update state (0x61) downloading, progress: 0.11 (6359205 / 5954150556)".
                        var progress = ExtractUpdateProgress(text);
                        if (progress is not null)
                        {
                            var (_, done, total) = progress.Value;

                            // Скорость считаем по приросту байт между замерами.
                            var now = DateTimeOffset.Now;
                            var elapsed = (now - lastSample).TotalSeconds;
                            if (done >= lastBytes && elapsed >= 0.4)
                            {
                                var instant = (done - lastBytes) / elapsed;
                                // Сглаживаем, чтобы цифра не прыгала.
                                lastSpeed = lastSpeed <= 0 ? instant : lastSpeed * 0.6 + instant * 0.4;
                                lastBytes = done;
                                lastSample = now;
                            }

                            // Процент считаем из «скачано / всего» — тогда число
                            // и полоса всегда совпадают и не могут разойтись.
                            var percent = total > 0
                                ? Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 100)
                                : 0;

                            verifying = text.Contains("verifying", StringComparison.OrdinalIgnoreCase);

                            ReportProgress(new SteamDownloadProgress(
                                Math.Clamp(percent, 0, 100),
                                verifying ? "Проверка файлов" : "Скачивание сервера DayZ",
                                null,
                                done > 0 ? done : null,
                                total > 0 ? total : null,
                                !verifying && lastSpeed > 0 ? lastSpeed : null));
                            continue;
                        }

                        if (text.Contains("preallocat", StringComparison.OrdinalIgnoreCase))
                        {
                            ReportProgress(new SteamDownloadProgress(2, "Подготовка диска"));
                        }
                        else if (text.Contains("verifying", StringComparison.OrdinalIgnoreCase))
                        {
                            ReportProgress(new SteamDownloadProgress(99, "Проверка файлов"));
                        }
                    }
                }
            }, cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            try { await Task.WhenAll(drain, watcher); } catch { /* процесс завершился */ }

            if (IsServerInstalled(installDirectory))
            {
                Report(100, "Готово");
                return new SteamCommandResult(true, process.ExitCode, log.ToString(), null);
            }

            var error = DetectError(log.ToString()) ?? "Сервер DayZ не установился: папка не содержит DayZServer_x64.exe.";
            Report(0, "Ошибка загрузки сервера");
            return new SteamCommandResult(false, process.ExitCode, log.ToString(), error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка установки сервера DayZ.");
            Report(0, "Ошибка");
            return new SteamCommandResult(false, -1, log.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Скачивает предмет Steam Workshop (мод) в указанный каталог.
    /// Команда <c>+workshop_download_item &lt;appId&gt; &lt;itemId&gt;</c> кладёт файлы в
    /// <c>steamapps/workshop/content/&lt;appId&gt;/&lt;itemId&gt;</c>; после загрузки
    /// переносим их в целевую папку.
    /// </summary>
    public async Task<SteamCommandResult> DownloadWorkshopAsync(string steamCmdDirectory, string appId,
        string itemId, string targetDirectory, string username, IProgress<SteamDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var log = new StringBuilder();

        void Report(int percent, string phase) => progress?.Report(new SteamDownloadProgress(percent, phase));

        try
        {
            if (!IsInstalled(steamCmdDirectory))
            {
                return new SteamCommandResult(false, -1, log.ToString(), "SteamCMD не установлен.");
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                return new SteamCommandResult(false, -1, log.ToString(), "Сначала войдите в Steam.");
            }

            if (string.IsNullOrWhiteSpace(itemId) || !itemId.All(char.IsDigit))
            {
                return new SteamCommandResult(false, -1, log.ToString(), "Некорректный Workshop ID.");
            }

            Report(2, "Скачивание мода из Workshop");

            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath(steamCmdDirectory),
                WorkingDirectory = steamCmdDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("+login");
            psi.ArgumentList.Add(username);

            foreach (var argument in new[] { "+workshop_download_item", appId, itemId, "validate", "+quit" })
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new SteamCommandResult(false, -1, log.ToString(), "Не удалось запустить SteamCMD.");
            }

            var consoleLog = ConsoleLogPath(steamCmdDirectory);
            var logOffset = File.Exists(consoleLog) ? new FileInfo(consoleLog).Length : 0;

            var drain = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (line.Length > 0) log.AppendLine(line);
                }
            }, cancellationToken);

            var watcher = Task.Run(async () =>
            {
                while (!process.HasExited)
                {
                    await Task.Delay(500, cancellationToken);
                    var tail = ReadLogTail(consoleLog, ref logOffset);
                    if (tail.Length == 0) continue;

                    foreach (var line in tail.Split('\n'))
                    {
                        var text = line.Trim();
                        if (text.Length == 0) continue;
                        log.AppendLine(text);

                        var parsed = ExtractUpdateProgress(text);
                        if (parsed is not null)
                        {
                            var (_, done, total) = parsed.Value;
                            var percent = total > 0 ? Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 100) : 0;
                            Report(percent, text.Contains("verifying", StringComparison.OrdinalIgnoreCase)
                                ? "Проверка мода" : "Скачивание мода из Workshop");
                        }
                    }
                }
            }, cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            try { await Task.WhenAll(drain, watcher); } catch { /* завершился */ }

            // Куда SteamCMD положил файлы.
            var downloaded = Path.Combine(steamCmdDirectory, "steamapps", "workshop", "content", appId, itemId);
            if (!Directory.Exists(downloaded))
            {
                return new SteamCommandResult(false, process.ExitCode, log.ToString(),
                    DetectError(log.ToString()) ?? "Мод не скачался: папка пуста.");
            }

            Report(95, "Раскладка мода");

            Directory.CreateDirectory(targetDirectory);
            CopyDirectory(downloaded, targetDirectory, overwrite: true);

            Report(100, "Готово");
            return new SteamCommandResult(true, process.ExitCode, log.ToString(), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка загрузки мода из Workshop ({Item}).", itemId);
            Report(0, "Ошибка");
            return new SteamCommandResult(false, -1, log.ToString(), ex.Message);
        }
    }

    /// <summary>Копирует каталог рекурсивно.</summary>
    private static void CopyDirectory(string source, string target, bool overwrite)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = file.Replace(source, target);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite);
        }
    }

    /// <summary>
    /// Скачивает SteamCMD и выполняет анонимный прогрев, чтобы клиент
    /// обновился и создал свои каталоги до входа в аккаунт.
    /// <paramref name="progress"/> получает проценты для страницы создания владельца.
    /// </summary>
    public async Task<SteamCommandResult> InstallAsync(string directory,
        IProgress<SteamDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var log = new StringBuilder();

        void Report(int percent, string phase) => progress?.Report(new SteamDownloadProgress(percent, phase));

        try
        {
            // SteamCMD лежит в папке сервера, которая может ещё не существовать.
            Directory.CreateDirectory(directory);

            // Если движок уже есть, анонимный прогрев всё равно нужен.
            if (!IsInstalled(directory))
            {
                Report(0, "Скачивание SteamCMD");

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DayZPulse/1.0");

                byte[]? payload = null;
                foreach (var url in DownloadUrls)
                {
                    try
                    {
                        log.AppendLine($"Скачивание: {url}");
                        payload = await DownloadWithProgressAsync(http, url, log, progress, cancellationToken);
                        break;
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine($"Не удалось скачать с {url}: {ex.Message}");
                    }
                }

                if (payload is null)
                {
                    Report(0, "Ошибка загрузки");
                    return new SteamCommandResult(false, -1, log.ToString(), "Не удалось скачать SteamCMD.");
                }

                Report(60, "Распаковка архива");
                var zipPath = Path.Combine(directory, "steamcmd.zip");
                await File.WriteAllBytesAsync(zipPath, payload, cancellationToken);
                ZipFile.ExtractToDirectory(zipPath, directory, overwriteFiles: true);
                TryDelete(zipPath);
                log.AppendLine($"Распаковано, {payload.Length / 1024} КБ.");

                if (!IsInstalled(directory))
                {
                    Report(0, "Ошибка распаковки");
                    return new SteamCommandResult(false, -1, log.ToString(), "После распаковки не найден steamcmd.exe.");
                }
            }
            else
            {
                Report(60, "SteamCMD уже скачан");
            }

            // Анонимный вход: SteamCMD обновляется и создаёт структуру каталогов.
            var warmup = await WarmupAsync(directory, log, progress, cancellationToken);

            if (!warmup)
            {
                Report(0, "Ошибка настройки");
                return new SteamCommandResult(false, -1, log.ToString(), "SteamCMD не завершил первичную настройку.");
            }

            Report(100, "Готово");
            return new SteamCommandResult(true, 0, log.ToString(), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка установки SteamCMD.");
            Report(0, "Ошибка");
            return new SteamCommandResult(false, -1, log.ToString(), ex.Message);
        }
    }

    /// <summary>Скачивает файл потоком, сообщая о прогрессе.</summary>
    private static async Task<byte[]> DownloadWithProgressAsync(HttpClient http, string url,
        StringBuilder log, IProgress<SteamDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new MemoryStream(total > 0 ? (int)total : 0);

        var buffer = new byte[81920];
        long read = 0;
        var lastReported = -1;

        // Архив небольшой и качается быстро: сразу сообщаем начало загрузки,
        // чтобы полоса не оставалась на нуле.
        progress?.Report(new SteamDownloadProgress(2, "Скачивание SteamCMD"));

        int written;
        while ((written = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, written), cancellationToken);
            read += written;

            // 5–55% — доля загрузки архива; отчёт идёт и в журнал, и в состояние.
            if (total <= 0)
            {
                continue;
            }

            var percent = 5 + (int)(read * 50 / total);
            if (percent == lastReported)
            {
                continue;
            }

            lastReported = percent;
            progress?.Report(new SteamDownloadProgress(percent, "Скачивание SteamCMD"));
            log.AppendLine($"Загрузка {read * 100 / total}% ({read / 1024} из {total / 1024} КБ)");
        }

        return target.ToArray();
    }

    /// <summary>
    /// Анонимный вход прогревает клиент: при первом запуске SteamCMD
    /// перезапускает себя, поэтому ждём появления config.vdf.
    /// </summary>
    private async Task<bool> WarmupAsync(string directory, StringBuilder log,
        IProgress<SteamDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        log.AppendLine("Первичная настройка SteamCMD (анонимный вход)…");
        progress?.Report(new SteamDownloadProgress(70, "Первичная настройка SteamCMD"));

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath(directory),
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in new[] { "+login", "anonymous", "+quit" })
        {
            psi.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("Не удалось запустить SteamCMD: " + ex.Message);
            return false;
        }

        using (process)
        {
            // Проценты обновления SteamCMD пишет не в вывод, а в bootstrap_log.txt,
            // поэтому следим за файлом, как и при входе в Steam.
            var bootstrapLog = Path.Combine(directory, "logs", "bootstrap_log.txt");
            var logOffset = File.Exists(bootstrapLog) ? new FileInfo(bootstrapLog).Length : 0;

            // Вывод всё равно нужно вычитывать: иначе буфер канала заполнится
            // и SteamCMD остановится.
            var drain = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (line.Length > 0)
                    {
                        log.AppendLine(line);
                    }
                }
            }, cancellationToken);

            var watcher = Task.Run(async () =>
            {
                while (!process.HasExited)
                {
                    await Task.Delay(400, cancellationToken);

                    var tail = ReadLogTail(bootstrapLog, ref logOffset);
                    if (tail.Length == 0)
                    {
                        continue;
                    }

                    foreach (var line in tail.Split('\n'))
                    {
                        var text = line.Trim();
                        if (text.Length == 0)
                        {
                            continue;
                        }

                        log.AppendLine(text);

                        // Доля загруженного архива обновления: "17,265 of 43,472 KB".
                        var percent = ExtractProgress(text);
                        if (percent is not null)
                        {
                            // 70–96% — общая шкала для фазы обновления.
                            var mapped = 70 + (int)(percent.Value * 26 / 100);
                            progress?.Report(new SteamDownloadProgress(mapped, "Обновление SteamCMD"));
                            continue;
                        }

                        // После загрузки SteamCMD распаковывает и устанавливает обновление.
                        var phase = UpdatePhase(text);
                        if (phase is not null)
                        {
                            progress?.Report(new SteamDownloadProgress(phase.Value.Percent, phase.Value.Name));
                        }
                    }
                }
            }, cancellationToken);

            // SteamCMD при обновлении перезапускает себя: ждём завершения,
            // но не дольше разумного времени.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(8));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
            }

            try { await Task.WhenAll(drain, watcher); } catch { /* процесс завершился */ }
        }

        // Признак готовности — файл конфигурации, который создаёт клиент.
        var deadline = DateTimeOffset.Now.AddSeconds(30);
        while (DateTimeOffset.Now < deadline)
        {
            if (IsWarm(directory))
            {
                log.AppendLine("SteamCMD готов к работе.");
                _logger.LogInformation("SteamCMD прогрет: {Directory}", directory);
                return true;
            }

            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        log.AppendLine("Предупреждение: файл конфигурации SteamCMD не появился.");
        return IsInstalled(directory);
    }

    // -------------------------------------------------------------------- вход

    /// <summary>
    /// Проверяет сохранённую сессию в фоне и вызывает <paramref name="onResult"/>.
    /// Повторные вызовы в течение двух минут используют уже полученный результат.
    /// </summary>
    public void CheckCachedSessionInBackground(string directory, string username, Action<bool> onResult)
    {
        if (string.IsNullOrWhiteSpace(username) || !IsInstalled(directory))
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_cachedCheckUser, username, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.Now - _lastCachedCheck < TimeSpan.FromMinutes(2))
            {
                var cached = _cachedCheckResult;
                _ = Task.Run(() => onResult(cached));
                return;
            }

            if (_cachedCheckRunning)
            {
                return;
            }

            _cachedCheckRunning = true;
        }

        _ = Task.Run(async () =>
        {
            var ok = false;
            try
            {
                ok = await CheckCachedSessionAsync(directory, username, CancellationToken.None);
            }
            catch
            {
                // Проверка не критична: флаг просто останется прежним.
            }
            finally
            {
                lock (_gate)
                {
                    _cachedCheckRunning = false;
                    _lastCachedCheck = DateTimeOffset.Now;
                    _cachedCheckUser = username;
                    _cachedCheckResult = ok;
                }
            }

            try { onResult(ok); } catch { /* обработчик результата не критичен */ }
        });
    }

    /// <summary>
    /// Проверяет, есть ли в SteamCMD сохранённая сессия для аккаунта.
    /// Запускает вход без пароля: Steam пишет «using cached credentials» и «OK»,
    /// если сохранённый токен ещё действителен.
    /// </summary>
    public async Task<bool> HasCachedSessionAsync(string directory, string username, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || !IsInstalled(directory))
        {
            return false;
        }

        // Не запускаем проверку слишком часто: каждый раз это новый процесс SteamCMD.
        lock (_gate)
        {
            if (string.Equals(_cachedCheckUser, username, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.Now - _lastCachedCheck < TimeSpan.FromMinutes(2))
            {
                return _cachedCheckResult;
            }
        }

        var result = await CheckCachedSessionAsync(directory, username, cancellationToken);

        lock (_gate)
        {
            _lastCachedCheck = DateTimeOffset.Now;
            _cachedCheckUser = username;
            _cachedCheckResult = result;
        }

        return result;
    }

    private async Task<bool> CheckCachedSessionAsync(string directory, string username, CancellationToken cancellationToken)
    {
        var logPath = ConsoleLogPath(directory);
        var offset = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath(directory),
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in new[] { "+login", username, "+quit" })
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
            }

            var fresh = ReadLogTail(logPath, ref offset);
            return fresh.Contains("using cached credentials", StringComparison.OrdinalIgnoreCase) &&
                   fresh.Contains("Logging in user", StringComparison.OrdinalIgnoreCase) &&
                   !fresh.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase) &&
                   !fresh.Contains("Waiting for confirmation", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось проверить сохранённую сессию Steam.");
            return false;
        }
    }

    /// <summary>
    /// Запускает вход в фоне. Панель опрашивает <see cref="Status"/>.
    /// Если <paramref name="restart"/> — прерывает текущую попытку и начинает заново.
    /// </summary>
    public bool StartLogin(string directory, string username, string password, bool restart = false)
    {
        Process? previous = null;
        int attempt;

        lock (_gate)
        {
            if (_state is SteamLoginState.Running or SteamLoginState.WaitingGuard or SteamLoginState.WaitingConfirm)
            {
                if (!restart)
                {
                    return false;
                }

                // Прерываем текущую попытку: пользователь запросил повтор.
                previous = _process;
                _process = null;
            }

            attempt = ++_loginAttempt;
            _state = SteamLoginState.Running;
            _error = null;
            _startedAt = DateTimeOffset.Now;
            _finishedAt = null;
            _pendingGuardCode = null;
            _output.Clear();
        }

        if (previous is not null)
        {
            TryKill(previous);
        }

        _ = Task.Run(() => LoginWorkerAsync(directory, username, password, attempt));
        return true;
    }

    /// <summary>Передаёт код Steam Guard ожидающему процессу.</summary>
    public bool ProvideGuardCode(string code)
    {
        lock (_gate)
        {
            var process = _process;

            // Код принимаем и когда Steam ждёт подтверждения: пользователь мог
            // выбрать ввод кода вместо подтверждения в приложении.
            if (_state is not (SteamLoginState.WaitingGuard or SteamLoginState.WaitingConfirm) ||
                process is null || process.HasExited)
            {
                _pendingGuardCode = code;
                return false;
            }

            try
            {
                process.StandardInput.WriteLine(code.Trim());
                process.StandardInput.Flush();
                _state = SteamLoginState.Running;
                _output.AppendLine("Код Steam Guard отправлен.");
                _logger.LogInformation("Код Steam Guard передан.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось передать код Steam Guard.");
                return false;
            }
        }
    }

    private async Task LoginWorkerAsync(string directory, string username, string password, int attempt)
    {
        // Проверяет, что эта попытка всё ещё актуальна.
        bool Current()
        {
            lock (_gate)
            {
                return _loginAttempt == attempt;
            }
        }

        void SetState(SteamLoginState state, string? error)
        {
            lock (_gate)
            {
                if (_loginAttempt != attempt)
                {
                    return;
                }

                _state = state;
                _error = error;
                _finishedAt = DateTimeOffset.Now;
            }
        }

        try
        {
            if (!IsInstalled(directory))
            {
                var install = await InstallAsync(directory, null, CancellationToken.None);
                if (!install.Success)
                {
                    SetState(SteamLoginState.Failed, install.Error ?? "SteamCMD не установлен.");
                    return;
                }
            }
            else if (!IsWarm(directory))
            {
                // Первичная настройка после установки вручную.
                Append("Подготовка SteamCMD…");
                await WarmupAsync(directory, _output, null, CancellationToken.None);
            }

            if (!Current())
            {
                return;
            }

            Append("Обращение к Steam…");

            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath(directory),
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // +quit обязателен: иначе SteamCMD остаётся в интерактивной консоли.
            foreach (var argument in new[] { "+login", username, password, "+quit" })
            {
                psi.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                SetState(SteamLoginState.Failed, "Не удалось запустить SteamCMD.");
                return;
            }

            lock (_gate)
            {
                if (_loginAttempt != attempt)
                {
                    // Попытку уже отменили: не публикуем этот процесс.
                    TryKill(process);
                    return;
                }

                _process = process;

                if (_pendingGuardCode is { Length: > 0 } pending)
                {
                    try
                    {
                        process.StandardInput.WriteLine(pending);
                        process.StandardInput.Flush();
                        _pendingGuardCode = null;
                    }
                    catch { /* код уйдёт, когда процесс будет готов */ }
                }
            }

            var result = RunAndWatchAsync(process, directory, attempt);

            lock (_gate)
            {
                if (_process == process)
                {
                    _process = null;
                }
            }

            var watch = await result;

            if (!Current())
            {
                return;
            }

            if (watch.LoggedIn)
            {
                Append("Вход выполнен. Сессия SteamCMD сохранена.");
                SetState(SteamLoginState.Done, null);
                return;
            }

            if (watch.NeedsGuard)
            {
                SetState(SteamLoginState.Failed,
                    "Код Steam Guard не принят. Проверьте код или запросите новый.");
                return;
            }

            if (watch.NeedsConfirm)
            {
                SetState(SteamLoginState.Failed,
                    "Подтверждение входа в приложении Steam не получено. Подтвердите вход и повторите.");
                return;
            }

            SetState(SteamLoginState.Failed, watch.Error ?? "Steam отклонил вход.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка входа в Steam.");
            SetState(SteamLoginState.Failed, ex.Message);
        }
    }

    private sealed record WatchResult(bool LoggedIn, bool NeedsGuard, bool NeedsConfirm, string? Error);

    /// <summary>
    /// Ждёт завершения входа, читая и stdout, и лог SteamCMD:
    /// приглашение кода и результат пишутся именно в лог.
    /// </summary>
    private async Task<WatchResult> RunAndWatchAsync(Process process, string directory, int attempt)
    {
        // Запрос кода и итог входа SteamCMD пишет в console_log,
        // а ожидание подтверждения и токен сессии — в connection_log.
        // Поэтому следим за обоими файлами.
        var logPath = ConsoleLogPath(directory);
        var logOffset = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

        var connectionPath = Path.Combine(directory, "logs", "connection_log.txt");
        var connectionOffset = File.Exists(connectionPath) ? new FileInfo(connectionPath).Length : 0;

        var guardRequested = false;
        var confirmRequested = false;

        var readOutput = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                {
                    Append(line);
                }
            }
        });

        var readError = Task.Run(async () =>
        {
            var text = await process.StandardError.ReadToEndAsync();
            if (text.Trim().Length > 0)
            {
                Append(text.Trim());
            }
        });

        // Следим за логами SteamCMD: там появляется запрос кода, ожидание
        // подтверждения в мобильном приложении и итог входа.
        var watcher = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                await Task.Delay(600);

                var tail = ReadLogTail(logPath, ref logOffset);
                var connectionTail = ReadLogTail(connectionPath, ref connectionOffset);

                if (tail.Length == 0 && connectionTail.Length == 0)
                {
                    continue;
                }

                if (tail.Length > 0)
                {
                    Append(tail.TrimEnd());
                }

                if (connectionTail.Length > 0)
                {
                    Append(connectionTail.TrimEnd());
                }

                // Подтверждение через мобильное приложение (без ввода кода).
                if (!confirmRequested && !guardRequested && LooksLikeConfirmPrompt(connectionTail))
                {
                    confirmRequested = true;
                    lock (_gate)
                    {
                        if (_loginAttempt == attempt)
                        {
                            _state = SteamLoginState.WaitingConfirm;
                        }
                    }

                    _logger.LogInformation("Steam ждёт подтверждения входа в мобильном приложении.");
                    continue;
                }

                // Ввод кода Steam Guard.
                if (!guardRequested && LooksLikeGuardPrompt(tail))
                {
                    guardRequested = true;
                    lock (_gate)
                    {
                        if (_loginAttempt == attempt)
                        {
                            _state = SteamLoginState.WaitingGuard;
                        }
                    }

                    _logger.LogInformation("Steam запросил код Steam Guard.");
                }
            }
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }

        await Task.WhenAll(readOutput, readError, watcher);

        // Читаем оба лога целиком: итог мог появиться в любом из них.
        var log = Status.Output + ReadWholeFile(logPath) + ReadWholeFile(connectionPath);
        var loggedIn = LooksLikeLoggedIn(log);

        return new WatchResult(loggedIn, guardRequested, confirmRequested, loggedIn ? null : DetectError(log));
    }

    private static string ReadWholeFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Дочитывает новые строки лога SteamCMD.</summary>
    private static string ReadLogTail(string path, ref long offset)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (stream.Length <= offset)
            {
                if (stream.Length < offset)
                {
                    offset = 0; // лог пересоздан
                }
                else
                {
                    return "";
                }
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            offset = stream.Length;
            return text;
        }
        catch
        {
            return "";
        }
    }

    private static string? DetectError(string log)
    {
        var lower = log.ToLowerInvariant();

        if (lower.Contains("invalid password"))
        {
            return "Неверный логин или пароль Steam.";
        }

        if (lower.Contains("account logon denied") || lower.Contains("rate limit"))
        {
            return "Steam временно ограничил попытки входа. Подождите и повторите.";
        }

        if (lower.Contains("steam guard") || lower.Contains("two-factor"))
        {
            return "Требуется код Steam Guard.";
        }

        if (lower.Contains("not online or not logged in"))
        {
            return "SteamCMD не авторизован. Войдите в Steam в панели.";
        }

        if (lower.Contains("no subscription"))
        {
            return "У аккаунта нет подписки на DayZ. Войдите под аккаунтом, которому принадлежит игра.";
        }

        if (lower.Contains("failed to install") || lower.Contains("network"))
        {
            return "Проблема с сетью при обращении к Steam.";
        }

        if (lower.Contains("invalid login"))
        {
            return "Неверный логин Steam.";
        }

        return null;
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            _output.AppendLine(line);

            if (_output.Length > 200_000)
            {
                _output.Remove(0, _output.Length - 100_000);
            }
        }
    }

    /// <summary>
    /// Достаёт прогресс из строки загрузки депо SteamCMD вида
    /// "Update state (0x61) downloading, progress: 0.11 (6359205 / 5954150556)".
    /// </summary>
    private static (int Percent, long Done, long Total)? ExtractUpdateProgress(string line)
    {
        var marker = line.IndexOf("progress:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var rest = line[(marker + "progress:".Length)..].Trim();

        // Часть «1.00» — это уже процент (не доля). Разбираем до скобок или пробела.
        var end = rest.IndexOfAny(['(', ' ', '\r', '\n']);
        var number = end < 0 ? rest : rest[..end];

        if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value is < 0 or > 100)
        {
            return null;
        }

        // Скобки «(6359205 / 5954150556)» — скачано и общий вес в байтах.
        long done = 0, total = 0;
        var open = rest.IndexOf('(');
        var close = rest.IndexOf(')', open + 1);
        if (open >= 0 && close > open)
        {
            var parts = rest[(open + 1)..close].Split('/');
            if (parts.Length == 2)
            {
                long.TryParse(parts[0].Trim(), out done);
                long.TryParse(parts[1].Trim(), out total);
            }
        }

        return ((int)Math.Round(value * 100), done, total);
    }

    /// <summary>
    /// Считает процент по строке вида
    /// "Downloading update (17,265 of 43,472 KB)..." из bootstrap_log.txt.
    /// </summary>
    private static int? ExtractProgress(string line)
    {
        if (!line.Contains(" of ", StringComparison.OrdinalIgnoreCase) ||
            !line.Contains("KB", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var open = line.IndexOf('(');
        var close = line.IndexOf(')', open + 1);
        if (open < 0 || close < 0)
        {
            return null;
        }

        var parts = line[(open + 1)..close].Split(" of ", StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        static double Parse(string value)
        {
            var digits = new string(value.Where(c => char.IsDigit(c) || c == '.').ToArray());
            return double.TryParse(digits, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
        }

        var current = Parse(parts[0]);
        var total = Parse(parts[1]);

        return total <= 0 ? null : (int)Math.Clamp(current * 100 / total, 0, 100);
    }

    /// <summary>
    /// Определяет фазу обновления SteamCMD по строке bootstrap_log:
    /// после загрузки идут распаковка, установка и запуск.
    /// </summary>
    private static (int Percent, string Name)? UpdatePhase(string line)
    {
        var text = line.ToLowerInvariant();

        if (text.Contains("extracting package"))
        {
            return (97, "Распаковка обновления");
        }

        if (text.Contains("installing update"))
        {
            return (98, "Установка обновления");
        }

        if (text.Contains("update complete") || text.Contains("launching"))
        {
            return (99, "Запуск SteamCMD");
        }

        return null;
    }

    private static bool LooksLikeGuardPrompt(string text) =>
        text.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Two-factor code", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Enter the current code", StringComparison.OrdinalIgnoreCase) ||
        (text.Contains("Please enter", StringComparison.OrdinalIgnoreCase) &&
         text.Contains("code", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Steam просит подтвердить вход в мобильном приложении (вместо ввода кода).
    /// SteamCMD пишет в connection_log «Waiting for confirmation».
    /// </summary>
    private static bool LooksLikeConfirmPrompt(string text) =>
        text.Contains("Waiting for confirmation", StringComparison.OrdinalIgnoreCase) ||
        (text.Contains("confirm", StringComparison.OrdinalIgnoreCase) &&
         (text.Contains("mobile", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("device", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("Steam app", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("authenticator", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Вход действительно выполнен: Steam выдал токен сессии.</summary>
    private static bool LooksLikeLoggedIn(string text) =>
        text.Contains("Logged in OK", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Successfully generated token via password authentication", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("PollAuthSessionStatus succeeded and has refresh token", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("has refresh token", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("RecvMsgClientLogOnResponse() : [U:1:", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* игнорируем */ }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* уже завершён */ }
    }
}
