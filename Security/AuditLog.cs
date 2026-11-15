using System.Text;
using DayZPulse.Infrastructure;

namespace DayZPulse.Security;

/// <summary>Простой журнал действий: кто и что сделал через веб-интерфейс.</summary>
public sealed class AuditLog
{
    private readonly object _gate = new();
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(ILogger<AuditLog> logger)
    {
        _logger = logger;
    }

    public void Write(string actor, string action, string? details = null, string? remoteAddress = null)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\t{actor}\t{action}\t{remoteAddress}\t{details}";
        _logger.LogInformation("Аудит: {Actor} {Action} {Details}", actor, action, details);

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                File.AppendAllText(AppPaths.AuditFile, line + Environment.NewLine, Encoding.UTF8);
                Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось записать запись аудита.");
        }
    }

    public IReadOnlyList<string> ReadTail(int maxLines = 500)
    {
        if (!File.Exists(AppPaths.AuditFile))
        {
            return [];
        }

        try
        {
            lock (_gate)
            {
                using var stream = new FileStream(AppPaths.AuditFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var queue = new Queue<string>(maxLines);
                while (reader.ReadLine() is { } line)
                {
                    if (queue.Count == maxLines)
                    {
                        queue.Dequeue();
                    }

                    queue.Enqueue(line);
                }

                return queue.ToArray();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать журнал аудита.");
            return [];
        }
    }

    private void Trim()
    {
        var info = new FileInfo(AppPaths.AuditFile);
        if (info.Length < 5 * 1024 * 1024)
        {
            return;
        }

        var lines = File.ReadAllLines(AppPaths.AuditFile, Encoding.UTF8);
        var keep = lines.Length > 20_000 ? lines[^20_000..] : lines;
        File.WriteAllLines(AppPaths.AuditFile, keep, Encoding.UTF8);
    }
}
