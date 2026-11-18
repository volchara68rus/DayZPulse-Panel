using System.Diagnostics;
using System.Drawing;
using DayZPulse.Infrastructure;
using DayZPulse.Service;

namespace DayZPulse.Tray;

/// <summary>
/// Значок в трее, когда панель уже обслуживается службой Windows или другим экземпляром.
/// Трей работает как пульт: открывает веб-интерфейс и управляет самой службой.
/// </summary>
internal sealed class RemoteControllerContext : ApplicationContext
{
    private readonly CommandLineOptions _options;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startServiceItem;
    private readonly ToolStripMenuItem _stopServiceItem;
    private readonly ToolStripMenuItem _restartServiceItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly Icon _okIcon;
    private readonly Icon _offIcon;
    private readonly Icon _busyIcon;
    private readonly WindowsServiceManager _serviceManager;

    private RemoteInstance _remote;
    private bool _disposed;

    public RemoteControllerContext(CommandLineOptions options, RemoteInstance remote)
    {
        _options = options;
        _remote = remote;

        using var factory = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        _serviceManager = new WindowsServiceManager(factory.CreateLogger<WindowsServiceManager>());

        _okIcon = TrayIconFactory.Create(Color.FromArgb(24, 58, 40), Color.FromArgb(80, 220, 130), true);
        _offIcon = TrayIconFactory.Create(Color.FromArgb(40, 44, 52), Color.FromArgb(150, 160, 175), false);
        _busyIcon = TrayIconFactory.Create(Color.FromArgb(60, 52, 20), Color.FromArgb(245, 195, 70), false);

        _statusItem = new ToolStripMenuItem("Панель: проверка…") { Enabled = false };
        _openItem = new ToolStripMenuItem("Открыть панель управления", null, (_, _) => OpenBrowser())
        {
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };

        _startServiceItem = new ToolStripMenuItem("Запустить службу", null, (_, _) => ServiceAction("start"));
        _stopServiceItem = new ToolStripMenuItem("Остановить службу", null, (_, _) => ServiceAction("stop"));
        _restartServiceItem = new ToolStripMenuItem("Перезапустить службу", null, (_, _) => ServiceAction("restart"));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startServiceItem);
        menu.Items.Add(_stopServiceItem);
        menu.Items.Add(_restartServiceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Открыть каталог данных", null, (_, _) => OpenPath(AppPaths.DataRoot)));
        menu.Items.Add(new ToolStripMenuItem("Открыть каталог журналов", null, (_, _) => OpenPath(AppPaths.LogsDirectory)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitApplication()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _okIcon,
            Text = "DayZPulse — панель обслуживается службой",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => OpenBrowser();

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // Первый опрос — в фоне, без блокировки UI: иначе при поднимающейся
        // службе трей «зависал» и меню не открывалось.
        RefreshAsync().ContinueWith(_ => { }, TaskScheduler.Default);

        if (!options.NoBrowser)
        {
            OpenBrowser();
        }
    }

    private string BaseUrl => _remote.BaseUrl;

    public void NotifyAttached()
    {
        _notifyIcon.ShowBalloonTip(6000, "DayZPulse",
            "Панель уже запущена (служба Windows). Трей работает как пульт управления.", ToolTipIcon.Info);
    }

    public int RunMessageLoop()
    {
        Application.Run(this);
        return 0;
    }

    private async Task RefreshAsync()
    {
        // Порт мог измениться (смена в настройках панели) — берём актуальный
        // из настроек, иначе пульт навсегда остался бы на старом адресе.
        var port = InstanceProbe.ConfiguredPort(_remote.Port);
        _remote = await InstanceProbe.ProbeAsync(port);

        var serviceRunning = InstanceProbe.IsServiceRunning();
        var installed = _serviceManager.GetStatus().Installed;

        if (_remote.Available)
        {
            _statusItem.Text = $"Панель доступна на порту {_remote.Port}";
            SetIcon(_okIcon);
        }
        else if (serviceRunning)
        {
            _statusItem.Text = "Служба запускается…";
            SetIcon(_busyIcon);
        }
        else
        {
            _statusItem.Text = installed ? "Служба остановлена" : "Панель не запущена";
            SetIcon(_offIcon);
        }

        _openItem.Enabled = _remote.Available;
        _startServiceItem.Enabled = installed && !serviceRunning;
        _stopServiceItem.Enabled = installed && serviceRunning;
        _restartServiceItem.Enabled = installed;
    }

    private void SetIcon(Icon icon)
    {
        if (!ReferenceEquals(_notifyIcon.Icon, icon))
        {
            _notifyIcon.Icon = icon;
        }
    }

    private void ServiceAction(string action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (action == "restart")
                {
                    await _serviceManager.StopAsync(CancellationToken.None);
                    await Task.Delay(2500);
                    await _serviceManager.StartAsync(CancellationToken.None);
                }
                else
                {
                    var message = action == "start"
                        ? await _serviceManager.StartAsync(CancellationToken.None)
                        : await _serviceManager.StopAsync(CancellationToken.None);
                    _notifyIcon.ShowBalloonTip(4000, "DayZPulse", message, ToolTipIcon.Info);
                }

                // Ждём, пока панель снова начнёт отвечать.
                _remote = await InstanceProbe.WaitForAsync(_remote.Port, TimeSpan.FromSeconds(40));
            }
            catch (Exception ex)
            {
                _notifyIcon.ShowBalloonTip(8000, "DayZPulse", ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                await RefreshAsync();
            }
        });
    }

    private void OpenBrowser()
    {
        // Берём актуальный порт: пользователь мог сменить его в настройках,
        // а пульт ещё не успел обновиться.
        var port = InstanceProbe.ConfiguredPort(_remote.Port);
        var url = $"http://127.0.0.1:{port}/";

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

    private void ExitApplication()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        ExitThread();
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
            _timer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _okIcon.Dispose();
            _offIcon.Dispose();
            _busyIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
