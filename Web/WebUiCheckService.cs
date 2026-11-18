using DayZPulse.Infrastructure;

namespace DayZPulse.Web;

/// <summary>
/// Проверяет наличие новой веб-части при запуске панели (если включена
/// автопроверка) и сообщает интерфейсу. Установку не выполняет: обновление
/// запускает пользователь кнопкой — так он контролирует момент.
/// </summary>
public sealed class WebUiCheckService : BackgroundService
{
    private readonly WebUiUpdateService _webUi;
    private readonly LiveHub _hub;
    private readonly SettingsStore _settings;
    private readonly ILogger<WebUiCheckService> _logger;

    public WebUiCheckService(WebUiUpdateService webUi, LiveHub hub,
        SettingsStore settings, ILogger<WebUiCheckService> logger)
    {
        _webUi = webUi;
        _hub = hub;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Небольшая задержка: не отвлекаем сеть, пока панель только поднимается.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_settings.Current.WebUi.AutoCheck)
        {
            return;
        }

        await CheckAsync(stoppingToken);
    }

    /// <summary>Разовая проверка обновления веб-части.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var manifest = await _webUi.FetchManifestAsync(ct);
            if (manifest is null)
            {
                return;
            }

            // Сообщаем интерфейсу, только когда обновление реально есть.
            if (manifest is { UpToDate: false } && !string.IsNullOrEmpty(manifest.Version))
            {
                _logger.LogInformation("Доступно обновление веб-части панели: {Version}.", manifest.Version);
                _hub.Broadcast("web-ui", new
                {
                    available = true,
                    upToDate = false,
                    remoteVersion = manifest.Version,
                    localVersion = _webUi.EffectiveVersion
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Автопроверка веб-части не удалась.");
        }
    }
}
