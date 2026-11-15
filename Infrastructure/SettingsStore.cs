using DayZPulse.Hosting;
using DayZPulse.Models;

namespace DayZPulse.Infrastructure;

public sealed record SettingsChangedEventArgs(AppSettings Settings, AppSettings Previous);

/// <summary>Потокобезопасное хранилище настроек с записью на диск и резервной копией при загрузке.</summary>
public sealed class SettingsStore
{
    private readonly ILogger<SettingsStore> _logger;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private AppSettings _current = new();
    private RuntimeOverrides _overrides = new();

    public SettingsStore(ILogger<SettingsStore> logger)
    {
        _logger = logger;
        Load();
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public AppSettings Current
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _current;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Вернуть независимую копию настроек, чтобы её можно было безопасно изменять.</summary>
    public AppSettings Snapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return Json.Clone(_current);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Load()
    {
        _lock.EnterWriteLock();
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                // Пустой список серверов: владелец добавит первый сам.
                _current = new AppSettings();
                WriteToDisk(_current);
                _logger.LogInformation("Создан файл настроек по умолчанию: {Path}", AppPaths.SettingsFile);
                return;
            }

            try
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                _current = Json.Deserialize<AppSettings>(json) ?? new AppSettings();
                Migrate(_current);
            }
            catch (Exception ex)
            {
                var broken = AppPaths.SettingsFile + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                _logger.LogError(ex, "Файл настроек повреждён, переименован в {Broken}", broken);
                TryMove(AppPaths.SettingsFile, broken);
                _current = new AppSettings();
                WriteToDisk(_current);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Переносит настройки одиночного сервера (формат до мультисерверности)
    /// в первый профиль и убирает дублирующиеся поля.
    /// </summary>
    private void Migrate(AppSettings settings)
    {
        settings.Servers ??= [];

        if (settings.Server is not null || settings.Tracker is not null)
        {
            var legacy = new ServerProfile
            {
                Id = ServerProfile.Slugify(settings.Server?.BaseDirectory is { Length: > 0 } dir
                    ? Path.GetFileName(dir.TrimEnd('\\', '/'))
                    : "dayz"),
                Name = "Сервер DayZ",
                Server = settings.Server ?? new ServerSettings(),
                Tracker = settings.Tracker ?? new TrackerSettings(),
                Backups = settings.Backups ?? new BackupSettings(),
                ConfigFiles = settings.ConfigFiles is { Count: > 0 }
                    ? settings.ConfigFiles
                    : ServerProfile.CreateDefaultConfigFiles(),
                Schedule = settings.Schedule ?? []
            };

            if (string.IsNullOrWhiteSpace(legacy.Tracker.ServerId))
            {
                legacy.Tracker.ServerId = legacy.Id;
            }

            // Не затираем уже существующий профиль с тем же идентификатором.
            if (settings.Servers.All(p => p.Id != legacy.Id))
            {
                settings.Servers.Insert(0, legacy);
                _logger.LogInformation("Настройки одиночного сервера перенесены в профиль «{Id}».", legacy.Id);
            }

            settings.Server = null;
            settings.Tracker = null;
            settings.Backups = null;
            settings.ConfigFiles = null;
            settings.Schedule = null;
        }

        // Пустой список допустим: панель показывает экран приветствия,
        // пока владелец не добавит первый сервер.

        // Уникализируем идентификаторы на случай ручной правки файла.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Servers)
        {
            var id = ServerProfile.Slugify(profile.Id, "server");
            var candidate = id;
            var index = 2;
            while (!seen.Add(candidate))
            {
                candidate = $"{id}-{index++}";
            }

            if (profile.Id != candidate)
            {
                _logger.LogWarning("Идентификатор сервера «{Old}» заменён на «{New}».", profile.Id, candidate);
                profile.Id = candidate;
            }

            profile.ConfigFiles ??= ServerProfile.CreateDefaultConfigFiles();
            profile.Schedule ??= [];
            profile.Tracker ??= new TrackerSettings();

            if (string.IsNullOrWhiteSpace(profile.Tracker.ServerId))
            {
                profile.Tracker.ServerId = profile.Id;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultServerId) ||
            settings.Servers.All(p => p.Id != settings.DefaultServerId))
        {
            settings.DefaultServerId = settings.Servers.FirstOrDefault()?.Id;
        }
    }

    public ServerProfile? FindServer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Current.Servers.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Изменяет профиль сервера и сохраняет настройки.</summary>
    public void UpdateServer(string id, Action<ServerProfile> mutate, bool notify = true) =>
        Update(settings =>
        {
            var profile = settings.Servers.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Сервер не найден: {id}");

            mutate(profile);
        }, notify);

    /// <summary>Загрузка из резервной копии файла (для восстановления).</summary>
    public AppSettings ImportFrom(string file, bool persist = true)
    {
        var json = File.ReadAllText(file);
        var incoming = Json.Deserialize<AppSettings>(json)
                       ?? throw new InvalidDataException("Файл настроек пуст или имеет неверный формат.");
        Validate(incoming);

        if (persist)
        {
            Update(_ => Json.Clone(incoming));
        }

        return incoming;
    }

    public void Update(Action<AppSettings> mutate, bool notify = true)
    {
        AppSettings previous;
        AppSettings updated;

        _lock.EnterWriteLock();
        try
        {
            previous = Json.Clone(_current);
            updated = Json.Clone(_current);
            mutate(updated);
            Validate(updated);
            _current = updated;

            try
            {
                WriteToDisk(updated);
            }
            catch (Exception ex)
            {
                // Настройки применяются в памяти, даже если файл записать не удалось.
                _logger.LogError(ex, "Не удалось сохранить настройки на диск.");
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (notify)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(updated, previous));
        }
    }

    public void SetRuntimeOverrides(RuntimeOverrides overrides) => _overrides = overrides ?? new RuntimeOverrides();

    /// <summary>Эффективные настройки веб-интерфейса с учётом переопределений запуска.</summary>
    public WebSettings EffectiveWeb
    {
        get
        {
            var web = Json.Clone(Snapshot().Web);
            var overrides = _overrides;

            if (overrides.WebPort is > 0)
            {
                web.Port = overrides.WebPort.Value;
            }

            if (overrides.OpenBrowserOnStart is not null)
            {
                web.OpenBrowserOnStart = overrides.OpenBrowserOnStart.Value;
            }

            return web;
        }
    }

    /// <summary>Экспорт текущих настроек в указанный файл.</summary>
    public void ExportTo(string file)
    {
        var json = Json.Serialize(Snapshot());
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
    }

    public static void Validate(AppSettings settings)
    {
        if (settings.Web.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Порт должен быть в диапазоне 1-65535.");
        }

        if (!string.IsNullOrWhiteSpace(settings.Web.BindAddress) &&
            !System.Net.IPAddress.TryParse(settings.Web.BindAddress, out _))
        {
            throw new ArgumentException("Адрес прослушивания должен быть корректным IP-адресом.", nameof(settings));
        }

        if (settings.Auth.Enabled && settings.Auth.SessionHours is < 1 or > 720)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Время жизни сессии должно быть от 1 до 720 часов.");
        }

        if (settings.Logging.MaxConsoleLines is < 100 or > 200_000)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Размер буфера консоли должен быть от 100 до 200000 строк.");
        }

        if (settings.Servers is null)
        {
            throw new ArgumentException("Список серверов отсутствует.", nameof(settings));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new List<(string Id, int First, int Last)>();

        foreach (var profile in settings.Servers)
        {
            var name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name;

            if (string.IsNullOrWhiteSpace(profile.Id) || !ServerProfile.Slugify(profile.Id).Equals(profile.Id, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Некорректный идентификатор сервера: «{profile.Id}».", nameof(settings));
            }

            if (!ids.Add(profile.Id))
            {
                throw new ArgumentException($"Идентификатор сервера повторяется: «{profile.Id}».", nameof(settings));
            }

            var server = profile.Server;

            if (server.Port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), $"Порт сервера «{name}» должен быть в диапазоне 1-65535.");
            }

            if (server.GracefulStopSeconds is < 0 or > 600)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), $"Таймаут остановки сервера «{name}» должен быть от 0 до 600 секунд.");
            }

            // Сервер занимает блок из пяти портов (игра, Steam Query, RCON …).
            var first = server.Port;
            var last = server.Port + 4;
            if (last > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(settings),
                    $"Порт сервера «{name}» слишком велик: нужен блок из пяти портов.");
            }

            foreach (var (otherId, otherFirst, otherLast) in ports)
            {
                if (first <= otherLast && otherFirst <= last)
                {
                    throw new ArgumentException(
                        $"Порты серверов «{otherId}» ({otherFirst}–{otherLast}) и «{name}» ({first}–{last}) пересекаются. " +
                        "Укажите другой игровой порт: сервер занимает пять соседних портов.",
                        nameof(settings));
                }
            }

            ports.Add((profile.Id, first, last));

            foreach (var job in profile.Schedule)
            {
                if (job.Type is not ("start" or "stop" or "restart" or "backup" or "command"))
                {
                    throw new ArgumentException($"Неизвестный тип задачи расписания: {job.Type}", nameof(settings));
                }

                if (job.Mode is not ("daily" or "interval"))
                {
                    throw new ArgumentException($"Неизвестный режим расписания: {job.Mode}", nameof(settings));
                }

                if (job.Mode == "daily" && !TimeOnly.TryParse(job.Time, out _))
                {
                    throw new ArgumentException($"Некорректное время задачи: {job.Time}", nameof(settings));
                }

                if (job.Mode == "interval" && job.IntervalMinutes < 1)
                {
                    throw new ArgumentException("Интервал задачи должен быть не меньше 1 минуты.", nameof(settings));
                }
            }
        }
    }

    private void WriteToDisk(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var json = Json.Serialize(settings);
        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(AppPaths.SettingsFile))
        {
            File.Replace(temp, AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, AppPaths.SettingsFile);
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch
        {
            // Игнорируем: файл мог быть занят.
        }
    }
}
