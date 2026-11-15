namespace DayZPulse.Models;

/// <summary>
/// Полный набор настроек приложения. Общие параметры (веб-интерфейс, владелец,
/// журналирование) хранятся здесь, а всё, что относится к конкретному серверу
/// DayZ, живёт в <see cref="ServerProfile"/>.
/// </summary>
public sealed class AppSettings
{
    public WebSettings Web { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public SteamSettings Steam { get; set; } = new();

    /// <summary>Серверы DayZ, которыми управляет панель. Может быть пустым до добавления первого.</summary>
    public List<ServerProfile> Servers { get; set; } = [];

    /// <summary>Идентификатор сервера, открытого по умолчанию.</summary>
    public string? DefaultServerId { get; set; }

    /// <summary>
    /// Папка сервера, выбранная при первом запуске. Пустая по умолчанию:
    /// пользователь указывает её сам, и она же задаёт каталог SteamCMD.
    /// </summary>
    public string DefaultServerDirectory { get; set; } = "";

    /// <summary>
    /// Настройки одиночного сервера из версий до мультисерверности.
    /// Читаются только для переноса и больше не записываются.
    /// </summary>
    public ServerSettings? Server { get; set; }
    public TrackerSettings? Tracker { get; set; }
    public BackupSettings? Backups { get; set; }
    public List<ConfigFileEntry>? ConfigFiles { get; set; }
    public List<ScheduledJob>? Schedule { get; set; }
}

/// <summary>Один сервер DayZ: свои настройки, мод, копии и расписание.</summary>
public sealed class ServerProfile
{
    /// <summary>Короткий идентификатор для API и адресов (латиница, цифры, «-», «_»).</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "Сервер DayZ";

    public bool Enabled { get; set; } = true;

    /// <summary>Цвет метки в интерфейсе (HEX).</summary>
    public string Color { get; set; } = "#3fb950";

    /// <summary>Карта сервера: chernarusplus, enoch, sakhal, namalsk и т.п.</summary>
    public string Map { get; set; } = "chernarusplus";

    public ServerSettings Server { get; set; } = new();
    public TrackerSettings Tracker { get; set; } = new();
    public BackupSettings Backups { get; set; } = new();
    public List<ConfigFileEntry> ConfigFiles { get; set; } = CreateDefaultConfigFiles();
    public List<ScheduledJob> Schedule { get; set; } = [];

    /// <summary>Список модов сервера: Workshop и локальные.</summary>
    public List<ServerMod> ModList { get; set; } = [];

    /// <summary>Настройки работы с модами (автообновление и т.п.).</summary>
    public ModSettings ModSettings { get; set; } = new();

    /// <summary>Стартовые наборы новых игроков (общий и по полу).</summary>
    public SpawnKits SpawnKits { get; set; } = new();

    /// <summary>Пресеты для выдачи игрокам на время.</summary>
    public List<GrantPreset> GrantPresets { get; set; } = [];

    public static List<ConfigFileEntry> CreateDefaultConfigFiles() =>
    [
        new ConfigFileEntry { Name = "Конфигурация сервера", Path = "serverDZ.cfg", Description = "Основной файл настроек DayZ" },
        new ConfigFileEntry { Name = "Бан-лист", Path = "bans.txt", Description = "Список забаненных игроков" },
        new ConfigFileEntry { Name = "Погода", Path = "cfggameplay.json", Description = "Игровые настройки" }
    ];

    /// <summary>Приводит идентификатор к безопасному виду.</summary>
    public static string Slugify(string value, string fallback = "server")
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in (value ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (ch is '-' or '_' or ' ' or '.')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length is 0 or > 40)
        {
            slug = slug.Length > 40 ? slug[..40].Trim('-') : fallback;
        }

        return slug.Length > 0 ? slug : fallback;
    }
}

/// <summary>Источник мода: из Steam Workshop или локальная папка.</summary>
public enum ModSource
{
    Workshop,
    Local
}

/// <summary>Один мод сервера: ссылка/папка, папка установки, автообновление.</summary>
public sealed class ServerMod
{
    /// <summary>Стабильный идентификатор записи.</summary>
    public string Id { get; set; } = "";

    /// <summary>Workshop ID (для модов из мастерской).</summary>
    public string WorkshopId { get; set; } = "";

    /// <summary>Человеческое название мода.</summary>
    public string Name { get; set; } = "";

    public ModSource Source { get; set; } = ModSource.Workshop;

    /// <summary>
    /// Символическая папка мода на сервере (например <c>@CF</c>). Именно она
    /// идёт в <c>-mod=</c>. Пусто — папка ещё не определена.
    /// </summary>
    public string Folder { get; set; } = "";

    /// <summary>Для локального мода — исходный путь, откуда он подключён/скопирован.</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>
    /// Серверный мод: подключается через <c>-serverMod=</c> и нужен только серверу.
    /// Иначе — клиентский мод (<c>-mod=</c>), его должны поставить и игроки.
    /// </summary>
    public bool ServerOnly { get; set; }

    /// <summary>Автоматически обновлять из Workshop.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Подключать в параметры запуска сервера (-mod= / -serverMod=).</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset? InstalledAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Общие настройки модов сервера.</summary>
public sealed class ModSettings
{
    /// <summary>Обновлять моды из Workshop при запуске сервера.</summary>
    public bool UpdateOnStart { get; set; } = true;

    /// <summary>Проверять обновления раз в сутки в фоне.</summary>
    public bool AutoUpdateDaily { get; set; } = true;

    /// <summary>Удалять временные файлы Steam после загрузки.</summary>
    public bool CleanupAfterDownload { get; set; } = true;
}

public sealed class TrackerSettings
{
    /// <summary>Принимать данные от мода DayZPulse.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Токен, который мод передаёт в заголовке X-Api-Token. У каждого сервера свой.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Идентификатор сервера в моде (SERVER_ID в config.json).</summary>
    public string ServerId { get; set; } = "";

    /// <summary>Считать данные устаревшими через столько секунд.</summary>
    public int StaleSeconds { get; set; } = 30;

    /// <summary>Хранить историю позиций, дней.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Автоматически писать config.json для мода в profiles/DayZPulseCore.</summary>
    public bool AutoConfigureMod { get; set; } = true;

    /// <summary>Читать админ-логи для килфида и чата.</summary>
    public bool ReadAdminLogs { get; set; } = true;

    /// <summary>Показывать демонстрационного игрока (для проверки интерфейса).</summary>
    public bool DemoPlayer { get; set; }
}

public sealed class SteamSettings
{
    /// <summary>Аккаунт Steam, под которым выполняется вход (для SteamCMD).</summary>
    public string Username { get; set; } = "";
    /// <summary>Вход выполнен и сессия сохранена.</summary>
    public bool LoggedIn { get; set; }

    /// <summary>Когда выполнялся последний удачный вход.</summary>
    public DateTimeOffset? LoggedInAt { get; set; }

    /// <summary>Последняя ошибка входа (например, требуется код Steam Guard).</summary>
    public string? LastError { get; set; }

    /// <summary>Каталог SteamCMD. Пусто = каталог данных панели + steamcmd.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Скачивать и обновлять SteamCMD автоматически.</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>SteamCMD установлен и готов к работе (по версии файла).</summary>
    public bool Installed { get; set; }

    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Базовый адрес сайта с модом панели (dayzpulse.ru по умолчанию).</summary>
    public string PanelModBaseUrl { get; set; } = "https://dayzpulse.ru";

    /// <summary>Установленная версия мода панели.</summary>
    public string PanelModVersion { get; set; } = "";

    public DateTimeOffset? PanelModUpdatedAt { get; set; }
}

public sealed class WebSettings
{
    /// <summary>Порт локального веб-интерфейса.</summary>
    public int Port { get; set; } = 8730;

    /// <summary>Адрес прослушивания. По умолчанию только локальная машина.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Разрешить доступ из локальной сети (не только с этой машины).</summary>
    public bool AllowRemote { get; set; }

    /// <summary>Открывать браузер при старте в режиме трея.</summary>
    public bool OpenBrowserOnStart { get; set; } = true;

    public string Url => $"http://{(AllowRemote ? "0.0.0.0" : BindAddress)}:{Port}";
}

/// <summary>Учётная запись администратора панели с ограниченными правами.</summary>
public sealed class AdminAccount
{
    /// <summary>Стабильный идентификатор (латиница/цифры), по нему правят запись.</summary>
    public string Id { get; set; } = "";

    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int PasswordIterations { get; set; } = 210_000;

    public bool Enabled { get; set; } = true;

    /// <summary>Доступ ко всем разделам (владелец всегда).</summary>
    public bool AllSections { get; set; }

    /// <summary>Разрешённые разделы панели (ключи из VIEWS: dashboard, console, …).</summary>
    public List<string> Sections { get; set; } = [];

    /// <summary>
    /// Разрешённые серверы (id профиля). Пусто и <see cref="AllServers"/> = false —
    /// доступ ко всем серверам панели.
    /// </summary>
    public List<string> Servers { get; set; } = [];

    /// <summary>Доступ ко всем серверам панели.</summary>
    public bool AllServers { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class AuthSettings
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int PasswordIterations { get; set; } = 600_000;
    public int SessionHours { get; set; } = 12;

    /// <summary>Владелец панели создан (первый запуск пройден).</summary>
    public bool OwnerCreated { get; set; }

    public string? OwnerName { get; set; }
    public DateTimeOffset? OwnerCreatedAt { get; set; }

    /// <summary>Администраторы с ограниченными правами (владелец хранится отдельно).</summary>
    public List<AdminAccount> Admins { get; set; } = [];
}

public sealed class ServerSettings
{
    /// <summary>Базовая папка сервера DayZ. Меняется в веб-интерфейсе.</summary>
    public const string DefaultBaseDirectory = @"C:\DayZServer";

    /// <summary>Путь к исполняемому файлу сервера. Пусто = файл ищется в базовой папке.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Базовая папка сервера. Пусто = значение по умолчанию C:\DayZServer.</summary>
    public string BaseDirectory { get; set; } = DefaultBaseDirectory;

    /// <summary>
    /// Разрешить каталоги вне базовой папки
    /// (например, чтобы указать exe в другом месте на диске).
    /// </summary>
    public bool AllowDirectoryOutsideBase { get; set; }

    /// <summary>Рабочий каталог. Пусто = базовая папка.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Имя исполняемого файла в базовой папке, если путь не задан явно.</summary>
    public string ExecutableName { get; set; } = "DayZServer_x64.exe";

    /// <summary>Аргументы запуска. Поддерживаются токены {exe} {workingDir} {port} {config} {profiles} {mods}.</summary>
    public string Arguments { get; set; } = "-config={config} -port={port} -profiles={profiles} -dologs -adminlog -netlog -freezecheck";

    public string ConfigFile { get; set; } = "serverDZ.cfg";
    public string ProfilesDirectory { get; set; } = "profiles";
    public int Port { get; set; } = 2302;
    public string Mods { get; set; } = "";

    /// <summary>Серверные моды (<c>-serverMod=</c>): только на сервере.</summary>
    public string ServerMods { get; set; } = "";

    public string ExtraArguments { get; set; } = "";

    /// <summary>Команда, отправляемая в stdin процесса при корректной остановке.</summary>
    public string StopCommand { get; set; } = "quit";
    public bool UseStopCommand { get; set; }
    public bool RedirectStandardInput { get; set; } = true;
    public int GracefulStopSeconds { get; set; } = 20;

    // ------------------------------------------------------------- RCON
    // BattlEye RCON: живая консоль сервера, команды (say, kick, ban…),
    // список игроков. Пароль и порт хранятся в battleye\beserver_x64.cfg.

    /// <summary>Порт RCON. 0 = порт игры + 4.</summary>
    public int RconPort { get; set; }

    /// <summary>Пароль RCON. Пусто = сгенерировать и записать в beserver_x64.cfg.</summary>
    public string RconPassword { get; set; } = "";

    /// <summary>Подключаться к RCON автоматически при запуске сервера.</summary>
    public bool RconEnabled { get; set; } = true;

    public bool StartOnLaunch { get; set; }
    public bool AutoRestartOnCrash { get; set; } = true;
    public int AutoRestartDelaySeconds { get; set; } = 15;
    public int MaxAutoRestarts { get; set; } = 5;
    public int AutoRestartWindowMinutes { get; set; } = 30;

    /// <summary>Кодировка вывода сервера: utf-8, windows-1251, oem (866) или system.</summary>
    public string ConsoleEncoding { get; set; } = "utf-8";

    /// <summary>Приоритет процесса: Normal, AboveNormal, High, BelowNormal, Idle.</summary>
    public string ProcessPriority { get; set; } = "AboveNormal";

    public bool ClearConsoleOnStart { get; set; } = true;

    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

public sealed class BackupSettings
{
    public bool Enabled { get; set; } = true;
    public int RetentionCount { get; set; } = 10;

    /// <summary>Что включать в архив: пути относительно рабочего каталога сервера.</summary>
    public List<string> Includes { get; set; } = ["serverDZ.cfg", "profiles", "mpmissions", "bans.txt"];

    public List<string> ExcludePatterns { get; set; } = ["*.tmp", "*.pid", "*.rpt.tmp"];

    /// <summary>Fastest, Optimal или SmallestSize.</summary>
    public string CompressionLevel { get; set; } = "Fastest";

    /// <summary>Останавливать сервер на время создания резервной копии.</summary>
    public bool StopServerDuringBackup { get; set; }

    /// <summary>Резервное копирование логов сервера.</summary>
    public LogBackupSettings Logs { get; set; } = new();
}

/// <summary>Настройки бэкапов и хранения логов сервера.</summary>
public sealed class LogBackupSettings
{
    /// <summary>Включить резервное копирование логов.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Архивировать существующие логи при запуске сервера и начинать с чистых:
    /// старые логи упаковываются в zip и переносятся в резервные копии.
    /// </summary>
    public bool OnStart { get; set; } = true;

    /// <summary>Создавать копию по расписанию (интервал в часах).</summary>
    public int IntervalHours { get; set; } = 12;

    /// <summary>Хранить копий логов.</summary>
    public int RetentionCount { get; set; } = 5;

    /// <summary>Хранить сами логи сервера, дней (0 — не удалять).</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>Сжимать копии логов в zip.</summary>
    public bool Compress { get; set; } = true;
}

public sealed class LoggingSettings
{
    public int MaxConsoleLines { get; set; } = 5000;
    public bool WriteServerLogFile { get; set; } = true;
    public int LogRetentionDays { get; set; } = 14;
    public string MinimumLevel { get; set; } = "Information";
}

public sealed class ConfigFileEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Editable { get; set; } = true;
    public bool ReadOnly { get; set; }
}

public sealed class ScheduledJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    /// <summary>start | stop | restart | backup | command</summary>
    public string Type { get; set; } = "backup";
    public bool Enabled { get; set; }
    /// <summary>daily | interval</summary>
    public string Mode { get; set; } = "daily";
    /// <summary>Время запуска в формате HH:mm (для режима daily).</summary>
    public string Time { get; set; } = "04:00";
    public int IntervalMinutes { get; set; } = 360;
    /// <summary>Доп. данные: текст команды для типа command.</summary>
    public string Payload { get; set; } = "";
    public DateTimeOffset? LastRun { get; set; }
}
