using System.Text.RegularExpressions;
using DayZPulse.Core;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Steam;

namespace DayZPulse.Tracker;

/// <summary>Результат операции с модом для ответа панели.</summary>
public sealed record ModOperationResult(bool Success, string? Error = null);

/// <summary>
/// Управляет модами сервера: скачивание из Steam Workshop, подключение
/// локальных папок, обновление и удаление. Папки модов кладём рядом с сервером
/// (например <c>C:\Server\@CF</c>), а строку <c>-mod=</c> пересобираем из списка.
/// </summary>
public sealed class ModManager
{
    private readonly ILogger<ModManager> _logger;
    private readonly SteamCmdService _steam;
    private readonly SettingsStore _settings;

    public ModManager(ILogger<ModManager> logger, SteamCmdService steam, SettingsStore settings)
    {
        _logger = logger;
        _steam = steam;
        _settings = settings;
    }

    /// <summary>Идёт ли сейчас загрузка мода (одна одновременно).</summary>
    public bool Busy { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Текущая фаза загрузки (для панели).</summary>
    public string Phase { get; private set; } = "";

    /// <summary>Прогресс загрузки в процентах.</summary>
    public int Percent { get; private set; }

    // ------------------------------------------------------------- разбор ввода

    /// <summary>
    /// Достаёт Workshop ID из ссылки или строки. Поддерживает:
    /// <c>https://steamcommunity.com/sharedfiles/filedetails/?id=12345</c>,
    /// <c>?id=12345</c>, просто <c>12345</c>.
    /// </summary>
    public static string? ExtractWorkshopId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();

        // Голый числовой id.
        if (text.All(char.IsDigit) && text.Length is >= 5 and <= 12)
        {
            return text;
        }

        var match = Regex.Match(text, @"[?&]id=(\d{5,12})", RegexOptions.IgnoreCase)
            ?? Regex.Match(text, @"(\d{8,12})");

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Целевая папка мода рядом с сервером.</summary>
    public static string ModFolder(ServerSettings server, string folderName) =>
        Path.Combine(ServerPaths.WorkingDirectory(server), folderName);

    // --------------------------------------------------------------- установка

    /// <summary>
    /// Скачивает мод из Workshop и раскладывает в папку <c>@{имя}</c>.
    /// Имя папки берём из имени мода или из ID.
    /// </summary>
    public async Task<ModOperationResult> InstallFromWorkshopAsync(ServerProfile profile, ServerMod mod,
        CancellationToken ct)
    {
        if (Busy)
        {
            return new ModOperationResult(false, "Другая загрузка мода уже идёт.");
        }

        Busy = true;
        LastError = null;
        Percent = 0;
        Phase = "Подготовка";

        try
        {
            var username = _settings.Current.Steam.Username;
            var folder = string.IsNullOrWhiteSpace(mod.Folder) ? "@" + Sanitize(mod.Name, mod.WorkshopId) : mod.Folder;
            var target = ModFolder(profile.Server, folder);

            _logger.LogInformation("Скачивание мода {Id} в {Target}", mod.WorkshopId, target);

            var progress = new Progress<SteamDownloadProgress>(p =>
            {
                Percent = p.Percent;
                Phase = p.Phase;
            });

            var result = await _steam.DownloadWorkshopAsync(
                AppPaths.SteamCmdDirectory, SteamCmdService.DayZGameAppId, mod.WorkshopId, target,
                username, progress, ct);

            if (!result.Success)
            {
                LastError = result.Error;
                return new ModOperationResult(false, result.Error);
            }

            mod.Folder = folder;
            mod.InstalledAt ??= DateTimeOffset.Now;
            mod.UpdatedAt = DateTimeOffset.Now;

            // Папку-мод мог найти как @X, но если имя мода есть в нижней папке — уточним.
            mod.Folder = NormalizeFolder(target) ?? folder;

            return new ModOperationResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось установить мод {Id}.", mod.WorkshopId);
            LastError = ex.Message;
            return new ModOperationResult(false, ex.Message);
        }
        finally
        {
            Busy = false;
            if (Percent >= 100) Phase = "Готово";
        }
    }

    /// <summary>Подключает локальную папку как мод (копирует в папку сервера).</summary>
    public ModOperationResult InstallLocal(ServerProfile profile, ServerMod mod, string sourcePath)
    {
        try
        {
            if (!Directory.Exists(sourcePath))
            {
                return new ModOperationResult(false, "Папка не найдена.");
            }

            var folderName = Path.GetFileName(sourcePath.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return new ModOperationResult(false, "Не удалось определить имя папки мода.");
            }

            // Имя папки мода: @Name (если ещё не с @).
            if (!folderName.StartsWith('@'))
            {
                folderName = "@" + folderName;
            }

            var target = ModFolder(profile.Server, folderName);

            // Если источник уже внутри сервера — просто подключаем без копирования.
            var sourceFull = Path.GetFullPath(sourcePath);
            if (sourceFull.StartsWith(Path.GetFullPath(ServerPaths.WorkingDirectory(profile.Server)),
                    StringComparison.OrdinalIgnoreCase))
            {
                mod.Folder = folderName;
                mod.LocalPath = sourceFull;
                mod.InstalledAt ??= DateTimeOffset.Now;
                return new ModOperationResult(true);
            }

            CopyDirectory(sourceFull, target);
            mod.Folder = folderName;
            mod.LocalPath = sourceFull;
            mod.InstalledAt ??= DateTimeOffset.Now;
            mod.UpdatedAt = DateTimeOffset.Now;
            return new ModOperationResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось подключить локальный мод {Path}.", sourcePath);
            return new ModOperationResult(false, ex.Message);
        }
    }

    /// <summary>Удаляет папку мода с диска.</summary>
    public ModOperationResult RemoveFolder(ServerProfile profile, ServerMod mod)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mod.Folder))
            {
                return new ModOperationResult(true);
            }

            var path = ModFolder(profile.Server, mod.Folder);

            // Защита: удаляем только внутри рабочего каталога сервера.
            var working = Path.GetFullPath(ServerPaths.WorkingDirectory(profile.Server));
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(working, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
            {
                return new ModOperationResult(true);
            }

            Directory.Delete(full, recursive: true);
            return new ModOperationResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить папку мода {Folder}.", mod.Folder);
            return new ModOperationResult(false, ex.Message);
        }
    }

    // ------------------------------------------------------------- -mod=

    /// <summary>
    /// Пересобирает строку <c>-mod=</c> из включённых модов и записывает её
    /// в настройки сервера. Наш собственный мод @DayZPulseCore сюда не входит —
    /// он управляется отдельной карточкой.
    /// </summary>
    public void RebuildLaunchMods(string serverId)
    {
        _settings.UpdateServer(serverId, profile =>
        {
            var folders = profile.ModList.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Folder)).ToArray();

            // Клиентские моды — в -mod=, серверные (ServerOnly) — в -serverMod=.
            var client = folders.Where(m => !m.ServerOnly).Select(m => m.Folder!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var serverOnly = folders.Where(m => m.ServerOnly).Select(m => m.Folder!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Наш мод @DayZPulse — серверный: живёт только в -serverMod=.
            if (PanelModService.IsInstalled(profile) &&
                !serverOnly.Contains("@" + ModStatusService.PanelMod, StringComparer.OrdinalIgnoreCase))
            {
                serverOnly.Insert(0, "@" + ModStatusService.PanelMod);
            }

            profile.Server.Mods = string.Join(";", client);
            profile.Server.ServerMods = string.Join(";", serverOnly);
        });
    }

    // --------------------------------------------------------------- утилиты

    /// <summary>
    /// Если SteamCMD положил содержимое не в саму папку, а во вложенную
    /// с именем мода — возвращаем ожидаемое имя (или null, чтобы не менять).
    /// </summary>
    private static string? NormalizeFolder(string target) =>
        Directory.Exists(target) ? Path.GetFileName(target.TrimEnd('\\', '/')) : null;

    private static string Sanitize(string? name, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(name) ? fallback : name!;
        var builder = new System.Text.StringBuilder();

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
            {
                builder.Append(ch);
            }
        }

        var slug = builder.ToString();
        return slug.Length is 0 or > 40 ? fallback : slug;
    }

    private static void CopyDirectory(string source, string target)
    {
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = file.Replace(source, target);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
