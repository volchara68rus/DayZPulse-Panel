using System.Text.Json;
using DayZPulse.Infrastructure;
using DayZPulse.Models;

namespace DayZPulse.Core;

public sealed record JobExecution(string JobId, string Name, string Type, DateTimeOffset At, bool Success, string Message);

/// <summary>
/// Выполняет задачи расписания одного сервера: запуск, остановка, перезапуск,
/// резервное копирование и команды.
/// </summary>
public sealed class ScheduleRunner
{
    private readonly ILogger _logger;
    private readonly Tracker.CommandQueue _commands;
    private readonly ServerSupervisor _supervisor;
    private readonly BackupManager _backups;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<JobExecution> _history = new();

    private ServerProfile _profile;

    public ScheduleRunner(ILogger logger, ServerProfile profile, ServerSupervisor supervisor,
        BackupManager backups, Tracker.CommandQueue commands)
    {
        _logger = logger;
        _profile = profile;
        _supervisor = supervisor;
        _backups = backups;
        _commands = commands;
        LoadState();
    }

    public void UpdateProfile(ServerProfile profile) => _profile = profile;

    public string ServerId => _profile.Id;

    public IReadOnlyList<JobExecution> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    public DateTimeOffset? LastRunOf(string jobId)
    {
        lock (_gate)
        {
            return _lastRun.TryGetValue(jobId, out var value) ? value : null;
        }
    }

    /// <summary>Проверяет расписание и выполняет наступившие задачи.</summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;

        foreach (var job in _profile.Schedule.Where(j => j.Enabled).ToArray())
        {
            if (!IsDue(job, now))
            {
                continue;
            }

            await RunAsync(job, "расписание", cancellationToken);
        }
    }

    private bool IsDue(ScheduledJob job, DateTimeOffset now)
    {
        DateTimeOffset? last;
        lock (_gate)
        {
            last = _lastRun.TryGetValue(job.Id, out var value) ? value : job.LastRun;
        }

        if (job.Mode == "interval")
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, job.IntervalMinutes));
            return last is null || now - last.Value >= interval;
        }

        if (!TimeOnly.TryParse(job.Time, out var target))
        {
            return false;
        }

        var today = now.Date.Add(target.ToTimeSpan());
        if (now < today)
        {
            return false;
        }

        return last is null || last.Value.Date < now.Date || last.Value < today.AddMinutes(-1);
    }

    public async Task<JobExecution> RunAsync(ScheduledJob job, string actor, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var message = "";
        var success = true;

        try
        {
            switch (job.Type)
            {
                case "start":
                    await _supervisor.StartAsync($"{actor}:{job.Name}", cancellationToken);
                    message = "Сервер запущен.";
                    break;

                case "stop":
                    await _supervisor.StopAsync($"{actor}:{job.Name}", cancellationToken);
                    message = "Сервер остановлен.";
                    break;

                case "restart":
                    await _supervisor.RestartAsync($"{actor}:{job.Name}", cancellationToken);
                    message = "Сервер перезапущен.";
                    break;

                case "backup":
                    var result = await _backups.CreateAsync($"schedule:{job.Name}", cancellationToken);
                    success = result.Success;
                    message = result.Message;
                    break;

                case "command":
                    if (string.IsNullOrWhiteSpace(job.Payload))
                    {
                        throw new InvalidOperationException("Не задан текст команды.");
                    }

                    // Команда уходит моду через очередь трекера.
                    _commands.Enqueue("message", new Dictionary<string, string> { ["text"] = job.Payload });
                    message = $"Отправлено моду: {job.Payload}";
                    break;

                default:
                    throw new InvalidOperationException($"Неизвестный тип задачи: {job.Type}");
            }
        }
        catch (Exception ex)
        {
            success = false;
            message = ex.Message;
            _logger.LogWarning(ex, "[{Server}] Задача расписания «{Name}» завершилась с ошибкой.", _profile.Name, job.Name);
        }

        var execution = new JobExecution(job.Id, job.Name, job.Type, startedAt, success, message);

        lock (_gate)
        {
            _lastRun[job.Id] = DateTimeOffset.Now;
            _history.Enqueue(execution);
            while (_history.Count > 100)
            {
                _history.Dequeue();
            }
        }

        _supervisor.Console.Add("sys",
            $"Задача «{job.Name}» ({job.Type}): {(success ? "выполнена" : "ошибка")} — {message}");

        SaveState();
        return execution;
    }

    /// <summary>Состояние расписания хранится в папке сервера.</summary>
    private string StatePath => ServerPaths.StateFile(_profile.Server, "schedule.json");

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return;
            }

            var data = Json.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(StatePath));
            if (data is null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (var (key, value) in data)
                {
                    _lastRun[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Server}] Не удалось загрузить состояние расписания.", _profile.Name);
        }
    }

    private void SaveState()
    {
        try
        {
            Dictionary<string, DateTimeOffset> copy;
            lock (_gate)
            {
                copy = new Dictionary<string, DateTimeOffset>(_lastRun, StringComparer.OrdinalIgnoreCase);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath,
                JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Server}] Не удалось сохранить состояние расписания.", _profile.Name);
        }
    }
}
