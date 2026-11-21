using System.Security.Cryptography;
using System.Text.Json;
using DayZPulse.Infrastructure;
using DayZPulse.Models;

namespace DayZPulse.Web;

/// <summary>Файл в раздаче панели: путь относительно каталога установки.</summary>
public sealed record PanelFileInfo(string Path, long Size, string Sha256);

/// <summary>Манифест обновления панели (только файлы программы, без wwwroot).</summary>
public sealed record PanelArchive(long Size, string Sha256);

public sealed record PanelManifest(
    string Version,
    string Updated,
    IReadOnlyList<PanelFileInfo> Files,
    PanelArchive? Archive);

/// <summary>Состояние обновления панели для интерфейса.</summary>
public sealed record PanelUpdateState(
    string InstalledVersion,
    string? RemoteVersion,
    string? Updated,
    bool Available,
    bool UpToDate,
    bool Writable,
    int ChangedFiles,
    long DownloadSize,
    bool Busy,
    int Percent,
    string Phase);

/// <summary>Результат установки обновления панели.</summary>
public sealed record PanelInstallResult(bool Success, string? Version, string? Error, bool Restart = false);

/// <summary>
/// Обновление самой панели (файлы программы) по изменённым файлам, без
/// установщика. Веб-часть (<c>wwwroot</c>) обновляется отдельно сервисом
/// <see cref="WebUiUpdateService"/> — здесь её нет, чтобы механизмы не
/// конфликтовали.
///
/// Как это работает:
///   1) читаем манифест <c>panel-files.json</c> и сравниваем хеши файлов с
///      установленными;
///   2) качаем только изменившиеся файлы (обычно это <c>DayZPulse.dll</c>
///      и пара служебных — единицы мегабайт), сверяя контрольные суммы;
///   3) раскладываем их в каталог-заготовку и запускаем помощник, который
///      дождётся остановки панели, подменит файлы и запустит её снова —
///      файлы заняты, пока процесс жив.
///
/// Файлы раздаются по одному: <c>/download/panel/&lt;путь&gt;</c>.
/// </summary>
public sealed class PanelUpdateService
{
    private const string DefaultBaseUrl = "https://dayzpulse.ru";
    private const string ManifestName = "panel-files.json";
    private const string ZipName = "dayzpulse-panel.zip";

    /// <summary>
    /// Порог, после которого выгоднее скачать сжатый архив, а не отдельные
    /// файлы: типовое обновление меняет несколько мегабайт, а архив (~58 МБ)
    /// нужен только при крупных переходах (например, со старой однофайловой сборки).
    /// </summary>
    private const long ArchiveThreshold = 8 * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly ILogger<PanelUpdateService> _logger;
    private readonly SettingsStore _settings;
    private readonly Hosting.AppLifecycle _lifecycle;

    public PanelUpdateService(ILogger<PanelUpdateService> logger, SettingsStore settings,
        Hosting.AppLifecycle lifecycle)
    {
        _logger = logger;
        _settings = settings;
        _lifecycle = lifecycle;
    }

    /// <summary>Базовый адрес сайта: тот же, что и для веб-части и мода.</summary>
    public string BaseUrl =>
        string.IsNullOrWhiteSpace(_settings.Current.Steam.PanelModBaseUrl)
            ? DefaultBaseUrl
            : _settings.Current.Steam.PanelModBaseUrl.TrimEnd('/');

    public bool Busy { get; private set; }
    public string Phase { get; private set; } = "";
    public int Percent { get; private set; }

    /// <summary>Версия установленной панели (из сборки).</summary>
    public string InstalledVersion =>
        typeof(PanelUpdateService).Assembly.GetName().Version?.ToString() ?? "";

    /// <summary>Каталог установки панели (рядом с исполняемым файлом).</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>Каталог-заготовка с новыми файлами панели.</summary>
    public static string StagingDirectory => Path.Combine(AppPaths.DataRoot, "update", "staging");

    /// <summary>Можно ли писать в каталог установки (иначе нужен установщик).</summary>
    public static bool CanWriteInstall()
    {
        try
        {
            var probe = Path.Combine(InstallDirectory, $".write-probe-{Guid.NewGuid():n}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------- проверка

    public async Task<PanelManifest?> FetchManifestAsync(CancellationToken ct)
    {
        try
        {
            // Сайт за Cloudflare: он кеширует загрузки на часы, поэтому к
            // адресу добавляем нестабильный параметр — иначе манифест мог бы
            // подолгу отдаваться из кеша и обновление «не находилось».
            var bust = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var json = await Http.GetStringAsync($"{BaseUrl}/download/{ManifestName}?v={bust}", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var updated = root.TryGetProperty("updated", out var u) ? u.GetString() ?? "" : "";

            var files = new List<PanelFileInfo>();
            if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    files.Add(new PanelFileInfo(
                        item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                        item.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
                        item.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : ""));
                }
            }

            PanelArchive? archive = null;
            if (root.TryGetProperty("archive", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                archive = new PanelArchive(
                    a.TryGetProperty("size", out var asz) && asz.TryGetInt64(out var asize) ? asize : 0,
                    a.TryGetProperty("sha256", out var ah) ? ah.GetString() ?? "" : "");
            }

            return new PanelManifest(version, updated, files, archive);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось получить манифест обновления панели.");
            return null;
        }
    }

    /// <summary>Файлы манифеста, которых нет локально или у которых другой хеш.</summary>
    public static IReadOnlyList<PanelFileInfo> ChangedFiles(PanelManifest manifest)
    {
        var result = new List<PanelFileInfo>();
        var dir = InstallDirectory;

        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            var target = SafePath(dir, file.Path);
            if (target is null)
            {
                continue; // путь пытается выйти за каталог установки
            }

            if (!File.Exists(target) || !string.Equals(Hash(target), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file);
            }
        }

        return result;
    }

    public async Task<PanelUpdateState> GetStateAsync(CancellationToken ct)
    {
        var installed = InstalledVersion;
        var manifest = await FetchManifestAsync(ct);

        if (manifest is null)
        {
            return new PanelUpdateState(installed, null, null, false, true,
                CanWriteInstall(), 0, 0, Busy, Percent, Phase);
        }

        var changed = ChangedFiles(manifest);
        var rawSize = changed.Sum(f => f.Size);

        // Если изменений много, выгоднее скачать сжатый архив целиком.
        var useArchive = manifest.Archive is not null && rawSize > ArchiveThreshold;
        var size = useArchive ? manifest.Archive!.Size : rawSize;

        return new PanelUpdateState(
            installed,
            manifest.Version,
            manifest.Updated,
            Available: !string.IsNullOrEmpty(manifest.Version),
            UpToDate: changed.Count == 0,
            Writable: CanWriteInstall(),
            ChangedFiles: changed.Count,
            DownloadSize: size,
            Busy,
            Percent,
            Phase);
    }

    // -------------------------------------------------------------- установка

    public async Task<PanelInstallResult> InstallAsync(CancellationToken ct)
    {
        if (Busy)
        {
            return new PanelInstallResult(false, null, "Обновление уже идёт.");
        }

        Busy = true;
        Percent = 0;
        Phase = "Подготовка";

        try
        {
            if (!CanWriteInstall())
            {
                return new PanelInstallResult(false, null,
                    "Каталог панели недоступен для записи — обновите её установщиком.");
            }

            var manifest = await FetchManifestAsync(ct);
            if (manifest is null || manifest.Files.Count == 0)
            {
                return new PanelInstallResult(false, null, "Не удалось получить список файлов обновления.");
            }

            var changed = ChangedFiles(manifest);
            if (changed.Count == 0)
            {
                return new PanelInstallResult(false, null, "Обновление не требуется.");
            }

            var rawSize = changed.Sum(f => f.Size);
            var useArchive = manifest.Archive is not null && rawSize > ArchiveThreshold;

            var staging = StagingDirectory;
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            var changedPaths = changed.Select(f => f.Path).ToArray();

            if (useArchive)
            {
                Phase = "Скачивание обновления";
                var tmpZip = Path.Combine(AppPaths.TempDirectory, "panel-" + Guid.NewGuid().ToString("n") + ".zip");
                try
                {
                    using (var response = await Http.GetAsync($"{BaseUrl}/download/{ZipName}?v={manifest.Archive!.Sha256}", ct))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var output = File.Create(tmpZip);
                        await response.Content.CopyToAsync(output, ct);
                    }

                    if (!string.Equals(Hash(tmpZip), manifest.Archive!.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return new PanelInstallResult(false, null,
                            "Архив обновления повреждён. Загрузка отменена.");
                    }

                    Percent = 70;
                    Phase = "Распаковка";
                    System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, staging, overwriteFiles: true);
                }
                finally
                {
                    try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* занят */ }
                }

                // Ставим на подмену ровно изменившиеся файлы из распакованного архива.
                foreach (var entry in changed)
                {
                    var src = SafePath(staging, entry.Path);
                    if (src is null || !File.Exists(src) ||
                        !string.Equals(Hash(src), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return new PanelInstallResult(false, null,
                            $"Файл {entry.Path} в архиве отсутствует или повреждён. Обновление отменено.");
                    }
                }

                Percent = 90;
            }
            else
            {
                long total = rawSize;
                long done = 0;

                foreach (var file in changed)
                {
                    Phase = "Скачивание " + Path.GetFileName(file.Path);

                    var target = SafePath(staging, file.Path)
                        ?? throw new InvalidOperationException("Некорректный путь в манифесте: " + file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    var url = $"{BaseUrl}/download/panel/{file.Path.Replace('\\', '/')}?v={file.Sha256}";
                    using (var response = await Http.GetAsync(url, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var output = File.Create(target);
                        await response.Content.CopyToAsync(output, ct);
                    }

                    if (!string.Equals(Hash(target), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return new PanelInstallResult(false, null,
                            $"Файл {file.Path} повреждён при загрузке. Обновление отменено.");
                    }

                    done += file.Size;
                    Percent = total > 0 ? (int)(done * 90 / total) : 90;
                }
            }

            Percent = 95;
            Phase = "Перезапуск";

            // Помощник подменит файлы после остановки панели и запустит её снова.
            ScheduleApplyAndRestart(changedPaths);

            var version = manifest.Version;
            _settings.Update(s =>
            {
                s.PanelUpdate.LastVersion = version;
                s.PanelUpdate.UpdatedAt = DateTimeOffset.Now;
            });

            // Инициируем выход: помощник поднимет панель заново уже с новым кодом.
            _lifecycle.RequestApplyUpdate("обновление панели");

            _logger.LogInformation("Подготовлено обновление панели до версии {Version} ({Count} файлов).",
                version, changed.Count);

            return new PanelInstallResult(true, version, null, Restart: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось подготовить обновление панели.");
            Phase = "Ошибка";
            return new PanelInstallResult(false, null, ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    // --------------------------------------------------------------- помощник

    /// <summary>
    /// Пишет помощник, который дождётся остановки панели, подменит файлы из
    /// заготовки и запустит панель заново. Файлы заняты, пока процесс жив,
    /// поэтому подмена идёт отдельным процессом после выхода.
    ///
    /// В раскладке «служба + трей» трей — это тот же exe, поэтому после выхода
    /// панели помощник закрывает оставшиеся процессы DayZPulse.exe, подменяет
    /// файлы, поднимает службу и, если трей был запущен, запускает его снова.
    /// </summary>
    private static void ScheduleApplyAndRestart(string[] files)
    {
        var script = BuildApplyScript(
            files,
            StagingDirectory,
            InstallDirectory,
            AppPaths.BaseDirectory,
            Environment.ProcessId,
            Service.WindowsServiceManager.IsWindowsServiceHost());

        var path = Path.Combine(Path.GetTempPath(), $"dayzpulse-apply-{Guid.NewGuid():n}.cmd");
        File.WriteAllText(path, script, new System.Text.UTF8Encoding(false));

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };

        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>
    /// Собирает текст помощника. Вынесено отдельно и без побочных эффектов,
    /// чтобы можно было проверить сборку (см. тест) и не рисковать запуском.
    /// </summary>
    internal static string BuildApplyScript(string[] files, string staging, string installDir,
        string dataDir, int pid, bool isService)
    {
        var target = installDir.TrimEnd('\\');
        staging = staging.TrimEnd('\\');
        dataDir = dataDir.TrimEnd('\\');

        // Копируем точечно (только обновлённые файлы), а не весь каталог.
        var copy = files
            .Select(rel => rel.Replace('/', '\\'))
            .Select(win => $"copy /Y \"{staging}\\{win}\" \"{target}\\{win}\" >nul 2>&1")
            .ToList();

        var restartBlock = string.Join("\r\n", new[]
        {
            "set /a n=0",
            ":startloop",
            "sc query DayZPulse | findstr /i \"RUNNING\" >nul",
            "if not errorlevel 1 goto tray",
            "sc start DayZPulse >nul 2>&1",
            "set /a n+=1",
            "if %n% GEQ 150 goto tray",
            "ping -n 2 127.0.0.1 >nul",
            "goto startloop"
        });

        var lines = new List<string>
        {
            "@echo off",
            "setlocal",
            "set TRAY=",
            // Ждём именно свой процесс (по PID), а не любой DayZPulse.exe:
            // в раскладке «служба + трей» трей живёт дольше.
            "set /a n=0",
            ":waitpid",
            $"tasklist /fi \"pid eq {pid}\" 2>nul | findstr /i \"DayZPulse.exe\" >nul",
            "if errorlevel 1 goto afterexit",
            "set /a n+=1",
            "if %n% GEQ 150 goto afterexit",
            "ping -n 2 127.0.0.1 >nul",
            "goto waitpid",
            ":afterexit"
        };

        if (isService)
        {
            // Был ли запущен трей: после выхода службы считаем оставшиеся
            // DayZPulse.exe. Если хоть один остался — это трей (служба уже
            // вышла), и его надо вернуть после обновления. Через tasklist, без
            // WMI/PowerShell — надёжнее на любых сборках Windows.
            lines.Add("set COUNT=0");
            lines.Add("for /f %%N in ('tasklist /fi \"imagename eq DayZPulse.exe\" ^| find /i /c \"DayZPulse.exe\"') do set COUNT=%%N");
            lines.Add("if %COUNT% GEQ 1 set TRAY=1");
        }

        lines.Add("taskkill /f /im DayZPulse.exe >nul 2>&1");
        lines.Add("ping -n 2 127.0.0.1 >nul");
        lines.AddRange(copy);

        if (isService)
        {
            lines.Add(restartBlock);
            lines.Add(":tray");
            lines.Add($"if defined TRAY start \"\" \"{target}\\DayZPulse.exe\" --tray --no-browser --data-dir=\"{dataDir}\"");
        }
        else
        {
            // Режим трея: поднимаем панель тем же запуском, что и был.
            lines.Add($"start \"\" \"{target}\\DayZPulse.exe\" --tray --no-browser --data-dir=\"{dataDir}\"");
        }

        lines.Add(":end");
        lines.Add($"rmdir /s /q \"{staging}\" >nul 2>&1");
        lines.Add("del \"%~f0\" >nul 2>&1");

        return string.Join("\r\n", lines);
    }

    // --------------------------------------------------------------- утилиты

    /// <summary>Безопасно склеивает базовый путь и относительный (защита от «..»).</summary>
    private static string? SafePath(string baseDir, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(baseDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
