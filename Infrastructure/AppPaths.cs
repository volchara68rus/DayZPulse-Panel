namespace DayZPulse.Infrastructure;

/// <summary>
/// Пути самой панели. Всё, что относится к панели — настройки, аудит,
/// журналы, временные файлы — лежит рядом с её исполняемым файлом,
/// чтобы переустановка и перенос панели были предсказуемыми.
/// Данные игровых серверов хранятся отдельно, в папке сервера.
/// </summary>
public static class AppPaths
{
    private static string? _baseDirectory;

    /// <summary>Каталог панели: рядом с исполняемым файлом.</summary>
    public static string BaseDirectory => _baseDirectory ??= ResolveBaseDirectory();

    /// <summary>Данные панели: подкаталог <c>data</c> рядом с панелью.</summary>
    public static string DataRoot => Path.Combine(BaseDirectory, "data");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string AuditFile => Path.Combine(DataRoot, "audit.log");
    public static string LogsDirectory => Path.Combine(DataRoot, "logs");
    public static string TempDirectory => Path.Combine(DataRoot, "temp");

    /// <summary>
    /// Каталог SteamCMD: рядом с панелью, в подкаталоге <c>steamcmd</c>.
    /// Клиент общий для панели и не зависит от папки конкретного сервера.
    /// </summary>
    public static string SteamCmdDirectory => Path.Combine(BaseDirectory, "steamcmd");

    /// <summary>Веб-интерфейс панели (встроен рядом с исполняемым файлом).</summary>
    public static string WebRoot => Path.Combine(BaseDirectory, "wwwroot");

    public static void Initialize(string? overrideBaseDirectory = null, bool ensure = true)
    {
        if (!string.IsNullOrWhiteSpace(overrideBaseDirectory))
        {
            _baseDirectory = Path.GetFullPath(overrideBaseDirectory!);
        }
        else
        {
            _baseDirectory ??= ResolveBaseDirectory();
        }

        if (!ensure)
        {
            return;
        }

        foreach (var dir in new[] { DataRoot, LogsDirectory, TempDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// Каталог панели: явная настройка (<c>--data-dir</c> или переменная окружения),
    /// иначе — папка исполняемого файла. Если она недоступна для записи
    /// (например, программа установлена в защищённый каталог), берём LocalAppData.
    /// </summary>
    private static string ResolveBaseDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DAYZPULSE_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv!);
        }

        var nearExe = AppContext.BaseDirectory;

        if (IsWritable(nearExe))
        {
            return nearExe;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZPulse");

        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():n}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Переносит настройки из прежнего расположения (%ProgramData% и подобных)
    /// к панели, если там уже есть файл, а рядом с панелью его ещё нет.
    /// </summary>
    public static void MigrateLegacySettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return;
            }

            foreach (var legacy in LegacyCandidates())
            {
                if (!File.Exists(legacy))
                {
                    continue;
                }

                Directory.CreateDirectory(DataRoot);
                File.Copy(legacy, SettingsFile, overwrite: false);

                // Журналы и аудит переносим рядом.
                var legacyRoot = Path.GetDirectoryName(legacy)!;
                CopyIfExists(Path.Combine(legacyRoot, "audit.log"), AuditFile);

                var legacyLogs = Path.Combine(legacyRoot, "logs");
                if (Directory.Exists(legacyLogs))
                {
                    Directory.CreateDirectory(LogsDirectory);
                    foreach (var file in Directory.EnumerateFiles(legacyLogs, "*.log"))
                    {
                        CopyIfExists(file, Path.Combine(LogsDirectory, Path.GetFileName(file)));
                    }
                }

                return;
            }
        }
        catch
        {
            // Перенос не критичен: панель просто создаст настройки заново.
        }
    }

    private static IEnumerable<string> LegacyCandidates()
    {
        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DayZPulse");
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZPulse");
        var fromEnv = Environment.GetEnvironmentVariable("DAYZPULSE_DATA");

        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            yield return Path.Combine(fromEnv!, "settings.json");
        }

        yield return Path.Combine(programData, "settings.json");
        yield return Path.Combine(localAppData, "settings.json");
    }

    private static void CopyIfExists(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to))
            {
                File.Copy(from, to);
            }
        }
        catch
        {
            // Игнорируем: часть файлов может быть занята.
        }
    }
}
