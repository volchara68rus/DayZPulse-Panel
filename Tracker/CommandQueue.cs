using System.Text;

namespace DayZPulse.Tracker;

public sealed class CommandEntry
{
    public long Id { get; set; }
    public string Action { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new();
    public string Status { get; set; } = "pending";
    public string Message { get; set; } = "";
    public string Oids { get; set; } = "";
    public string Group { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Очередь команд для мода. Протокол at-most-once: выданная команда никогда
/// не выдаётся повторно, даже если подтверждение не пришло.
/// </summary>
public sealed class CommandQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<long, CommandEntry> _commands = new();
    private readonly ILogger<CommandQueue> _logger;
    private long _nextId;

    /// <summary>Сколько ждём подтверждения, прежде чем считать команду просроченной.</summary>
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько команда может ждать выдачи.</summary>
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(5);

    public CommandQueue(ILogger<CommandQueue> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> AllowedActions { get; } =
    [
        "ping", "spawn", "give", "preset", "delete",
        "weather", "time", "teleport", "heal", "damage", "kill", "message", "broadcast",
        "inventory", "mute", "unmute", "warn", "vehicle"
    ];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _commands.Count;
            }
        }
    }

    public CommandEntry Enqueue(string action, IReadOnlyDictionary<string, string> args, string group = "")
    {
        lock (_gate)
        {
            var entry = new CommandEntry
            {
                Id = ++_nextId,
                Action = action,
                Args = new Dictionary<string, string>(args),
                Group = group,
                CreatedAt = DateTimeOffset.Now,
                ExpiresAt = DateTimeOffset.Now.Add(PendingTimeout)
            };

            _commands[entry.Id] = entry;
            Expire();
            return entry;
        }
    }

    /// <summary>Возвращает готовые к отправке команды и помечает их выданными.</summary>
    public IReadOnlyList<CommandEntry> TakePending(int max)
    {
        lock (_gate)
        {
            Expire();

            var ready = _commands.Values
                .Where(c => c.Status == "pending" && c.ExpiresAt > DateTimeOffset.Now)
                .OrderBy(c => c.Id)
                .Take(Math.Max(0, max))
                .ToArray();

            foreach (var entry in ready)
            {
                entry.Status = "dispatched";
                entry.DispatchedAt = DateTimeOffset.Now;
            }

            return ready;
        }
    }

    /// <summary>Строка протокола: <c>id|action|key=value|...</c></summary>
    public static string BuildLine(CommandEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Id).Append('|').Append(entry.Action);

        foreach (var (key, value) in entry.Args)
        {
            // Разделитель и переводы строк недопустимы в плоском формате.
            var safe = value.Replace("|", "").Replace("\r", "").Replace("\n", " ").Trim();
            builder.Append('|').Append(key).Append('=').Append(safe);
        }

        return builder.ToString();
    }

    public void Acknowledge(IEnumerable<(long Id, bool Ok, string? Message, string? Oids)> results)
    {
        lock (_gate)
        {
            foreach (var (id, ok, message, oids) in results)
            {
                if (!_commands.TryGetValue(id, out var entry))
                {
                    continue;
                }

                entry.Status = ok ? "done" : "failed";
                entry.Message = message ?? "";
                entry.Oids = oids ?? "";
                entry.UpdatedAt = DateTimeOffset.Now;
            }
        }
    }

    public bool Cancel(long id)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(id, out var entry) || entry.Status != "pending")
            {
                return false;
            }

            entry.Status = "cancelled";
            entry.UpdatedAt = DateTimeOffset.Now;
            return true;
        }
    }

    public IReadOnlyList<CommandEntry> List(string? status, int limit)
    {
        lock (_gate)
        {
            Expire();

            var query = _commands.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(c => string.Equals(c.Status, status, StringComparison.OrdinalIgnoreCase));
            }

            return query.OrderByDescending(c => c.Id).Take(limit).ToArray();
        }
    }

    public CommandEntry? Get(long id)
    {
        lock (_gate)
        {
            return _commands.TryGetValue(id, out var entry) ? entry : null;
        }
    }

    /// <summary>Просроченные можно удалять: они уже не влияют на мод.</summary>
    public void Expire()
    {
        var now = DateTimeOffset.Now;

        foreach (var entry in _commands.Values.ToArray())
        {
            if (entry.Status == "pending" && entry.ExpiresAt <= now)
            {
                entry.Status = "expired";
                entry.Message = "Команда не была забрана модом за отведённое время.";
                entry.UpdatedAt = now;
            }
            else if (entry.Status == "dispatched" && entry.DispatchedAt is not null &&
                     now - entry.DispatchedAt.Value > DispatchTimeout)
            {
                entry.Status = "expired";
                entry.Message = "Мод не подтвердил выполнение команды.";
                entry.UpdatedAt = now;
            }
        }

        // Чистим завершённые старше суток, чтобы словарь не рос бесконечно.
        var cutoff = now.AddDays(-1);
        foreach (var entry in _commands.Values.Where(c =>
                     c.Status is "done" or "failed" or "cancelled" or "expired" &&
                     c.UpdatedAt is not null && c.UpdatedAt < cutoff).ToArray())
        {
            _commands.Remove(entry.Id);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _commands.Clear();
        }
    }
}
