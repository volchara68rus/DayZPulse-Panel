using DayZPulse.Infrastructure;

namespace DayZPulse.Web;

/// <summary>
/// Проверяет наличие обновления самой панели при запуске (если включена
/// автопроверка) и сообщает интерфейсу. Установку не выполняет: обновление
/// запускает пользователь кнопкой — так он контролирует момент перезапуска.
/// </summary>
public sealed class PanelUpdateCheckService : BackgroundService
{
    private readonly PanelUpdateService _panel;
    private readonly LiveHub _hub;
    private readonly SettingsStore _settings;
    private readonly ILogger<PanelUpdateCheckService> _logger;

    public PanelUpdateCheckService(PanelUpdateService panel, LiveHub hub,
        SettingsStore settings, ILogger<PanelUpdateCheckService> logger)
    {
        _panel = panel;
        _hub = hub;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Небольшая задержка: не отвлекаем сеть, пока панель только поднимается.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_settings.Current.PanelUpdate.AutoCheck)
        {
            return;
        }

        try
        {
            var state = await _panel.GetStateAsync(stoppingToken);

            // Сообщаем интерфейсу, только когда обновление реально есть и его
            // можно применить (каталог установки доступен для записи).
            if (state is { Available: true, UpToDate: false, Writable: true })
            {
                _logger.LogInformation(
                    "Доступно обновление панели: {Remote} (установлена {Local}, файлов {Count}).",
                    state.RemoteVersion, state.InstalledVersion, state.ChangedFiles);

                _hub.Broadcast("panel-update", new
                {
                    available = true,
                    upToDate = false,
                    remoteVersion = state.RemoteVersion,
                    localVersion = state.InstalledVersion,
                    changedFiles = state.ChangedFiles
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Автопроверка обновления панели не удалась.");
        }
    }
}
