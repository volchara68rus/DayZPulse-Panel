using DayZPulse.Core;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>Файл лога сервера для списка в интерфейсе.</summary>
public sealed record ServerLogFile(
    string Name,
    string Path,
    string Kind,
    long SizeBytes,
    DateTimeOffset Modified);

/// <summary>
/// Читает логи сервера DayZ из папки профилей: <c>.RPT</c> (основной лог),
/// <c>.ADM</c> (админ-лог), <c>script_*.log</c> и <c>error_*.log</c>.
/// Логи переживают перезапуск, поэтому панель читает их напрямую с диска.
/// </summary>
public sealed class ServerLogService
{
    private readonly ILogger<ServerLogService> _logger;

    public ServerLogService(ILogger<ServerLogService> logger)
    {
        _logger = logger;
        UseLogger(logger);
    }

    /// <summary>Каталог профилей сервера (где лежат логи).</summary>
    public static string ProfilesDirectory(ServerProfile profile)
    {
        var working = ServerPaths.WorkingDirectory(profile.Server);
        var profiles = profile.Server.ProfilesDirectory;

        return string.IsNullOrWhiteSpace(profiles)
            ? Path.Combine(working, "profiles")
            : Path.IsPathRooted(profiles) ? profiles : Path.Combine(working, profiles);
    }

    /// <summary>Список логов: свежие сверху.</summary>
    public IReadOnlyList<ServerLogFile> List(ServerProfile profile)
    {
        try
        {
            var directory = ProfilesDirectory(profile);
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsLog)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new ServerLogFile(
                    f.Name,
                    f.FullName,
                    Classify(f.Name),
                    f.Length,
                    new DateTimeOffset(f.LastWriteTimeUtc)))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось перечислить логи сервера.");
            return [];
        }
    }

    /// <summary>
    /// Читает последние строки лога. <paramref name="tail"/> — сколько строк,
    /// <paramref name="filter"/> — подстрока для поиска.
    /// </summary>
    public IReadOnlyList<string> Read(ServerProfile profile, string fileName, int tail, string? filter)
    {
        try
        {
            var path = Resolve(profile, fileName);
            if (path is null || !File.Exists(path))
            {
                return [];
            }

            tail = Math.Clamp(tail, 50, 5000);
            var lines = new LinkedList<string>();

            // Читаем файл с конца, чтобы не грузить всё в память.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(filter) &&
                    line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                lines.AddLast(line);
                if (lines.Count > tail)
                {
                    lines.RemoveFirst();
                }
            }

            return lines.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось прочитать лог {File}.", fileName);
            return [];
        }
    }

    /// <summary>Полный путь к логу, если имя корректно и лежит внутри профилей.</summary>
    public string? Resolve(ServerProfile profile, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0)
        {
            return null;
        }

        var directory = Path.GetFullPath(ProfilesDirectory(profile));
        var full = Path.GetFullPath(Path.Combine(directory, fileName));

        // Защита: только внутри каталога профилей.
        return full.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Удаляет лог (одиночный или все, старше дней).</summary>
    public int Delete(ServerProfile profile, string? fileName, int olderThanDays)
    {
        var removed = 0;

        try
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var path = Resolve(profile, fileName);
                if (path is not null && File.Exists(path))
                {
                    File.Delete(path);
                    return 1;
                }

                return 0;
            }

            var cutoff = DateTime.Now.AddDays(-Math.Max(0, olderThanDays));

            foreach (var file in List(profile))
            {
                if (file.Modified.LocalDateTime < cutoff)
                {
                    try { File.Delete(file.Path); removed++; } catch { /* занят */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось удалить логи.");
        }

        return removed;
    }

    private static bool IsLog(string path)
    {
        return IsLogFile(path);
    }

    // -------------------------------------------------- чтение архивов логов

    /// <summary>Содержимое одного файла внутри zip-архива логов (null — нет такого).</summary>
    public static IReadOnlyList<string>? ListArchiveEntries(string archivePath)
    {
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

            return archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => e.FullName.Replace('\\', '/'))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _archiveLogger?.LogDebug(ex, "Не удалось открыть архив логов {File}.", archivePath);
            return null;
        }
    }

    /// <summary>
    /// Читает строки файла прямо из zip-архива логов — без распаковки на диск.
    /// <paramref name="tail"/> — сколько последних строк, <paramref name="filter"/> — подстрока.
    /// </summary>
    public static IReadOnlyList<string> ReadArchive(
        string archivePath, string entryName, int tail, string? filter)
    {
        var lines = new LinkedList<string>();

        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

            var entry = archive.GetEntry(entryName)
                ?? archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return [];
            }

            tail = Math.Clamp(tail, 50, 20000);

            using var reader = new StreamReader(entry.Open());
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(filter) &&
                    line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                lines.AddLast(line);
                if (lines.Count > tail)
                {
                    lines.RemoveFirst();
                }
            }
        }
        catch (Exception ex)
        {
            _archiveLogger?.LogDebug(ex, "Не удалось прочитать {Entry} из архива {File}.", entryName, archivePath);
            return [];
        }

        return lines.ToArray();
    }

    private static ILogger? _archiveLogger;

    /// <summary>Логгер для операций с архивами (задаётся один раз при старте).</summary>
    public static void UseLogger(ILogger logger) => _archiveLogger ??= logger;

    /// <summary>Относится ли файл к логам сервера (RPT, ADM, script_*, error_*).</summary>
    public static bool IsLogFile(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);

        return ext.Equals(".RPT", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ADM", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("script_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("error_", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Тип лога для значка и подписи.</summary>
    public static string Classify(string name)
    {
        var lower = name.ToLowerInvariant();

        if (lower.EndsWith(".rpt")) return "rpt";
        if (lower.EndsWith(".adm")) return "adm";
        if (lower.StartsWith("script_")) return "script";
        if (lower.StartsWith("error_")) return "error";
        return "log";
    }
}
