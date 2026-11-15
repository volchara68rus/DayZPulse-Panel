using System.Diagnostics;
using DayZPulse.Core;
using DayZPulse.Hosting;
using DayZPulse.Infrastructure;
using DayZPulse.Service;
using Microsoft.AspNetCore.Builder;

namespace DayZPulse.Tray;

/// <summary>Иконка в области уведомлений и меню управления локальной панелью.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WebApplication _app;
    private readonly CommandLineOptions _options;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _pulseTimer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _serviceItem;
    private readonly ToolStripMenuItem _serversItem;
    private readonly HashSet<string> _notifiedRunning = new(StringComparer.OrdinalIgnoreCase);

    private readonly Icon _idleIcon;
    private readonly Icon _runningIcon;
    private readonly Icon _busyIcon;
    private readonly Icon _errorIcon;
    private readonly SynchronizationContext _uiContext;

    private string _lastState = "";
    private bool _disposed;

    public TrayApplicationContext(WebApplication app, CommandLineOptions options)
    {
        _app = app;
        _options = options;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _idleIcon = TrayIconFactory.Create(Color.FromArgb(40, 44, 52), Color.FromArgb(150, 160, 175), false);
        _runningIcon = TrayIconFactory.Create(Color.FromArgb(24, 58, 40), Color.FromArgb(80, 220, 130), true);
        _busyIcon = TrayIconFactory.Create(Color.FromArgb(60, 52, 20), Color.FromArgb(245, 195, 70), false);
        _errorIcon = TrayIconFactory.Create(Color.FromArgb(62, 26, 26), Color.FromArgb(240, 95, 95), false);

        _statusItem = new ToolStripMenuItem("Состояние: загрузка…") { Enabled = false };
        _serversItem = new ToolStripMenuItem("Серверы");
        _startItem = new ToolStripMenuItem("Запустить сервер", null, async (_, _) => await GuardedAsync(s => s.StartAsync("трей", CancellationToken.None)));
        _stopItem = new ToolStripMenuItem("Остановить сервер", null, async (_, _) => await GuardedAsync(s => s.StopAsync("трей", CancellationToken.None)));
        _restartItem = new ToolStripMenuItem("Перезапустить сервер", null, async (_, _) => await GuardedAsync(s => s.RestartAsync("трей", CancellationToken.None)));
        _autostartItem = new ToolStripMenuItem("Автозапуск при входе в систему", null, OnToggleAutostart);
        _serviceItem = new ToolStripMenuItem("Настроить службу Windows…", null, OnOpenServicePage);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Открыть панель управления", null, (_, _) => OpenBrowser()) { Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_statusItem);
        menu.Items.Add(_serversItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Открыть каталог данных", null, (_, _) => OpenPath(AppPaths.DataRoot)));
        menu.Items.Add(new ToolStripMenuItem("Открыть каталог журналов", null, (_, _) => OpenPath(AppPaths.LogsDirectory)));
        menu.Items.Add(_serviceItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitApplication()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "DayZPulse",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => OpenBrowser();
        _notifyIcon.BalloonTipClicked += (_, _) => OpenBrowser();

        var autostart = TryReadAutostart();
        _autostartItem.Checked = autostart;
        _autostartItem.CheckOnClick = true;

        var lifecycle = _app.Services.GetRequiredService<AppLifecycle>();
        lifecycle.RestartRequestedEvent += (_, _) => _uiContext.Post(_ => RestartApplication(), null);

        _pulseTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _pulseTimer.Tick += (_, _) => RefreshStatus();
        _pulseTimer.Start();

        RefreshStatus();

        if (!_options.NoBrowser && _app.Services.GetRequiredService<SettingsStore>().Current.Web.OpenBrowserOnStart)
        {
            OpenBrowser();
        }
    }

    private string BaseUrl
    {
        get
        {
            var web = _app.Services.GetRequiredService<SettingsStore>().Current.Web;
            var host = web.AllowRemote ? "localhost" : web.BindAddress;
            return $"http://{host}:{web.Port}";
        }
    }

    public void NotifyDefaultCredentials(bool created, string username, string password)
    {
        // Учётных данных по умолчанию больше нет: владелец создаётся на странице /setup.html.
    }

    /// <summary>Запускает цикл обработки сообщений WinForms.</summary>
    public int RunMessageLoop()
    {
        Application.Run(this);
        return 0;
    }

    private async Task GuardedAsync(Func<ServerSupervisor, Task> action)
    {
        var supervisor = CurrentSupervisor();
        if (supervisor is null)
        {
            return;
        }

        try
        {
            await action(supervisor);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(8000, "DayZPulse", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            RefreshStatus();
        }
    }

    private ServerRegistry Registry => _app.Services.GetRequiredService<ServerRegistry>();

    private ServerSupervisor? CurrentSupervisor() => Registry.All.FirstOrDefault()?.Supervisor;

    private void RefreshStatus()
    {
        try
        {
            var runtimes = Registry.All;
            if (runtimes.Count == 0)
            {
                return;
            }

            RebuildServerMenu(runtimes);

            var running = runtimes.Where(r => r.Supervisor.IsRunning).ToArray();
            var crashed = runtimes.Where(r => r.Supervisor.Status.State is ServerState.Crashed or ServerState.Failed).ToArray();

            _statusItem.Text = runtimes.Count == 1
                ? DescribeSingle(runtimes[0])
                : $"Серверов: {runtimes.Count}, работает: {running.Length}";

            if (running.Length > 0)
            {
                _statusItem.Text += $" · игроков: {runtimes.Sum(r => r.Tracker.LivePlayers().Count(p => p.Alive))}";
            }

            var first = runtimes[0];
            _startItem.Enabled = !first.Supervisor.IsRunning;
            _stopItem.Enabled = first.Supervisor.IsRunning;
            _restartItem.Enabled = first.Supervisor.IsRunning;

            var icon = crashed.Length > 0 ? _errorIcon
                : running.Length == runtimes.Count ? _runningIcon
                : running.Length > 0 ? _busyIcon
                : _idleIcon;

            if (!ReferenceEquals(_notifyIcon.Icon, icon))
            {
                _notifyIcon.Icon = icon;
            }

            _notifyIcon.Text = running.Length > 0
                ? $"DayZPulse — работает серверов: {running.Length} из {runtimes.Count}"
                : $"DayZPulse — все серверы остановлены ({runtimes.Count})";

            var stateKey = string.Join("|", runtimes.Select(r => $"{r.Id}:{r.Supervisor.Status.State}:{r.Supervisor.Status.ProcessId}"));
            if (stateKey != _lastState)
            {
                _lastState = stateKey;

                foreach (var runtime in runtimes)
                {
                    if (runtime.Supervisor.Status.State != ServerState.Running || !_notifiedRunning.Add(runtime.Id))
                    {
                        continue;
                    }

                    _notifyIcon.ShowBalloonTip(4000, "DayZPulse",
                        $"Сервер «{runtime.Name}» запущен (PID {runtime.Supervisor.Status.ProcessId}).", ToolTipIcon.Info);
                }
            }
        }
        catch
        {
            // Статус может быть недоступен в момент запуска/останова.
        }
    }

    private static string DescribeSingle(ServerRuntime runtime)
    {
        var status = runtime.Supervisor.Status;
        return status.State switch
        {
            ServerState.Running =>
                $"{runtime.Name}: работает (PID {status.ProcessId}, CPU {status.CpuPercent:0.#}%)",
            ServerState.Crashed => $"{runtime.Name}: аварийно завершился (код {status.ExitCode})",
            _ => $"{runtime.Name}: {ApiEndpointsText(status.State)}"
        };
    }

    /// <summary>Подменю со списком серверов: у каждого команды запуска, остановки и открытие панели.</summary>
    private void RebuildServerMenu(IReadOnlyList<ServerRuntime> runtimes)
    {
        _serversItem.DropDownItems.Clear();
        _serversItem.Text = runtimes.Count == 1 ? "Серверы (1)" : $"Серверы ({runtimes.Count})";

        foreach (var runtime in runtimes)
        {
            var status = runtime.Supervisor.Status;
            var players = runtime.Tracker.LivePlayers().Count(p => p.Alive);

            var header = new ToolStripMenuItem(runtime.Name)
            {
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };

            header.DropDownItems.Add(new ToolStripMenuItem(
                $"Состояние: {ApiEndpointsText(status.State)}") { Enabled = false });

            if (status.ProcessId is not null)
            {
                header.DropDownItems.Add(new ToolStripMenuItem(
                    $"PID {status.ProcessId} · CPU {status.CpuPercent:0.#}% · RAM {FormatBytes(status.WorkingSetBytes)}") { Enabled = false });
            }

            header.DropDownItems.Add(new ToolStripMenuItem(
                runtime.Tracker.IsConnected(runtime.TrackerSettings.StaleSeconds)
                    ? $"Игроков онлайн: {players}"
                    : "Мод не на связи") { Enabled = false });

            header.DropDownItems.Add(new ToolStripSeparator());
            header.DropDownItems.Add(new ToolStripMenuItem("Открыть панель", null,
                (_, _) => OpenBrowser($"#players")
            ));

            var id = runtime.Id;

            header.DropDownItems.Add(new ToolStripMenuItem("Запустить", null,
                async (_, _) => await GuardedServerAsync(Registry.Get(id), s => s.StartAsync($"трей:{id}", CancellationToken.None))));
            header.DropDownItems.Add(new ToolStripMenuItem("Остановить", null,
                async (_, _) => await GuardedServerAsync(Registry.Get(id), s => s.StopAsync($"трей:{id}", CancellationToken.None))));
            header.DropDownItems.Add(new ToolStripMenuItem("Перезапустить", null,
                async (_, _) => await GuardedServerAsync(Registry.Get(id), s => s.RestartAsync($"трей:{id}", CancellationToken.None))));

            _serversItem.DropDownItems.Add(header);
        }
    }

    private async Task GuardedServerAsync(ServerRuntime? runtime, Func<ServerSupervisor, Task> action)
    {
        if (runtime is null)
        {
            return;
        }

        try
        {
            await action(runtime.Supervisor);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(8000, "DayZPulse", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            RefreshStatus();
        }
    }

    private void OnToggleAutostart(object? sender, EventArgs e)
    {
        try
        {
            AutostartManager.Set(_autostartItem.Checked);
            _notifyIcon.ShowBalloonTip(4000, "DayZPulse",
                _autostartItem.Checked ? "Автозапуск включён." : "Автозапуск отключён.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _autostartItem.Checked = !_autostartItem.Checked;
            _notifyIcon.ShowBalloonTip(6000, "DayZPulse", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnOpenServicePage(object? sender, EventArgs e)
    {
        try
        {
            var manager = _app.Services.GetRequiredService<WindowsServiceManager>();
            var status = manager.GetStatus();
            var summary = status.Installed
                ? $"Служба: {status.DisplayName}\nСостояние: {status.State}\nТип запуска: {status.StartMode}"
                : "Служба DayZPulse не установлена.";

            if (!manager.IsElevated)
            {
                summary += "\n\nУправление службой требует прав администратора.";
            }

            OpenBrowser("/service.html");
            _notifyIcon.ShowBalloonTip(6000, "DayZPulse", summary, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(6000, "DayZPulse", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OpenBrowser(string? path = null)
    {
        var url = BaseUrl + (path ?? "/");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(8000, "DayZPulse",
                $"Не удалось открыть браузер: {ex.Message}\nАдрес панели: {url}", ToolTipIcon.Warning);
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // Проводник недоступен.
        }
    }

    private void RestartApplication()
    {
        _pulseTimer.Stop();

        // Освобождаем порт, затем запускаем новый экземпляр приложения.
        try
        {
            _app.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch
        {
            // Игнорируем ошибки остановки.
        }

        try
        {
            ((IAsyncDisposable)_app).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Игнорируем.
        }

        _notifyIcon.Visible = false;

        var forwarded = _options.Raw.Where(a => a is not "--service").ToArray();
        AppLifecycle.SpawnReplacementProcess(forwarded);

        ExitThread();
    }

    private void ExitApplication()
    {
        _pulseTimer.Stop();
        _notifyIcon.Visible = false;

        try
        {
            _app.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch
        {
            // Игнорируем ошибки остановки.
        }

        try
        {
            ((IAsyncDisposable)_app).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Игнорируем.
        }

        ExitThread();
    }

    private static bool TryReadAutostart()
    {
        try
        {
            return AutostartManager.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    private static string ApiEndpointsText(ServerState state) => state switch
    {
        ServerState.Stopped => "остановлен",
        ServerState.Starting => "запускается",
        ServerState.Running => "работает",
        ServerState.Stopping => "останавливается",
        ServerState.Restarting => "перезапускается",
        ServerState.Crashed => "аварийно завершился",
        ServerState.Failed => "ошибка запуска",
        _ => state.ToString().ToLowerInvariant()
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _pulseTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _idleIcon.Dispose();
            _runningIcon.Dispose();
            _busyIcon.Dispose();
            _errorIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
