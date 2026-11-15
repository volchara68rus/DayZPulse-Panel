using System.IO.Compression;
using System.Text.Json;
using DayZPulse.Core;
using DayZPulse.Infrastructure;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>Сведения о раздаче мода на сайте.</summary>
public sealed record PanelModManifest(
    string Version,
    string Updated,
    long Size,
    string Sha256,
    bool UpToDate,
    string? InstalledVersion);

/// <summary>Результат установки/обновления мода.</summary>
public sealed record PanelModInstallResult(bool Success, string? Version, string? Error);

/// <summary>
/// Скачивание и обновление собственного мода DayZPulse с официального сайта
/// (dayzpulse.ru): манифест <c>/download/mod-version.json</c>, архив
/// <c>/download/dayzpulse-mod.zip</c>. Панель сравнивает версии и раскладывает
/// папку <c>@DayZPulse</c> рядом с сервером, копирует ключи и пишет маркер версии.
/// </summary>
public sealed class PanelModService
{
    private const string DefaultBaseUrl = "https://dayzpulse.ru";
    private const string ManifestName = "mod-version.json";
    private const string ZipName = "dayzpulse-mod.zip";

    private static readonly string[] ModFolders = ["@DayZPulse"];

    // Папки прежних сборок: удаляем, чтобы не было дублей и конфликта классов.
    private static readonly string[] LegacyFolders =
        ["@PlayerTracker", "@AntiCheat", "@DayZPulseCore", "@DayZPulseAntiCheat"];

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private readonly ILogger<PanelModService> _logger;
    private readonly SettingsStore _settings;

    public PanelModService(ILogger<PanelModService> logger, SettingsStore settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>Базовый адрес сайта (можно переопределить в настройках).</summary>
    public string BaseUrl =>
        string.IsNullOrWhiteSpace(_settings.Current.Steam.PanelModBaseUrl)
            ? DefaultBaseUrl
            : _settings.Current.Steam.PanelModBaseUrl.TrimEnd('/');

    /// <summary>Идёт ли установка прямо сейчас.</summary>
    public bool Busy { get; private set; }

    /// <summary>Текущая фаза (для интерфейса).</summary>
    public string Phase { get; private set; } = "";

    /// <summary>Прогресс в процентах.</summary>
    public int Percent { get; private set; }

    // --------------------------------------------------------------- версии

    /// <summary>Тянет манифест с сайта и сравнивает с установленной версией.</summary>
    public async Task<PanelModManifest?> FetchManifestAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}/download/{ManifestName}";
            var json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var updated = root.TryGetProperty("updated", out var u) ? u.GetString() ?? "" : "";
            var size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            var sha = root.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
            var installed = _settings.Current.Steam.PanelModVersion;

            return new PanelModManifest(
                version,
                updated,
                size,
                sha,
                InstalledVersion: installed,
                UpToDate: !string.IsNullOrEmpty(installed) &&
                          string.Equals(installed, version, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось получить манифест мода панели.");
            return null;
        }
    }

    /// <summary>Версия мода, установленного в конкретном сервере (из маркера).</summary>
    public static string? InstalledVersion(ServerProfile profile)
    {
        try
        {
            var path = MarkerPath(profile);
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Установлен ли мод в сервере (папка @DayZPulse с аддонами).</summary>
    public static bool IsInstalled(ServerProfile profile)
    {
        try
        {
            var root = Path.Combine(ServerPaths.WorkingDirectory(profile.Server), "@DayZPulse");
            return Directory.Exists(root) &&
                   Directory.EnumerateFiles(root, "*.pbo", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------------- установка

    /// <summary>Скачивает и раскладывает мод в конкретный сервер.</summary>
    public async Task<PanelModInstallResult> InstallAsync(ServerProfile profile, CancellationToken ct)
    {
        if (Busy)
        {
            return new PanelModInstallResult(false, null, "Установка уже идёт.");
        }

        Busy = true;
        Percent = 0;
        Phase = "Подготовка";

        var tmp = Path.Combine(Path.GetTempPath(), "dayzpulse-mod-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tmp);
            var zipPath = Path.Combine(tmp, ZipName);

            Phase = "Скачивание мода";
            using (var response = await Http.GetAsync($"{BaseUrl}/download/{ZipName}", ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zipPath);
                await response.Content.CopyToAsync(file, ct);
            }

            Percent = 60;
            Phase = "Распаковка";

            var extract = Path.Combine(tmp, "extract");
            ZipFile.ExtractToDirectory(zipPath, extract, overwriteFiles: true);

            Percent = 80;
            Phase = "Раскладка по серверу";

            var deployed = Deploy(profile, extract);
            if (deployed == 0)
            {
                return new PanelModInstallResult(false, null, "В архиве не найдена папка @DayZPulse.");
            }

            var manifest = await FetchManifestAsync(ct);
            var version = manifest?.Version ?? "";

            WriteMarker(profile, version);

            // Глобальную версию обновляем, если мод установили как минимум один раз.
            _settings.Update(s =>
            {
                s.Steam.PanelModVersion = version;
                s.Steam.PanelModUpdatedAt = DateTimeOffset.Now;
            });

            Percent = 100;
            Phase = "Готово";
            _logger.LogInformation("[{Server}] Мод DayZPulse установлен (версия {Version}).", profile.Name, version);

            return new PanelModInstallResult(true, version, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось установить мод DayZPulse.");
            Phase = "Ошибка";
            return new PanelModInstallResult(false, null, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* занят */ }
            Busy = false;
        }
    }

    /// <summary>Раскладывает содержимое архива в папку сервера.</summary>
    private int Deploy(ServerProfile profile, string extractRoot)
    {
        var target = ServerPaths.WorkingDirectory(profile.Server);

        // Убираем устаревшие папки прежних сборок.
        foreach (var legacy in LegacyFolders)
        {
            var path = Path.Combine(target, legacy);
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); } catch { /* занят */ }
            }
        }

        var deployed = 0;
        var keys = Path.Combine(target, "keys");
        Directory.CreateDirectory(keys);

        foreach (var folder in ModFolders)
        {
            var source = Path.Combine(extractRoot, folder);
            if (!Directory.Exists(source))
            {
                source = Directory.EnumerateDirectories(extractRoot, folder, SearchOption.AllDirectories)
                    .FirstOrDefault() ?? "";
            }

            if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
            {
                continue;
            }

            SyncDirectory(source, Path.Combine(target, folder));

            // Ключи подписи (.bikey) — в keys сервера.
            foreach (var key in Directory.GetFiles(source, "*.bikey", SearchOption.AllDirectories))
            {
                try { File.Copy(key, Path.Combine(keys, Path.GetFileName(key)), overwrite: true); } catch { /* занят */ }
            }

            deployed++;
        }

        return deployed;
    }

    private static void SyncDirectory(string source, string target)
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

    private static string MarkerPath(ServerProfile profile) =>
        Path.Combine(ServerPaths.WorkingDirectory(profile.Server), "dayzpulse-mod.json");

    private static void WriteMarker(ServerProfile profile, string version)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new { version, updatedAt = DateTimeOffset.Now },
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(MarkerPath(profile), json, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Маркер не критичен.
        }
    }
}
