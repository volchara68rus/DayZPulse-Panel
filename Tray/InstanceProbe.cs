using System.Diagnostics;
using System.Net.Http.Json;
using System.ServiceProcess;
using DayZPulse.Service;

namespace DayZPulse.Tray;

public sealed record RemoteInstance(bool Available, int Port, string? Product, string? User, string BaseUrl);

/// <summary>
/// Определяет, обслуживает ли панель уже другой экземпляр (обычно служба Windows).
/// Если да, приложение-трей работает как пульт и не поднимает второй веб-сервер.
/// </summary>
public static class InstanceProbe
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public static async Task<RemoteInstance> ProbeAsync(int port, CancellationToken cancellationToken = default)
    {
        var baseUrl = $"http://127.0.0.1:{port}";
        try
        {
            var response = await Client.GetAsync($"{baseUrl}/api/auth/status", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new RemoteInstance(false, port, null, null, baseUrl);
            }

            var payload = await response.Content.ReadFromJsonAsync<AuthStatusDto>(cancellationToken);
            return new RemoteInstance(true, port, payload?.ProductName, payload?.User, baseUrl);
        }
        catch
        {
            return new RemoteInstance(false, port, null, null, baseUrl);
        }
    }

    /// <summary>Ждём освобождения порта предыдущим экземпляром после перезапуска.</summary>
    public static async Task<RemoteInstance> WaitForAsync(int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.Now + timeout;
        RemoteInstance last = new(false, port, null, null, $"http://127.0.0.1:{port}");

        while (DateTimeOffset.Now < deadline)
        {
            last = await ProbeAsync(port, cancellationToken);
            if (last.Available)
            {
                return last;
            }

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return last;
    }

    public static bool IsServiceRunning()
    {
        try
        {
            using var service = new ServiceController(WindowsServiceManager.ServiceName);
            service.Refresh();
            return service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Актуальный порт панели: читается из settings.json. Нужен трею-пульту,
    /// чтобы после смены порта он переподключился, а не искал старый адрес.
    /// Если прочитать не удалось — возвращаем <paramref name="fallback"/>.
    /// </summary>
    public static int ConfiguredPort(int fallback)
    {
        try
        {
            var path = Infrastructure.AppPaths.SettingsFile;
            if (!File.Exists(path))
            {
                return fallback;
            }

            var json = File.ReadAllText(path);
            var settings = Infrastructure.Json.Deserialize<Models.AppSettings>(json);
            return settings?.Web.Port is > 0 ? settings.Web.Port : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private sealed record AuthStatusDto(string? ProductName, string? User);
}
