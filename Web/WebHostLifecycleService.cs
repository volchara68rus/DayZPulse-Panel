using DayZPulse.Hosting;
using DayZPulse.Service;

namespace DayZPulse.Web;

/// <summary>Реагирует на запросы перезапуска и завершения приложения.</summary>
public sealed class WebHostLifecycleService : BackgroundService
{
    private readonly AppLifecycle _lifecycle;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WebHostLifecycleService> _logger;
    private readonly bool _isWindowsService;

    public WebHostLifecycleService(AppLifecycle lifecycle, IHostApplicationLifetime lifetime,
        ILogger<WebHostLifecycleService> logger, IHostEnvironment environment)
    {
        _lifecycle = lifecycle;
        _lifetime = lifetime;
        _logger = logger;
        _isWindowsService = !environment.IsDevelopment() &&
                            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                                System.Runtime.InteropServices.OSPlatform.Windows) &&
                            !Environment.UserInteractive;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lifecycle.RestartRequestedEvent += OnRestartRequested;
        _lifecycle.ShutdownRequested += OnShutdownRequested;
        _lifecycle.ApplyUpdateRequested += OnApplyUpdateRequested;
        return Task.CompletedTask;
    }

    private void OnRestartRequested(object? sender, EventArgs e)
    {
        _logger.LogInformation("Перезапуск приложения. Режим службы: {IsService}", _isWindowsService);
        if (_isWindowsService)
        {
            AppLifecycle.ScheduleServiceRestart(WindowsServiceManager.ServiceName);
        }

        // Небольшая задержка, чтобы ответ успел уйти клиенту.
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            _lifetime.StopApplication();
        });
    }

    /// <summary>
    /// Обновление панели: просто завершаем процесс. Файлы подменит внешний
    /// помощник (он же поднимет панель) — в режиме службы через <c>sc start</c>,
    /// в режиме трея запуском exe. Сами ничего не перезапускаем.
    /// </summary>
    private void OnApplyUpdateRequested(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            _lifetime.StopApplication();
        });
    }

    private void OnShutdownRequested(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            _lifetime.StopApplication();
        });
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _lifecycle.RestartRequestedEvent -= OnRestartRequested;
        _lifecycle.ShutdownRequested -= OnShutdownRequested;
        return base.StopAsync(cancellationToken);
    }
}
