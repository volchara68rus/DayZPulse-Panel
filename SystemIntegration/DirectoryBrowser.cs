using DayZPulse.Core;

namespace DayZPulse.SystemIntegration;

public sealed record DirectoryEntry(string Name, string Path, bool HasChildren);

public sealed record BrowseResult(
    string Path,
    string? Parent,
    IReadOnlyList<DirectoryEntry> Directories,
    IReadOnlyList<DirectoryEntry> Drives,
    string? Error);

/// <summary>
/// Просмотр файловой системы для выбора папки прямо в веб-интерфейсе.
/// Работает в любом режиме, включая службу Windows, где системный диалог недоступен.
/// </summary>
public static class DirectoryBrowser
{
    /// <summary>Диски и список каталогов по указанному пути.</summary>
    public static BrowseResult Browse(string? requestedPath)
    {
        var drives = ListDrives();

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            // По умолчанию показываем диски.
            return new BrowseResult("", null, [], drives, null);
        }

        try
        {
            var path = Path.GetFullPath(requestedPath.Trim());

            if (!Directory.Exists(path))
            {
                // Если каталога нет, поднимаемся к существующему родителю.
                var fallback = FindExistingParent(path);
                if (fallback is null)
                {
                    return new BrowseResult("", null, [], drives, "Каталог не найден.");
                }

                path = fallback;
            }

            var directories = new List<DirectoryEntry>();

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);

                        // Скрытые и системные каталоги не показываем.
                        if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                            info.Attributes.HasFlag(FileAttributes.System))
                        {
                            continue;
                        }

                        directories.Add(new DirectoryEntry(info.Name, info.FullName, HasSubdirectories(info.FullName)));
                    }
                    catch
                    {
                        // Каталог мог исчезнуть или нет доступа.
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new BrowseResult(path, ParentOf(path), [], drives,
                    "Нет доступа к содержимому каталога.");
            }

            directories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            return new BrowseResult(path, ParentOf(path), directories, drives, null);
        }
        catch (Exception ex)
        {
            return new BrowseResult("", null, [], drives, ex.Message);
        }
    }

    /// <summary>Создаёт подкаталог в указанной папке. Возвращает путь новой папки.</summary>
    public static (bool Ok, string? Path, string? Error) CreateDirectory(string? parentPath, string? name)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return (false, null, "Сначала откройте папку, где создать новую.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return (false, null, "Укажите имя новой папки.");
        }

        var clean = name.Trim().TrimEnd('\\', '/');

        if (clean.Length == 0 || clean.Length > 100)
        {
            return (false, null, "Имя папки: от 1 до 100 символов.");
        }

        if (clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            clean is "." or "..")
        {
            return (false, null, "Имя папки содержит недопустимые символы.");
        }

        try
        {
            var parent = Path.GetFullPath(parentPath.Trim());

            if (!Directory.Exists(parent))
            {
                return (false, null, "Папка, где создаётся каталог, не найдена.");
            }

            var target = Path.GetFullPath(Path.Combine(parent, clean));

            // Защита от выхода за пределы родительского каталога.
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, "Недопустимый путь.");
            }

            if (Directory.Exists(target))
            {
                return (true, target, null);
            }

            Directory.CreateDirectory(target);
            return (true, target, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string? ParentOf(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent) ? null : parent;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindExistingParent(string path)
    {
        var current = path;

        for (var i = 0; i < 32; i++)
        {
            var parent = ParentOf(current);
            if (parent is null)
            {
                return null;
            }

            if (Directory.Exists(parent))
            {
                return parent;
            }

            current = parent;
        }

        return null;
    }

    private static bool HasSubdirectories(string path)
    {
        try
        {
            using var enumerator = Directory.EnumerateDirectories(path).GetEnumerator();
            return enumerator.MoveNext();
        }
        catch
        {
            return false;
        }
    }

    private static List<DirectoryEntry> ListDrives()
    {
        var drives = new List<DirectoryEntry>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? root
                    : $"{root}  ({drive.VolumeLabel})";

                drives.Add(new DirectoryEntry(label, root, true));
            }
            catch
            {
                // Некоторые тома недоступны.
            }
        }

        return drives;
    }
}
