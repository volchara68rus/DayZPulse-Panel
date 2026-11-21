using System.Diagnostics;
using System.ServiceProcess;
using DayZPulse.Infrastructure;

namespace DayZPulse.Service;

public sealed record ServiceStatusInfo(
    bool Installed,
    string Name,
    string DisplayName,
    string State,
    string StartMode,
    string? ExecutablePath,
    bool IsElevated);

/// <summary>Установка, удаление и управление службой Windows.</summary>
public sealed class WindowsServiceManager
{
    private readonly ILogger<WindowsServiceManager> _logger;

    public WindowsServiceManager(ILogger<WindowsServiceManager> logger)
    {
        _logger = logger;
    }

    public static string ServiceName => "DayZPulse";

    /// <summary>
    /// Запущен ли текущий процесс как служба Windows. Нужно помощнику обновления:
    /// службу поднимает SCM (<c>sc start</c>), а трей — обычным запуском exe.
    /// </summary>
    public static bool IsWindowsServiceHost() =>
        Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

    public static string ServiceDisplayName => "DayZPulse — панель управления игровым сервером";

    public static string ServiceDescription =>
        "Запускает локальный веб-интерфейс для управления игровым сервером и следит за его процессом.";

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public ServiceStatusInfo GetStatus()
    {
        string state = "Не установлена";
        string startMode = "—";
        string? path = null;
        var installed = false;

        try
        {
            using var service = new ServiceController(ServiceName);
            _ = service.Status; // Обращение выбрасывает исключение, если службы нет.
            installed = true;
            state = service.Status.ToString();
            startMode = service.StartType.ToString();
        }
        catch (InvalidOperationException)
        {
            installed = false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось определить состояние службы.");
        }

        try
        {
            path = QueryImagePath(ServiceName);
        }
        catch
        {
            // Не критично.
        }

        return new ServiceStatusInfo(installed, ServiceName, ServiceDisplayName, state, startMode, path, IsElevated);
    }

    public async Task<string> InstallAsync(string? arguments, CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу приложения.");

        if (string.IsNullOrWhiteSpace(arguments))
        {
            arguments = "--service";
        }

        // Останавливаем и удаляем прежнюю версию, чтобы обновить путь к исполняемому файлу.
        await RunScAsync(["stop", ServiceName], cancellationToken, ignoreErrors: true);
        await WaitForStopAsync(TimeSpan.FromSeconds(20), cancellationToken);
        await RunScAsync(["delete", ServiceName], cancellationToken, ignoreErrors: true);

        await RunScAsync(
            ["create", ServiceName, "binPath=", $"\"{exe}\" {arguments}", "start=", "auto", "DisplayName=", ServiceDisplayName],
            cancellationToken);

        await RunScAsync(["description", ServiceName, ServiceDescription], cancellationToken, ignoreErrors: true);

        // Восстановление после сбоев: перезапуск через 5 секунд (а не через
        // минуту) — иначе при неудачном перезапуске панель «висит» слишком долго.
        await RunScAsync(
            ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/15000"],
            cancellationToken, ignoreErrors: true);

        _logger.LogInformation("Служба установлена: {Exe} {Args}", exe, arguments);
        return $"Служба «{ServiceName}» установлена. Выполните вход в систему для обновления списка служб.";
    }

    public async Task<string> UninstallAsync(CancellationToken cancellationToken)
    {
        await RunScAsync(["stop", ServiceName], cancellationToken, ignoreErrors: true);
        await WaitForStopAsync(TimeSpan.FromSeconds(20), cancellationToken);
        await RunScAsync(["delete", ServiceName], cancellationToken);
        _logger.LogInformation("Служба удалена.");
        return $"Служба «{ServiceName}» удалена.";
    }

    public async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        await RunScAsync(["start", ServiceName], cancellationToken);
        return $"Отправлена команда запуска службы «{ServiceName}».";
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        await RunScAsync(["stop", ServiceName], cancellationToken);
        return $"Отправлена команда остановки службы «{ServiceName}».";
    }

    private async Task WaitForStopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                if (service.Status != ServiceControllerStatus.StopPending && service.Status != ServiceControllerStatus.Running)
                {
                    return;
                }

                service.Refresh();
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return; // Службы нет.
            }
            catch
            {
                return;
            }

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<(int ExitCode, string Output)> RunScAsync(string[] args, CancellationToken cancellationToken, bool ignoreErrors = false)
    {
        if (!IsElevated)
        {
            throw new UnauthorizedAccessException(
                "Для управления службой требуются права администратора. Запустите DayZPulse от имени администратора.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Не удалось запустить sc.exe.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 && !ignoreErrors)
        {
            throw new InvalidOperationException(
                $"sc.exe {string.Join(' ', args)} завершился с кодом {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}");
        }

        return (process.ExitCode, stdout + stderr);
    }

    private static string? QueryImagePath(string name)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("ImagePath") as string;
    }
}
