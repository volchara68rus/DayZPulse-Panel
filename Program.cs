using System.Runtime.InteropServices;
using DayZPulse.Bootstrap;
using DayZPulse.Hosting;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Service;
using DayZPulse.Tray;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace DayZPulse;

internal static class Program
{
    private const string MutexName = "Global\\DayZPulse.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = CommandLineOptions.Parse(args);

        if (options.ShowHelp)
        {
            WriteConsole(HelpText);
            return 0;
        }

        if (options.ShowVersion)
        {
            WriteConsole($"DayZPulse {typeof(Program).Assembly.GetName().Version}");
            return 0;
        }

        try
        {
            AppPaths.Initialize(options.DataDirectory);
            AppPaths.MigrateLegacySettings();

            return options.Command switch
            {
                "install-service" => RunServiceCommandAsync(() => InstallServiceAsync(options)).GetAwaiter().GetResult(),
                "uninstall-service" => RunServiceCommandAsync(UninstallServiceAsync).GetAwaiter().GetResult(),
                "service-status" => RunServiceCommandAsync(ServiceStatusAsync).GetAwaiter().GetResult(),
                "start-service" => RunServiceCommandAsync(StartServiceAsync).GetAwaiter().GetResult(),
                "stop-service" => RunServiceCommandAsync(StopServiceAsync).GetAwaiter().GetResult(),
                "open" => OpenPanel(options),
                _ => RunHost(options)
            };
        }
        catch (Exception ex)
        {
            WriteError(ex);
            return 1;
        }
    }

    private static int RunHost(CommandLineOptions options)
    {
        var isWindowsService = options.Service || WindowsServiceHelpers.IsWindowsService();

        if (isWindowsService)
        {
            return RunAsService(options);
        }

        AppPaths.Initialize(options.DataDirectory);

        // Если панель уже поднята службой или другим экземпляром — работаем как пульт.
        var configuredPort = options.Port ?? ReadConfiguredPort();
        var remote = InstanceProbe.ProbeAsync(configuredPort).GetAwaiter().GetResult();

        // Наличие запущенной службы уже означает, что панелью владеет она.
        // Не ждём её ответа синхронно: после обновления служба поднимается
        // несколько секунд, и ожидание подвешивало трей. Пульт сам опросит
        // панель в фоне и покажет статус, как только она ответит.
        var serviceRunning = InstanceProbe.IsServiceRunning();

        if (remote.Available || serviceRunning)
        {
            return RunAsRemoteController(options, remote);
        }

        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            WriteError(new InvalidOperationException(
                "DayZPulse уже запущен. Закройте его через значок в трее."));
            return 2;
        }

        return RunAsTrayApplication(options);
    }

    private static int RunAsService(CommandLineOptions options)
    {
        AppPaths.Initialize(options.DataDirectory);

        // В режиме службы консоль пишется в журнал приложения и системный журнал событий.
        var overrides = new RuntimeOverrides(options.Port, null);
        var app = AppBootstrap.Build(options.ToArray(), isWindowsService: true, consoleSink: null, overrides);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DayZPulse.Service");
        var settings = app.Services.GetRequiredService<SettingsStore>();
        FirstRunSetup.EnsureServerDirectories(settings);

        if (FirstRunSetup.IsSetupRequired(settings))
        {
            logger.LogWarning("Владелец панели ещё не создан. Откройте {Address}/setup.html и создайте владельца.",
                settings.Current.Web.Url);
        }

        var address = settings.Current.Web.Url;
        logger.LogInformation("DayZPulse запущен как служба Windows. Интерфейс: {Address}", address);

        app.Run();
        return 0;
    }

    private static int RunAsTrayApplication(CommandLineOptions options)
    {
        var overrides = new RuntimeOverrides(options.Port, options.NoBrowser ? false : null);
        var app = AppBootstrap.Build(options.ToArray(), isWindowsService: false,
            consoleSink: options.Console ? Console.WriteLine : null,
            overrides);

        app.Start();

        var settings = app.Services.GetRequiredService<SettingsStore>();
        FirstRunSetup.EnsureServerDirectories(settings);

        using var context = new TrayApplicationContext(app, options);
        return context.RunMessageLoop();
    }

    private static int ReadConfiguredPort()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = Json.Deserialize<Models.AppSettings>(json);
                if (settings?.Web.Port is > 0)
                {
                    return settings.Web.Port;
                }
            }
        }
        catch
        {
            // Используем порт по умолчанию.
        }

        return 8730;
    }

    private static int RunAsRemoteController(CommandLineOptions options, RemoteInstance remote)
    {
        using var context = new RemoteControllerContext(options, remote);
        context.NotifyAttached();
        return context.RunMessageLoop();
    }

    /// <summary>
    /// Открывает панель в браузере на актуальном адресе. Так делают ярлыки:
    /// порт читается из настроек при каждом запуске, поэтому смена порта не
    /// ломает ярлыки (жёсткий <c>http://127.0.0.1:8730</c> после смены вёл бы в никуда).
    /// </summary>
    private static int OpenPanel(CommandLineOptions options)
    {
        AppPaths.Initialize(options.DataDirectory);

        var port = options.Port ?? ReadConfiguredPort();
        var url = $"http://127.0.0.1:{port}/";

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Браузер недоступен — просто выходим без ошибки.
        }

        return 0;
    }

    private static async Task<int> InstallServiceAsync(CommandLineOptions options)
    {
        using var factory = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        var manager = new WindowsServiceManager(factory.CreateLogger<WindowsServiceManager>());
        var arguments = options.ServiceArguments ?? BuildServiceArguments(options);
        WriteConsole(await manager.InstallAsync(arguments, CancellationToken.None));
        return 0;
    }

    private static string BuildServiceArguments(CommandLineOptions options)
    {
        var parts = new List<string> { "--service" };
        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            parts.Add($"--data-dir=\"{options.DataDirectory}\"");
        }

        if (options.Port is > 0)
        {
            parts.Add($"--port={options.Port}");
        }

        return string.Join(' ', parts);
    }

    private static async Task<int> UninstallServiceAsync()
    {
        using var factory = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        var manager = new WindowsServiceManager(factory.CreateLogger<WindowsServiceManager>());
        WriteConsole(await manager.UninstallAsync(CancellationToken.None));
        return 0;
    }

    private static async Task<int> ServiceStatusAsync()
    {
        using var factory = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        var manager = new WindowsServiceManager(factory.CreateLogger<WindowsServiceManager>());
        var status = manager.GetStatus();
        WriteConsole(status.Installed
            ? $"Служба: {status.DisplayName}\nСостояние: {status.State}\nТип запуска: {status.StartMode}\nКоманда: {status.ExecutablePath}"
            : $"Служба «{WindowsServiceManager.ServiceName}» не установлена.");
        WriteConsole(manager.IsElevated ? "Права администратора: да" : "Права администратора: нет");
        return 0;
    }

    private static async Task<int> StartServiceAsync()
    {
        using var factory = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        var manager = new WindowsServiceManager(factory.CreateLogger<WindowsServiceManager>());
        WriteConsole(await manager.StartAsync(CancellationToken.None));
        return 0;
    }

    private static async Task<int> StopServiceAsync()
    {
        using var factory = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        var manager = new WindowsServiceManager(factory.CreateLogger<WindowsServiceManager>());
        WriteConsole(await manager.StopAsync(CancellationToken.None));
        return 0;
    }

    private static Task<int> RunServiceCommandAsync(Func<Task<int>> action)
    {
        if (!OperatingSystem.IsWindows())
        {
            WriteError(new PlatformNotSupportedException("Управление службой доступно только в Windows."));
            return Task.FromResult(1);
        }

        return action();
    }

    // ------------------------------------------------------------- вывод в консоль

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private static void WriteConsole(string text)
    {
        EnsureConsole();
        Console.WriteLine(text);
    }

    private static void WriteError(Exception exception)
    {
        EnsureConsole();
        Console.Error.WriteLine("Ошибка: " + exception.Message);
    }

    private static void EnsureConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (!AttachConsole(AttachParentProcess) && !AllocConsole())
            {
                // Консоли нет (запуск из проводника) — дополнительно показываем диалог.
                ShowMessage("DayZPulse запущен. Подробности смотрите в журнале приложения.");
            }
        }
        catch
        {
            // Консоль недоступна.
        }
    }

    private static void ShowMessage(string text)
    {
        try
        {
            System.Windows.Forms.MessageBox.Show(text, "DayZPulse",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
        }
        catch
        {
            // Даже диалог недоступен.
        }
    }

    private const string HelpText = """
        DayZPulse — панель управления игровым сервером с локальным веб-интерфейсом.

        Использование:
          DayZPulse                          запуск в режиме приложения с иконкой в трее (по умолчанию)
          DayZPulse --console                то же, но с выводом журнала в консоль
          DayZPulse --service                запуск как служба Windows (используется самой службой)
          DayZPulse install-service          установить службу Windows (нужны права администратора)
          DayZPulse uninstall-service        удалить службу Windows
          DayZPulse start-service            запустить установленную службу
          DayZPulse stop-service             остановить установленную службу
          DayZPulse service-status           показать состояние службы

        Параметры:
          --port=<номер>                     переопределить порт веб-интерфейса
          --data-dir=<путь>                  каталог данных (настройки, логи, резервные копии)
          --no-browser                       не открывать браузер при старте
          --help, -h, /?                     эта справка
          --version, -v                      версия приложения
        """;
}

/// <summary>Разобранные параметры командной строки.</summary>
internal sealed class CommandLineOptions
{
    public string? Command { get; private init; }
    public bool Service { get; private init; }
    public bool Console { get; private init; }
    public bool NoBrowser { get; private init; }
    public bool ShowHelp { get; private init; }
    public bool ShowVersion { get; private init; }
    public int? Port { get; private init; }
    public string? DataDirectory { get; private init; }
    public string? ServiceArguments { get; private init; }
    public string[] Raw { get; private init; } = [];

    public string[] ToArray() => Raw;

    public static CommandLineOptions Parse(string[] args)
    {
        string? command = null;
        var service = false;
        var console = false;
        var noBrowser = false;
        var help = false;
        var version = false;
        int? port = null;
        string? dataDir = null;
        string? serviceArgs = null;

        foreach (var raw in args)
        {
            var arg = raw.Trim();
            var lower = arg.ToLowerInvariant();

            switch (lower)
            {
                case "install-service" or "install":
                    command = "install-service";
                    continue;
                case "uninstall-service" or "uninstall" or "remove-service":
                    command = "uninstall-service";
                    continue;
                case "service-status" or "status":
                    command = "service-status";
                    continue;
                case "start-service":
                    command = "start-service";
                    continue;
                case "stop-service":
                    command = "stop-service";
                    continue;
                case "open":
                    command = "open";
                    continue;
                case "--service" or "-service":
                    service = true;
                    continue;
                case "--console" or "-console":
                    console = true;
                    continue;
                case "--no-browser":
                    noBrowser = true;
                    continue;
                case "--help" or "-h" or "/?" or "-?":
                    help = true;
                    continue;
                case "--version" or "-v":
                    version = true;
                    continue;
                case "--tray":
                    continue;
            }

            if (lower.StartsWith("--port=", StringComparison.Ordinal) && int.TryParse(arg[7..], out var parsedPort))
            {
                port = parsedPort;
                continue;
            }

            if (lower.StartsWith("--data-dir=", StringComparison.Ordinal))
            {
                dataDir = TrimQuotes(arg[11..]);
                continue;
            }

            if (lower.StartsWith("--service-args=", StringComparison.Ordinal))
            {
                serviceArgs = TrimQuotes(arg[15..]);
            }
        }

        return new CommandLineOptions
        {
            Command = command,
            Service = service,
            Console = console,
            NoBrowser = noBrowser,
            ShowHelp = help,
            ShowVersion = version,
            Port = port,
            DataDirectory = dataDir,
            ServiceArguments = serviceArgs,
            Raw = args
        };
    }

    private static string TrimQuotes(string value) => value.Trim().Trim('"');
}
