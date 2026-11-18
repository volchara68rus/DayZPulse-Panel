using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DayZPulse.Infrastructure;
using DayZPulse.Models;

namespace DayZPulse.Web;

/// <summary>Сведения о раздаче веб-части панели на сайте.</summary>
public sealed record WebUiManifest(
    string Version,
    string Updated,
    long Size,
    string Sha256,
    string? InstalledVersion,
    bool UpToDate);

/// <summary>Результат обновления веб-части.</summary>
public sealed record WebUiInstallResult(bool Success, string? Version, string? Error);

/// <summary>
/// Обновление веб-части панели (вёрстка, стили, скрипты) отдельно от
/// исполняемого файла. Архив скачивается с сайта (dayzpulse.ru) и
/// распаковывается в каталог данных — туда не нужны права администратора,
/// и службу останавливать не требуется: ASP.NET отдаёт статику с диска.
///
/// Ожидаемая раскладка архива: содержимое папки wwwroot в корне
/// (index.html, assets/…), плюс манифест:
///   /download/web-version.json
///   /download/dayzpulse-web.zip
/// </summary>
public sealed class WebUiUpdateService
{
    private const string DefaultBaseUrl = "https://dayzpulse.ru";
    private const string ManifestName = "web-version.json";
    private const string ZipName = "dayzpulse-web.zip";

    // Обязательные файлы: без них обновление не применяем, чтобы не
    // оставить панель без интерфейса.
    private static readonly string[] RequiredFiles = ["index.html", "login.html"];

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private readonly ILogger<WebUiUpdateService> _logger;
    private readonly SettingsStore _settings;

    public WebUiUpdateService(ILogger<WebUiUpdateService> logger, SettingsStore settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>Базовый адрес сайта: тот же, что и для мода панели.</summary>
    public string BaseUrl =>
        string.IsNullOrWhiteSpace(_settings.Current.Steam.PanelModBaseUrl)
            ? DefaultBaseUrl
            : _settings.Current.Steam.PanelModBaseUrl.TrimEnd('/');

    public bool Busy { get; private set; }
    public string Phase { get; private set; } = "";
    public int Percent { get; private set; }

    /// <summary>Версия установленной веб-части (пусто — встроенная).</summary>
    public string InstalledVersion => _settings.Current.WebUi.Version;

    /// <summary>
    /// Эффективная версия веб-части: версия обновлённой копии, а если её нет —
    /// версия ядра панели. Встроенная веб-часть всегда идёт в комплекте с
    /// исполняемым файлом, поэтому её версия совпадает с версией панели.
    /// </summary>
    public string EffectiveVersion
    {
        get
        {
            var installed = InstalledVersion;
            return string.IsNullOrWhiteSpace(installed)
                ? typeof(WebUiUpdateService).Assembly.GetName().Version?.ToString() ?? ""
                : installed;
        }
    }

    /// <summary>
    /// Установлена ли обновлённая веб-часть. Каталог переопределения создаётся
    /// заранее (пустым), поэтому одного его наличия мало — нужен признак
    /// установленной версии или хотя бы файл интерфейса.
    /// </summary>
    public bool HasOverride =>
        !string.IsNullOrWhiteSpace(InstalledVersion) &&
        File.Exists(Path.Combine(AppPaths.WebUiOverride, "index.html"));

    /// <summary>
    /// Отбрасывает обновлённую веб-часть, если она старее установленной панели.
    /// Иначе после обновления программы через установщик старая вёрстка из
    /// каталога данных перекрывала бы новую встроенную. Более новая веб-часть
    /// (обычная ситуация горячего обновления) сохраняется.
    ///
    /// Вызывается до настройки отдачи статики: удалять каталог после того,
    /// как провайдер файлов на него уже указывает, нельзя.
    /// </summary>
    public static void CleanupStaleOverride(SettingsStore settings, ILogger logger)
    {
        try
        {
            var installed = settings.Current.WebUi.Version;
            var coreVersion = typeof(WebUiUpdateService).Assembly.GetName().Version;

            if (coreVersion is null || string.IsNullOrWhiteSpace(installed))
            {
                return;
            }

            // Сравниваем только числовые части; нечисловую версию считаем
            // свежей, чтобы случайно не удалить рабочую веб-часть.
            if (!Version.TryParse(NormalizeVersion(installed), out var webVersion))
            {
                return;
            }

            if (webVersion >= coreVersion)
            {
                return;
            }

            logger.LogInformation(
                "Веб-часть {Web} старее панели {Core} — возвращаемся к встроенной.",
                installed, coreVersion);

            if (Directory.Exists(AppPaths.WebUiOverride))
            {
                Directory.Delete(AppPaths.WebUiOverride, recursive: true);
            }

            settings.Update(s =>
            {
                s.WebUi.Version = "";
                s.WebUi.UpdatedAt = null;
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось проверить устаревшую веб-часть.");
        }
    }

    /// <summary>Дополняет «2.0.8» до «2.0.8.0», чтобы Version.TryParse сработал.</summary>
    private static string NormalizeVersion(string value)
    {
        var parts = value.Split('.');
        return parts.Length switch
        {
            1 => value + ".0.0.0",
            2 => value + ".0.0",
            3 => value + ".0",
            _ => value
        };
    }

    /// <summary>Тянет манифест веб-части и сравнивает с установленной версией.</summary>
    public async Task<WebUiManifest?> FetchManifestAsync(CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"{BaseUrl}/download/{ManifestName}", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var updated = root.TryGetProperty("updated", out var u) ? u.GetString() ?? "" : "";
            var size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            var sha = root.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
            var installed = InstalledVersion;

            // Локальная версия — версия обновлённой копии либо встроенной.
            var local = EffectiveVersion;

            return new WebUiManifest(
                version,
                updated,
                size,
                sha,
                installed,
                UpToDate: !IsNewer(version, local));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось получить манифест веб-части.");
            return null;
        }
    }

    /// <summary>
    /// Новее ли удалённая версия локальной. Сравниваем числовые части, а
    /// неизвестную/нечисловую считаем «не новее» — лучше не предлагать
    /// обновление, чем предложить откат на старую веб-часть.
    /// </summary>
    private static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(local))
        {
            return true;
        }

        if (Version.TryParse(NormalizeVersion(remote), out var remoteVersion) &&
            Version.TryParse(NormalizeVersion(local), out var localVersion))
        {
            return remoteVersion > localVersion;
        }

        // Нечисловые версии: считаем обновлением только явное различие строк.
        return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Скачивает архив веб-части, проверяет его и распаковывает в каталог
    /// данных. Распаковка идёт в подкаталог с версией, а рабочий каталог
    /// переключается в конце — если что-то пойдёт не так, панель не пострадает.
    /// </summary>
    public async Task<WebUiInstallResult> InstallAsync(CancellationToken ct)
    {
        if (Busy)
        {
            return new WebUiInstallResult(false, null, "Обновление уже идёт.");
        }

        Busy = true;
        Percent = 0;
        Phase = "Подготовка";

        var tmp = Path.Combine(AppPaths.TempDirectory, "webui-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tmp);
            var zipPath = Path.Combine(tmp, ZipName);

            Phase = "Скачивание веб-части";
            using (var response = await Http.GetAsync($"{BaseUrl}/download/{ZipName}", ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zipPath);
                await response.Content.CopyToAsync(file, ct);
            }

            Percent = 40;
            Phase = "Проверка архива";

            var manifest = await FetchManifestAsync(ct);

            // Сверяем контрольную сумму, если сайт её отдал.
            if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var actual = await Sha256Async(zipPath, ct);
                if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new WebUiInstallResult(false, null,
                        "Контрольная сумма архива не совпала. Загрузка повреждена — попробуйте ещё раз.");
                }
            }

            Percent = 60;
            Phase = "Распаковка";

            var extract = Path.Combine(tmp, "extract");
            ZipFile.ExtractToDirectory(zipPath, extract, overwriteFiles: true);

            // Архив может содержать как содержимое wwwroot в корне, так и
            // подкаталог wwwroot — поддерживаем оба варианта.
            var source = ResolveWebRoot(extract);
            if (source is null)
            {
                return new WebUiInstallResult(false, null,
                    "В архиве нет index.html — похоже, это не веб-часть панели.");
            }

            Percent = 80;
            Phase = "Установка";

            ReplaceWebRoot(source);

            var version = manifest?.Version ?? "";
            _settings.Update(s =>
            {
                s.WebUi.Version = version;
                s.WebUi.UpdatedAt = DateTimeOffset.Now;
            });

            Percent = 100;
            Phase = "Готово";
            _logger.LogInformation("Веб-часть панели обновлена до версии {Version}.", version);

            return new WebUiInstallResult(true, version, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось обновить веб-часть панели.");
            Phase = "Ошибка";
            return new WebUiInstallResult(false, null, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* занят */ }
            Busy = false;
        }
    }

    /// <summary>Удаляет обновлённую веб-часть: панель вернётся к встроенной.</summary>
    public void Reset()
    {
        try
        {
            if (Directory.Exists(AppPaths.WebUiOverride))
            {
                Directory.Delete(AppPaths.WebUiOverride, recursive: true);
            }

            _settings.Update(s =>
            {
                s.WebUi.Version = "";
                s.WebUi.UpdatedAt = null;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить обновлённую веб-часть.");
        }
    }

    /// <summary>Ищет корень веб-части в распакованном архиве.</summary>
    private static string? ResolveWebRoot(string extract)
    {
        if (RequiredFiles.All(f => File.Exists(Path.Combine(extract, f))))
        {
            return extract;
        }

        // Иногда архивируют папку целиком (wwwroot/…): берём подкаталог с index.html.
        foreach (var dir in Directory.EnumerateDirectories(extract))
        {
            if (RequiredFiles.All(f => File.Exists(Path.Combine(dir, f))))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// Заменяет рабочий каталог веб-части. Действует через промежуточный
    /// каталог, чтобы не оставить панель без интерфейса при сбое.
    /// </summary>
    private static void ReplaceWebRoot(string source)
    {
        var target = AppPaths.WebUiOverride;
        var incoming = target + ".incoming";
        var previous = target + ".previous";

        if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
        CopyDirectory(source, incoming);

        // Проверяем, что скопировалось всё нужное, прежде чем подменять.
        if (!RequiredFiles.All(f => File.Exists(Path.Combine(incoming, f))))
        {
            Directory.Delete(incoming, true);
            throw new InvalidOperationException("Скопированная веб-часть неполна.");
        }

        if (Directory.Exists(previous)) Directory.Delete(previous, true);
        if (Directory.Exists(target)) Directory.Move(target, previous);
        Directory.Move(incoming, target);

        try { if (Directory.Exists(previous)) Directory.Delete(previous, true); } catch { /* занят */ }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
