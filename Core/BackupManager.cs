using System.IO.Compression;
using System.Text.Json;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Tracker;

namespace DayZPulse.Core;

public sealed record BackupInfo(
    string FileName,
    string FullPath,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    string? Kind);

public sealed record BackupResult(bool Success, string? FileName, long SizeBytes, TimeSpan Duration, string Message);

/// <summary>Создание, хранение и восстановление резервных копий данных одного сервера.</summary>
public sealed class BackupManager
{
    private readonly ILogger<BackupManager> _logger;
    private readonly ServerSupervisor _supervisor;
    private ServerProfile _profile;

    public BackupManager(ILogger<BackupManager> logger, ServerProfile profile, ServerSupervisor supervisor)
    {
        _logger = logger;
        _profile = profile;
        _supervisor = supervisor;
    }

    public void UpdateProfile(ServerProfile profile) => _profile = profile;

    /// <summary>Копии каждого сервера хранятся в его папке: <c>dayzpulse\backups</c>.</summary>
    public string Directory => ServerPaths.BackupsDirectory(_profile.Server);

    /// <summary>Копии логов хранятся в <c>dayzpulse\backups\logs</c>.</summary>
    public string LogsDirectory => Path.Combine(ServerPaths.BackupsDirectory(_profile.Server), "logs");

    public IReadOnlyList<BackupInfo> List()
    {
        var results = new List<BackupInfo>();

        // Основные резервные копии (без подкаталога logs).
        if (System.IO.Directory.Exists(Directory))
        {
            results.AddRange(System.IO.Directory.EnumerateFiles(Directory, "*.zip")
                .Select(path => new FileInfo(path))
                .Select(f => new BackupInfo(f.Name, f.FullName, f.Length,
                    new DateTimeOffset(f.LastWriteTimeUtc), ReadKind(f.FullName) ?? "manual")));
        }

        // Архивы логов (подкаталог logs) — показываем в общем списке.
        if (System.IO.Directory.Exists(LogsDirectory))
        {
            results.AddRange(System.IO.Directory.EnumerateFiles(LogsDirectory, "*.zip")
                .Select(path => new FileInfo(path))
                .Select(f => new BackupInfo(f.Name, f.FullName, f.Length,
                    new DateTimeOffset(f.LastWriteTimeUtc), "logs")));
        }

        return results.OrderByDescending(b => b.CreatedAt).ToArray();
    }

    /// <summary>Только основные копии (без логов) — для ротации по RetentionCount.</summary>
    private IReadOnlyList<BackupInfo> ListPrimary()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(Directory, "*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.FullName, f.Length, new DateTimeOffset(f.LastWriteTimeUtc), ReadKind(f.FullName)))
            .ToArray();
    }

    public async Task<BackupResult> CreateAsync(string kind, CancellationToken cancellationToken)
    {
        var options = _profile.Backups;
        var workingDirectory = ServerPaths.WorkingDirectory(_profile.Server);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var wasRunning = _supervisor.IsRunning;

        System.IO.Directory.CreateDirectory(Directory);
        var fileName = $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var target = Path.Combine(Directory, fileName);

        var stoppedForBackup = false;
        if (options.StopServerDuringBackup && wasRunning)
        {
            _logger.LogInformation("[{Server}] Остановка сервера на время резервного копирования.", _profile.Name);
            await _supervisor.StopAsync("резервное копирование", cancellationToken);
            stoppedForBackup = true;
        }

        try
        {
            var manifest = new Dictionary<string, object>
            {
                ["createdAt"] = DateTimeOffset.Now,
                ["kind"] = kind,
                ["server"] = _profile.Id,
                ["name"] = _profile.Name,
                ["workingDirectory"] = workingDirectory,
                ["includes"] = options.Includes
            };

            await Task.Run(() =>
            {
                using var zipStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

                var level = options.CompressionLevel.ToLowerInvariant() switch
                {
                    "smallestsize" or "optimal" => CompressionLevel.Optimal,
                    "nocompression" or "none" => CompressionLevel.NoCompression,
                    _ => CompressionLevel.Fastest
                };

                foreach (var entry in options.Includes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resolved = ResolveInclude(entry, workingDirectory);
                    if (resolved is null)
                    {
                        continue;
                    }

                    if (System.IO.Directory.Exists(resolved))
                    {
                        AddDirectory(archive, resolved, Path.GetFileName(resolved), options.ExcludePatterns, level, cancellationToken);
                    }
                    else if (File.Exists(resolved))
                    {
                        AddFile(archive, resolved, Path.GetFileName(resolved), level);
                    }
                }

                WriteManifest(archive, manifest, level);
            }, cancellationToken);

            Trim(options.RetentionCount);
            _logger.LogInformation("[{Server}] Создана резервная копия {File} за {Time}.",
                _profile.Name, fileName, stopwatch.Elapsed);

            return new BackupResult(true, fileName, new FileInfo(target).Length, stopwatch.Elapsed,
                $"Резервная копия {fileName} создана за {stopwatch.Elapsed.TotalSeconds:0.0} с.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Server}] Не удалось создать резервную копию.", _profile.Name);
            TryDelete(target);
            return new BackupResult(false, null, 0, stopwatch.Elapsed, $"Ошибка создания резервной копии: {ex.Message}");
        }
        finally
        {
            if (stoppedForBackup)
            {
                try
                {
                    await _supervisor.StartAsync("после резервного копирования", cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Server}] Не удалось запустить сервер после резервного копирования.", _profile.Name);
                }
            }
        }
    }

    // ------------------------------------------------------- бэкапы логов

    /// <summary>Список копий логов.</summary>
    public IReadOnlyList<BackupInfo> ListLogs()
    {
        if (!System.IO.Directory.Exists(LogsDirectory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(LogsDirectory, "*")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.FullName, f.Length, new DateTimeOffset(f.LastWriteTimeUtc), "logs"))
            .ToArray();
    }

    /// <summary>
    /// Создаёт резервную копию логов сервера (RPT, ADM, script_*, error_*).
    /// </summary>
    public async Task<BackupResult> CreateLogBackupAsync(CancellationToken cancellationToken)
    {
        var settings = _profile.Backups.Logs;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var source = ServerLogService.ProfilesDirectory(_profile);

        try
        {
            if (!System.IO.Directory.Exists(source))
            {
                return new BackupResult(false, null, 0, stopwatch.Elapsed, "Каталог профилей не найден.");
            }

            System.IO.Directory.CreateDirectory(LogsDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            if (settings.Compress)
            {
                var fileName = $"logs-{stamp}.zip";
                var target = Path.Combine(LogsDirectory, fileName);

                await Task.Run(() =>
                {
                    using var zipStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

                    foreach (var file in LogFiles(source))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Fastest);
                    }
                }, cancellationToken);

                TrimLogs();
                return new BackupResult(true, fileName, new FileInfo(target).Length, stopwatch.Elapsed,
                    $"Копия логов {fileName} создана.");
            }
            else
            {
                var fileName = $"logs-{stamp}";
                var target = Path.Combine(LogsDirectory, fileName);
                System.IO.Directory.CreateDirectory(target);

                foreach (var file in LogFiles(source))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                }

                TrimLogs();
                return new BackupResult(true, fileName, 0, stopwatch.Elapsed, $"Копия логов {fileName} создана.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Server}] Не удалось создать копию логов.", _profile.Name);
            return new BackupResult(false, null, 0, stopwatch.Elapsed, "Ошибка создания копии логов: " + ex.Message);
        }
    }

    /// <summary>Удаляет логи сервера старше заданного числа дней.</summary>
    public int PruneLogs()
    {
        var days = _profile.Backups.Logs.LogRetentionDays;
        if (days <= 0)
        {
            return 0;
        }

        var source = ServerLogService.ProfilesDirectory(_profile);
        if (!System.IO.Directory.Exists(source))
        {
            return 0;
        }

        var cutoff = DateTime.Now.AddDays(-days);
        var removed = 0;

        foreach (var file in LogFiles(source))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch
            {
                // Файл занят сервером.
            }
        }

        return removed;
    }

    private static IEnumerable<string> LogFiles(string directory) =>
        System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(ServerLogService.IsLogFile);

    private void TrimLogs()
    {
        var keep = Math.Max(1, _profile.Backups.Logs.RetentionCount);

        foreach (var info in ListLogs().Skip(keep))
        {
            TryDelete(info.FullPath);
        }
    }

    // ------------------------------------------------------- бэкапы мира

    /// <summary>Копии сохранения мира лежат в <c>dayzpulse\backups\world</c>.</summary>
    public string WorldDirectory => Path.Combine(ServerPaths.BackupsDirectory(_profile.Server), "world");

    /// <summary>Папка сохранения миссии: <c>mpmissions\&lt;миссия&gt;\storage_&lt;instanceId&gt;</c>.</summary>
    public string? WorldStoragePath()
    {
        var mission = QuickConfig.MissionDirectory(_profile.Server);
        if (mission is null)
        {
            return null;
        }

        var instanceId = ReadInstanceId();
        var storage = Path.Combine(mission, "storage_" + instanceId);

        return System.IO.Directory.Exists(storage) ? storage : null;
    }

    /// <summary>Номер инстанса из serverDZ.cfg (по умолчанию 1).</summary>
    private int ReadInstanceId()
    {
        try
        {
            var options = _profile.Server;
            var file = ServerPaths.ConfigFile(options);
            if (!File.Exists(file))
            {
                return 1;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(file), @"(?im)^\s*instanceId\s*=\s*(\d+)");

            return match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0 ? id : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Список копий сохранения мира.</summary>
    public IReadOnlyList<BackupInfo> ListWorld()
    {
        if (!System.IO.Directory.Exists(WorldDirectory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(WorldDirectory, "*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.FullName, f.Length, new DateTimeOffset(f.LastWriteTimeUtc), "world"))
            .ToArray();
    }

    /// <summary>
    /// Создаёт копию сохранения мира (папка storage с игроками, постройками,
    /// лутом). Мир пишется на диск всё время, пока сервер работает, поэтому
    /// копировать его на живом сервере нельзя: перед созданием сервер должен
    /// быть остановлен.
    /// </summary>
    public async Task<BackupResult> CreateWorldBackupAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (_supervisor.IsRunning)
        {
            return new BackupResult(false, null, 0, stopwatch.Elapsed,
                "Остановите сервер: сохранение мира пишется постоянно, копия на живом сервере будет повреждена.");
        }

        var storage = WorldStoragePath();
        if (storage is null)
        {
            return new BackupResult(false, null, 0, stopwatch.Elapsed,
                "Папка сохранения мира не найдена. Проверьте, что сервер хотя бы раз запускался.");
        }

        try
        {
            System.IO.Directory.CreateDirectory(WorldDirectory);
            var fileName = $"world-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            var target = Path.Combine(WorldDirectory, fileName);

            await Task.Run(() =>
            {
                using var zipStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

                // Пустые подкаталоги (например, 000-011) DayZ использует для
                // хранения регионов. Файлов в них может не быть, но структуру
                // нужно сохранить, иначе после отката они исчезнут.
                var directories = System.IO.Directory
                    .EnumerateDirectories(storage, "*", SearchOption.AllDirectories)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var dir in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntry(Path.GetRelativePath(storage, dir).Replace('\\', '/') + "/");
                }

                foreach (var file in System.IO.Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(storage, file).Replace('\\', '/');
                    AddFile(archive, file, relative, CompressionLevel.Fastest);
                }
            }, cancellationToken);

            TrimWorld();
            _logger.LogInformation("[{Server}] Создана копия мира {File} за {Time}.",
                _profile.Name, fileName, stopwatch.Elapsed);

            return new BackupResult(true, fileName, new FileInfo(target).Length, stopwatch.Elapsed,
                $"Копия мира {fileName} создана за {stopwatch.Elapsed.TotalSeconds:0.0} с.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Server}] Не удалось создать копию мира.", _profile.Name);
            return new BackupResult(false, null, 0, stopwatch.Elapsed, "Ошибка создания копии мира: " + ex.Message);
        }
    }

    /// <summary>
    /// Откат к копии мира: заменяет текущую папку сохранения содержимым архива.
    /// Требует остановленного сервера.
    /// </summary>
    public async Task<string> RestoreWorldAsync(string fileName, CancellationToken cancellationToken)
    {
        if (_supervisor.IsRunning)
        {
            throw new InvalidOperationException(
                "Остановите сервер перед откатом: сохранение мира нельзя менять на работающем сервере.");
        }

        var path = ResolveWorldPath(fileName);
        var storage = WorldStoragePath()
            ?? throw new InvalidOperationException("Папка сохранения мира не найдена.");

        // Текущее состояние сохраняем отдельной копией — на случай, если откат ошибочный.
        var safety = await CreateWorldBackupAsync(cancellationToken);
        _logger.LogInformation("[{Server}] Перед откатом мира сохранена копия {File}.",
            _profile.Name, safety.FileName ?? "(не создана)");

        var restored = 0;

        await Task.Run(() =>
        {
            // Архив должен содержать только пути внутри storage: иначе это не
            // наш дамп, и распаковывать его поверх сервера нельзя.
            using (var probeStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var probe = new ZipArchive(probeStream, ZipArchiveMode.Read))
            {
                foreach (var entry in probe.Entries)
                {
                    if (entry.FullName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(entry.FullName))
                    {
                        throw new InvalidOperationException($"Недопустимый путь в архиве: {entry.FullName}");
                    }
                }
            }

            // Удаляем текущее сохранение и распаковываем копию на его место.
            System.IO.Directory.Delete(storage, recursive: true);
            System.IO.Directory.CreateDirectory(storage);

            using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Запись-каталог (пустой регион 000-011): просто создаём папку.
                if (entry.FullName.EndsWith('/'))
                {
                    var dir = PathGuard.Resolve(entry.FullName.TrimEnd('/'), [storage]);
                    System.IO.Directory.CreateDirectory(dir);
                    continue;
                }

                var destination = PathGuard.Resolve(entry.FullName, [storage]);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                restored++;
            }
        }, cancellationToken);

        _logger.LogInformation("[{Server}] Мир восстановлен из {File}: {Count} файлов.",
            _profile.Name, fileName, restored);

        return $"Восстановлено файлов: {restored}. Запустите сервер.";
    }

    public string ResolveWorldPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(fileName))
        {
            throw new UnauthorizedAccessException("Недопустимое имя файла копии мира.");
        }

        if (!System.IO.Directory.Exists(WorldDirectory))
        {
            throw new FileNotFoundException($"Копия мира не найдена: {fileName}");
        }

        var full = PathGuard.Resolve(fileName, [WorldDirectory]);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Копия мира не найдена: {fileName}");
        }

        return full;
    }

    public bool DeleteWorld(string fileName)
    {
        var full = ResolveWorldPath(fileName);
        File.Delete(full);
        _logger.LogInformation("[{Server}] Копия мира удалена: {File}", _profile.Name, fileName);
        return true;
    }

    private void TrimWorld()
    {
        var keep = Math.Max(1, _profile.Backups.WorldRetentionCount);

        foreach (var info in ListWorld().Skip(keep))
        {
            TryDelete(info.FullPath);
        }
    }

    public async Task<string> RestoreAsync(string fileName, CancellationToken cancellationToken)
    {        var path = ResolveBackupPath(fileName);
        var workingDirectory = ServerPaths.WorkingDirectory(_profile.Server);
        var wasRunning = _supervisor.IsRunning;

        if (wasRunning)
        {
            await _supervisor.StopAsync("восстановление из резервной копии", cancellationToken);
        }

        // Перед восстановлением сохраняем текущее состояние.
        await CreateAsync("pre-restore", cancellationToken);

        var restored = 0;
        await Task.Run(() =>
        {
            using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.Name == "_manifest.json")
                {
                    continue;
                }

                var destination = PathGuard.Resolve(entry.FullName, [workingDirectory]);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                restored++;
            }
        }, cancellationToken);

        _logger.LogInformation("[{Server}] Восстановлено {Count} файлов из {File}.", _profile.Name, restored, fileName);

        if (wasRunning)
        {
            await _supervisor.StartAsync("после восстановления", cancellationToken);
        }

        return $"Восстановлено файлов: {restored}.";
    }

    public string ResolveBackupPath(string fileName)
    {
        if (fileName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(fileName))
        {
            throw new UnauthorizedAccessException("Недопустимое имя файла резервной копии.");
        }

        // Копии могут лежать и в основном каталоге, и в каталоге логов.
        foreach (var root in new[] { Directory, LogsDirectory })
        {
            if (!System.IO.Directory.Exists(root))
            {
                continue;
            }

            var full = PathGuard.Resolve(fileName, [root]);
            if (File.Exists(full))
            {
                return full;
            }
        }

        throw new FileNotFoundException($"Резервная копия не найдена: {fileName}");
    }

    public bool Delete(string fileName)
    {
        var full = ResolveBackupPath(fileName);

        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Удалять можно только файлы резервных копий.");
        }

        File.Delete(full);
        _logger.LogInformation("[{Server}] Резервная копия удалена: {File}", _profile.Name, fileName);
        return true;
    }

    private void Trim(int retention)
    {
        if (retention <= 0)
        {
            return;
        }

        // Ротация только основных копий: архивы логов живут по своему RetentionCount.
        var keep = ListPrimary().Take(retention).Select(b => b.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var backup in ListPrimary())
        {
            if (!keep.Contains(backup.FileName))
            {
                TryDelete(backup.FullPath);
            }
        }
    }

    private static string? ResolveInclude(string include, string workingDirectory)
    {
        try
        {
            return Path.IsPathRooted(include)
                ? Path.GetFullPath(include)
                : Path.GetFullPath(Path.Combine(workingDirectory, include));
        }
        catch
        {
            return null;
        }
    }

    private static void AddDirectory(ZipArchive archive, string sourceDirectory, string entryPrefix,
        IReadOnlyList<string> excludes, CompressionLevel level, CancellationToken cancellationToken)
    {
        var stack = new Stack<(string Path, string Prefix)>();
        stack.Push((sourceDirectory, entryPrefix));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, prefix) = stack.Pop();

            foreach (var file in System.IO.Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (excludes.Any(pattern => Matches(name, pattern)))
                {
                    continue;
                }

                var relative = Path.Combine(prefix, Path.GetRelativePath(sourceDirectory, file)).Replace('\\', '/');
                AddFile(archive, file, relative, level);
            }

            foreach (var directory in System.IO.Directory.EnumerateDirectories(current))
            {
                var relative = Path.Combine(prefix, Path.GetRelativePath(sourceDirectory, directory)).Replace('\\', '/');
                stack.Push((directory, relative));
            }
        }
    }

    private static void AddFile(ZipArchive archive, string file, string entryName, CompressionLevel level)
    {
        try
        {
            var entry = archive.CreateEntry(entryName.Replace('\\', '/'), level);
            entry.LastWriteTime = File.GetLastWriteTime(file);
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }
        catch (IOException)
        {
            // Файл занят сервером — пропускаем.
        }
    }

    private static void WriteManifest(ZipArchive archive, Dictionary<string, object> manifest, CompressionLevel level)
    {
        var entry = archive.CreateEntry("_manifest.json", level);
        using var stream = entry.Open();
        var bytes = System.Text.Encoding.UTF8.GetBytes(Json.Serialize(manifest));
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool Matches(string fileName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string? ReadKind(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("_manifest.json");
            if (entry is null)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open());
            var data = Json.Deserialize<Dictionary<string, JsonElement>>(reader.ReadToEnd());
            return data is not null && data.TryGetValue("kind", out var kind) ? kind.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Игнорируем.
        }
    }
}
