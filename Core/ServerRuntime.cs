using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Tracker;

namespace DayZPulse.Core;

/// <summary>
/// Всё, что относится к одному серверу DayZ во время работы панели:
/// процесс, консоль, менеджеры файлов и копий, трекер и админ-логи.
/// </summary>
public sealed class ServerRuntime
{
    public ServerRuntime(ServerProfile profile, SettingsStore settings, ILoggerFactory loggerFactory)
    {
        Profile = profile;

        Console = new ConsoleBuffer(settings.Current.Logging.MaxConsoleLines);
        Commands = new CommandQueue(loggerFactory.CreateLogger<CommandQueue>());
        Tracker = new TrackerStore(loggerFactory.CreateLogger<TrackerStore>());

        Supervisor = new ServerSupervisor(
            loggerFactory.CreateLogger<ServerSupervisor>(), settings, new EncodingResolver(), profile, Console);

        Configs = new ConfigFileManager(
            loggerFactory.CreateLogger<ConfigFileManager>(), profile);

        Backups = new BackupManager(
            loggerFactory.CreateLogger<BackupManager>(), profile, Supervisor);

        AdminLogs = new AdminLogService(
            loggerFactory.CreateLogger<AdminLogService>(), settings, profile, Tracker);

        Schedule = new ScheduleRunner(
            loggerFactory.CreateLogger($"Schedule[{profile.Id}]"), profile, Supervisor, Backups, Commands);
    }

    public ServerProfile Profile { get; private set; }

    public string Id => Profile.Id;

    public string Name => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Id : Profile.Name;

    public ConsoleBuffer Console { get; }
    public CommandQueue Commands { get; }
    public TrackerStore Tracker { get; }
    public ServerSupervisor Supervisor { get; }
    public AdminLogService AdminLogs { get; }
    public ConfigFileManager Configs { get; }
    public BackupManager Backups { get; }
    public ScheduleRunner Schedule { get; }

    public ServerSettings ServerSettings => Profile.Server;
    public TrackerSettings TrackerSettings => Profile.Tracker;
    public BackupSettings BackupSettings => Profile.Backups;

    /// <summary>Подготавливает журнал сервера и выполняет автозапуск при необходимости.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Supervisor.InitializeAsync(cancellationToken);

    /// <summary>Профиль мог быть изменён в настройках — подменяем во всех компонентах.</summary>
    public void UpdateProfile(ServerProfile profile)
    {
        Profile = profile;
        Supervisor.UpdateProfile(profile);
        AdminLogs.UpdateProfile(profile);
        Configs.UpdateProfile(profile);
        Backups.UpdateProfile(profile);
        Schedule.UpdateProfile(profile);
    }
}
