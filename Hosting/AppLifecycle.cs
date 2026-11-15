using DayZPulse.Infrastructure;

namespace DayZPulse.Hosting;

/// <summary>Общие события жизненного цикла приложения: перезапуск и завершение.</summary>
public sealed class AppLifecycle
{
    private readonly ILogger<AppLifecycle> _logger;
    private int _restartRequested;

    public AppLifecycle(ILogger<AppLifecycle> logger)
    {
        _logger = logger;
    }

    /// <summary>Запрошен перезапуск приложения (например, изменился порт веб-интерфейса).</summary>
    public bool RestartRequested => Volatile.Read(ref _restartRequested) == 1;

    public event EventHandler? RestartRequestedEvent;
    public event EventHandler? ShutdownRequested;

    public void RequestRestart(string reason)
    {
        _logger.LogInformation("Запрошен перезапуск приложения: {Reason}", reason);
        Interlocked.Exchange(ref _restartRequested, 1);
        RestartRequestedEvent?.Invoke(this, EventArgs.Empty);
    }

    public void RequestShutdown(string reason)
    {
        _logger.LogInformation("Запрошено завершение приложения: {Reason}", reason);
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Запускает новый экземпляр приложения, чтобы он занял место текущего после перезапуска.
    /// Используется в режиме приложения с иконкой в трее.
    /// </summary>
    public static void SpawnReplacementProcess(string[] arguments)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось запустить новый экземпляр приложения: {ex.Message}");
        }
    }

    /// <summary>
    /// Запускает отдельный процесс, который дождётся полной остановки службы и
    /// запустит её снова. Используется в режиме службы Windows при смене порта.
    ///
    /// Важно: нельзя вызывать <c>sc start</c> сразу — служба ещё в STOP_PENDING,
    /// команда терпит неудачу, и её поднимает только аварийное восстановление SCM
    /// спустя минуту. Из-за этого панель после смены порта «зависала». Поэтому
    /// скрипт ждёт состояния STOPPED и лишь затем запускает службу, повторяя
    /// попытки (в том числе пока она ещё START_PENDING).
    /// </summary>
    public static void ScheduleServiceRestart(string serviceName)
    {
        try
        {
            // ~2 минуты ожидания (2 попытки в секунду), затем сдаёмся.
            const int maxAttempts = 240;

            var script = string.Join("\r\n",
            [
                "@echo off",
                "setlocal",
                "set /a n=0",
                ":wait",
                $"sc query {serviceName} | findstr /i \"STOPPED\" >nul",
                "if not errorlevel 1 goto start",
                "set /a n+=1",
                "if %n% GEQ " + maxAttempts + " goto end",
                "ping -n 2 127.0.0.1 >nul",
                "goto wait",
                ":start",
                "set /a n=0",
                ":startloop",
                $"sc query {serviceName} | findstr /i \"RUNNING\" >nul",
                "if not errorlevel 1 goto end",
                $"sc start {serviceName} >nul 2>&1",
                "set /a n+=1",
                "if %n% GEQ " + maxAttempts + " goto end",
                "ping -n 2 127.0.0.1 >nul",
                "goto startloop",
                ":end",
                "del \"%~f0\" >nul 2>&1"
            ]);

            var path = Path.Combine(Path.GetTempPath(), $"dayzpulse-restart-{Guid.NewGuid():n}.cmd");
            File.WriteAllText(path, script, new System.Text.UTF8Encoding(false));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось запланировать перезапуск службы: {ex.Message}");
        }
    }
}
