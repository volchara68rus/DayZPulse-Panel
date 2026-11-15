using System.Text;
using DayZPulse.Infrastructure;
using DayZPulse.Models;

namespace DayZPulse.Core;

public sealed record ConfigFileInfo(
    string Name,
    string Path,
    string RelativePath,
    string? Description,
    bool Exists,
    bool Editable,
    long SizeBytes,
    DateTimeOffset? ModifiedAt);

/// <summary>Чтение и запись файлов конфигурации одного сервера в пределах его каталогов.</summary>
public sealed class ConfigFileManager
{
    private const long MaxEditableBytes = 2 * 1024 * 1024;

    private readonly ILogger<ConfigFileManager> _logger;
    private ServerProfile _profile;

    public ConfigFileManager(ILogger<ConfigFileManager> logger, ServerProfile profile)
    {
        _logger = logger;
        _profile = profile;
    }

    public void UpdateProfile(ServerProfile profile) => _profile = profile;

    private (string WorkingDirectory, IReadOnlyList<string> Roots) Context()
    {
        var options = _profile.Server;
        return (ServerPaths.WorkingDirectory(options), ServerPaths.AllowedRoots(options, AppPaths.DataRoot));
    }

    public IReadOnlyList<ConfigFileInfo> List()
    {
        var (workingDirectory, _) = Context();
        var result = new List<ConfigFileInfo>();

        foreach (var entry in _profile.ConfigFiles)
        {
            var full = ResolveOne(entry.Path);
            var exists = full is not null && File.Exists(full);

            result.Add(new ConfigFileInfo(
                entry.Name,
                full ?? entry.Path,
                Relative(entry.Path, workingDirectory),
                entry.Description,
                exists,
                entry.Editable && !entry.ReadOnly,
                exists ? new FileInfo(full!).Length : 0,
                exists ? new DateTimeOffset(File.GetLastWriteTimeUtc(full!)) : null));
        }

        return result;
    }

    /// <summary>Находит файл по записи из списка или по пути внутри разрешённых каталогов.</summary>
    public string Resolve(string requestedPath)
    {
        var entry = _profile.ConfigFiles.FirstOrDefault(c =>
            string.Equals(c.Path, requestedPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Name, requestedPath, StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
        {
            return ResolveOne(entry.Path)
                   ?? throw new FileNotFoundException($"Файл конфигурации не найден: {entry.Path}");
        }

        var (_, roots) = Context();
        return PathGuard.Resolve(requestedPath, roots);
    }

    public bool IsEditable(string requestedPath)
    {
        var entry = _profile.ConfigFiles.FirstOrDefault(c =>
            string.Equals(c.Path, requestedPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Name, requestedPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Path, Path.GetFileName(requestedPath), StringComparison.OrdinalIgnoreCase));

        return entry is null || (entry.Editable && !entry.ReadOnly);
    }

    public async Task<string> ReadAsync(string requestedPath, CancellationToken cancellationToken)
    {
        var full = Resolve(requestedPath);
        if (!File.Exists(full))
        {
            return "";
        }

        var info = new FileInfo(full);
        if (info.Length > MaxEditableBytes)
        {
            throw new InvalidOperationException($"Файл слишком большой для редактирования ({info.Length / 1024 / 1024} МБ).");
        }

        return await File.ReadAllTextAsync(full, DetectEncoding(full), cancellationToken);
    }

    public async Task WriteAsync(string requestedPath, string content, CancellationToken cancellationToken)
    {
        if (!IsEditable(requestedPath))
        {
            throw new UnauthorizedAccessException("Файл помечен как доступный только для чтения.");
        }

        var full = Resolve(requestedPath);
        if (Encoding.UTF8.GetByteCount(content) > MaxEditableBytes)
        {
            throw new InvalidOperationException("Содержимое слишком большое для сохранения.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Держим одну резервную копию предыдущего содержимого.
        if (File.Exists(full))
        {
            try
            {
                File.Copy(full, full + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось создать резервную копию файла {Path}.", full);
            }
        }

        await File.WriteAllTextAsync(full, content, DetectEncoding(full), cancellationToken);
        _logger.LogInformation("[{Server}] Файл конфигурации сохранён: {Path}", _profile.Name, full);
    }

    private string? ResolveOne(string path)
    {
        try
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            var (workingDirectory, _) = Context();
            return Path.GetFullPath(Path.Combine(workingDirectory, path));
        }
        catch
        {
            return null;
        }
    }

    private static string Relative(string path, string workingDirectory)
    {
        try
        {
            return Path.GetRelativePath(workingDirectory, Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }

    private static Encoding DetectEncoding(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> bom = stackalloc byte[3];
            var read = stream.Read(bom);
            if (read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                return new UTF8Encoding(true);
            }
        }
        catch
        {
            // Определение не критично.
        }

        return new UTF8Encoding(false);
    }
}
