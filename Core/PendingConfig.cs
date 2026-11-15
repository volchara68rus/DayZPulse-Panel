using DayZPulse.Models;

namespace DayZPulse.Core;

/// <summary>
/// Отложенные правки конфигурации: пока сервер запущен, изменения не пишутся
/// в рабочие файлы, а складываются во временную папку <c>dayzpulse\pending</c>
/// (с сохранением относительных путей). При следующем запуске сервера панель
/// переносит их в рабочие файлы и очищает папку.
/// </summary>
public static class PendingConfig
{
    /// <summary>Каталог отложенных правок.</summary>
    public static string Root(ServerSettings options) =>
        Path.Combine(ServerPaths.DataDirectory(options), "pending");

    /// <summary>Складывает отложенную правку: путь приводится к рабочему каталогу.</summary>
    public static void Stage(ServerSettings options, string absolutePath, string content)
    {
        try
        {
            var working = Path.GetFullPath(ServerPaths.WorkingDirectory(options));
            var full = Path.GetFullPath(absolutePath);

            // Храним относительно рабочего каталога, чтобы применить по тому же месту.
            var relative = Path.GetRelativePath(working, full);
            var target = Path.Combine(Root(options), relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Отложенная запись не критична для работы панели.
        }
    }

    /// <summary>Есть ли отложенные правки.</summary>
    public static bool HasAny(ServerSettings options)
    {
        try
        {
            var root = Root(options);
            return Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Список относительных путей отложенных правок.</summary>
    public static IReadOnlyList<string> List(ServerSettings options)
    {
        try
        {
            var root = Root(options);
            if (!Directory.Exists(root))
            {
                return [];
            }

            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Переносит отложенные правки в рабочие файлы и очищает папку.</summary>
    public static int Apply(ServerSettings options)
    {
        try
        {
            var root = Root(options);
            if (!Directory.Exists(root))
            {
                return 0;
            }

            var working = Path.GetFullPath(ServerPaths.WorkingDirectory(options));
            var applied = 0;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var target = Path.GetFullPath(Path.Combine(working, relative));

                // Защита: пишем только внутри рабочего каталога сервера.
                if (!target.StartsWith(working, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                applied++;
            }

            Clear(options);
            return applied;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Удаляет папку отложенных правок.</summary>
    public static void Clear(ServerSettings options)
    {
        try
        {
            var root = Root(options);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Игнорируем: файлы могли быть заняты.
        }
    }
}
