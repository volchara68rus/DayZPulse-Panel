namespace DayZPulse.Hosting;

/// <summary>
/// Переопределения из командной строки. Действуют только на время текущего запуска
/// и никогда не записываются в файл настроек.
/// </summary>
public sealed record RuntimeOverrides(int? WebPort = null, bool? OpenBrowserOnStart = null)
{
    public static RuntimeOverrides FromCommandLine(string[] args, string? explicitPort = null, bool noBrowser = false)
    {
        int? port = null;
        if (!string.IsNullOrWhiteSpace(explicitPort) && int.TryParse(explicitPort, out var parsed))
        {
            port = parsed;
        }

        return new RuntimeOverrides(port, noBrowser ? false : null);
    }
}
