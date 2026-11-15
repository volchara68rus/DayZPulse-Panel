using DayZPulse.Models;

namespace DayZPulse.Core;

/// <summary>
/// Определяет каталоги сервера DayZ с учётом базовой папки и настройки
/// «разрешить каталоги вне базовой папки».
/// </summary>
public static class ServerPaths
{
    public static string BaseDirectory(ServerSettings options)
    {
        if (!string.IsNullOrWhiteSpace(options.BaseDirectory))
        {
            try
            {
                return Path.GetFullPath(options.BaseDirectory);
            }
            catch
            {
                // Некорректный путь — используем значение по умолчанию.
            }
        }

        return Path.GetFullPath(ServerSettings.DefaultBaseDirectory);
    }

    /// <summary>Каталоги, внутри которых разрешено читать и писать файлы сервера.</summary>
    public static IReadOnlyList<string> AllowedRoots(ServerSettings options, string dataRoot)
    {
        var roots = new List<string> { Path.GetFullPath(dataRoot) };

        if (options.AllowDirectoryOutsideBase)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady)
                    {
                        roots.Add(drive.RootDirectory.FullName);
                    }
                }
                catch
                {
                    // Некоторые тома недоступны.
                }
            }
        }
        else
        {
            var workingDirectory = WorkingDirectory(options);
            roots.Add(workingDirectory);
            roots.Add(BaseDirectory(options));
        }

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string WorkingDirectory(ServerSettings options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            return Path.GetFullPath(options.WorkingDirectory);
        }

        return BaseDirectory(options);
    }

    /// <summary>
    /// Данные панели, относящиеся к конкретному серверу: состояние трекера,
    /// расписание, резервные копии. Лежат в папке сервера, в подкаталоге
    /// <c>dayzpulse</c>, чтобы переезд сервера переносил и их.
    /// </summary>
    public static string DataDirectory(ServerSettings options)
    {
        var root = BaseDirectory(options);
        return Path.Combine(root, "dayzpulse");
    }

    public static string BackupsDirectory(ServerSettings options) =>
        Path.Combine(DataDirectory(options), "backups");

    public static string ServerLogsDirectory(ServerSettings options) =>
        Path.Combine(DataDirectory(options), "logs");

    public static string StateFile(ServerSettings options, string fileName) =>
        Path.Combine(DataDirectory(options), fileName);

    /// <summary>Создаёт служебные каталоги сервера.</summary>
    public static void EnsureDataDirectories(ServerSettings options)
    {
        foreach (var dir in new[] { DataDirectory(options), BackupsDirectory(options), ServerLogsDirectory(options) })
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // Каталог может быть недоступен — панель сообщит при записи.
            }
        }
    }

    /// <summary>
    /// Абсолютный путь к конфигу сервера. Файл ищется рядом с исполняемым
    /// файлом (как обычно в DayZ), затем в рабочем каталоге.
    /// </summary>
    public static string ConfigFile(ServerSettings options)
    {
        var name = string.IsNullOrWhiteSpace(options.ConfigFile) ? "serverDZ.cfg" : options.ConfigFile.Trim();

        if (Path.IsPathRooted(name))
        {
            return Path.GetFullPath(name);
        }

        try
        {
            var exe = Executable(options);
            var exeDirectory = Path.GetDirectoryName(exe);
            var nearExe = string.IsNullOrEmpty(exeDirectory) ? null : Path.Combine(exeDirectory, name);
            if (nearExe is not null && File.Exists(nearExe))
            {
                return nearExe;
            }
        }
        catch
        {
            // exe может быть не настроен.
        }

        var inWorking = Path.Combine(WorkingDirectory(options), name);
        return File.Exists(inWorking) ? inWorking : inWorking;
    }

    /// <summary>Абсолютный путь к исполняемому файлу сервера.</summary>
    public static string Executable(ServerSettings options)
    {        var workingDirectory = WorkingDirectory(options);

        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return Path.IsPathRooted(options.ExecutablePath)
                ? Path.GetFullPath(options.ExecutablePath)
                : Path.GetFullPath(Path.Combine(workingDirectory, options.ExecutablePath));
        }

        var name = string.IsNullOrWhiteSpace(options.ExecutableName)
            ? "DayZServer_x64.exe"
            : options.ExecutableName.Trim();

        // Если каталог с exe задан как рабочий, ищем файл в нём.
        var candidate = Path.Combine(workingDirectory, name);
        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        // Иначе пробуем подкаталог с типовым именем сборки.
        foreach (var sub in new[] { "Server", "dayzserver", "bin" })
        {
            var nested = Path.Combine(workingDirectory, sub, name);
            if (File.Exists(nested))
            {
                return Path.GetFullPath(nested);
            }
        }

        return Path.GetFullPath(candidate);
    }
}
