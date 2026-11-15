using System.IO.Compression;
using DayZPulse.Core;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>
/// Архивация логов сервера: упаковывает существующие логи в zip-архив,
/// переносит его в каталог резервных копий логов и удаляет исходники,
/// чтобы каждый запуск сервера начинался с пустых (чистых) логов.
/// </summary>
public static class ServerLogArchiver
{
    /// <summary>
    /// Упаковывает все текущие логи сервера в один zip и очищает их.
    /// Возвращает результат (имя архива, сколько файлов, сколько байт).
    /// Если логов нет — успех без архива.
    /// </summary>
    public static LogArchiveResult Archive(ServerProfile profile)
    {
        var source = ServerLogService.ProfilesDirectory(profile);
        var logsBackupDir = Path.Combine(ServerPaths.BackupsDirectory(profile.Server), "logs");

        if (!Directory.Exists(source))
        {
            return LogArchiveResult.Nothing("Каталог профилей не найден.");
        }

        // Только верхнеуровневые логи: RPT, ADM, script_*, error_*, *.log.
        var files = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly)
            .Where(ServerLogService.IsLogFile)
            .ToArray();

        if (files.Length == 0)
        {
            return LogArchiveResult.Nothing("Логов нет.");
        }

        try
        {
            Directory.CreateDirectory(logsBackupDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var fileName = $"logs-{stamp}.zip";
            var target = Path.Combine(logsBackupDir, fileName);

            // Если запуск дважды в одну секунду — добавим хвост, чтобы не перезаписать.
            var unique = target;
            var counter = 1;
            while (File.Exists(unique))
            {
                unique = Path.Combine(logsBackupDir, $"logs-{stamp}-{counter++}.zip");
            }

            long totalBytes = 0;

            using (var zipStream = new FileStream(unique, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    try
                    {
                        // Файл может быть занят — копируем через поток с общим доступом.
                        var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Fastest);
                        entry.LastWriteTime = File.GetLastWriteTime(file);

                        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var output = entry.Open();
                        input.CopyTo(output);

                        totalBytes += input.Length;
                    }
                    catch (IOException)
                    {
                        // Пропускаем занятый файл.
                    }
                }
            }

            // После успешной упаковки удаляем исходники — новые логи будут чистыми.
            var removed = 0;
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    // Файл занят — оставим, он уйдёт в следующий архив.
                }
                catch (UnauthorizedAccessException)
                {
                    // Нет прав — пропускаем.
                }
            }

            var info = new FileInfo(unique);
            TrimArchives(logsBackupDir, profile.Backups.Logs.RetentionCount);

            return new LogArchiveResult(true, info.Name, files.Length, removed, totalBytes,
                $"Логи упакованы в {info.Name} ({files.Length} файл(ов)).");
        }
        catch (Exception ex)
        {
            return LogArchiveResult.Nothing("Не удалось архивировать логи: " + ex.Message);
        }
    }

    /// <summary>Оставляет только последние N архивов логов.</summary>
    private static void TrimArchives(string directory, int retention)
    {
        if (retention <= 0)
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.zip")
                         .Select(p => new FileInfo(p))
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(retention))
            {
                try { file.Delete(); } catch { /* занят или нет прав */ }
            }
        }
        catch
        {
            // Каталог недоступен — не критично.
        }
    }
}

/// <summary>Результат архивации логов при запуске сервера.</summary>
public sealed record LogArchiveResult(
    bool Success,
    string? FileName,
    int FileCount,
    int RemovedCount,
    long TotalBytes,
    string Message)
{
    public static LogArchiveResult Nothing(string message) => new(false, null, 0, 0, 0, message);

    /// <summary>Была ли реально создана копия.</summary>
    public bool HasArchive => Success && !string.IsNullOrEmpty(FileName);
}
