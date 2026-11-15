namespace DayZPulse.Infrastructure;

/// <summary>
/// Не даёт выйти за пределы разрешённых каталогов через "../" и симлинки.
/// </summary>
public static class PathGuard
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public static bool IsInside(string candidate, string root)
    {
        var fullCandidate = Normalize(candidate);
        var fullRoot = Normalize(root);
        if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Разрешает путь относительно одной из разрешённых корневых папок.</summary>
    public static string Resolve(string requestedPath, IEnumerable<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ArgumentException("Путь не указан.", nameof(requestedPath));
        }

        if (requestedPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("Путь содержит недопустимые символы.", nameof(requestedPath));
        }

        var roots = allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
        {
            throw new InvalidOperationException("Не задан ни один разрешённый каталог.");
        }

        // Абсолютный путь проверяем напрямую, относительный — относительно первого корня.
        var candidates = Path.IsPathRooted(requestedPath)
            ? new[] { Normalize(requestedPath) }
            : roots.Select(r => Normalize(Path.Combine(r, requestedPath))).ToArray();

        foreach (var candidate in candidates)
        {
            if (roots.Any(r => IsInside(candidate, r)))
            {
                return candidate;
            }
        }

        throw new UnauthorizedAccessException($"Доступ к пути запрещён: {requestedPath}");
    }
}
