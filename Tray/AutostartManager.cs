using Microsoft.Win32;
using DayZPulse.Infrastructure;

namespace DayZPulse.Tray;

/// <summary>Автозапуск приложения при входе пользователя в систему (ветка Run в реестре).</summary>
public static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DayZPulse";

    /// <summary>
    /// Автозапуск включён, если запись есть в HKCU (тумблер в трее) или в HKLM
    /// (её ставит установщик, чтобы значок появлялся у всех пользователей).
    /// </summary>
    public static bool IsEnabled()
    {
        using var current = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        if (!string.IsNullOrWhiteSpace(current?.GetValue(ValueName) as string))
        {
            return true;
        }

        using var machine = Registry.LocalMachine.OpenSubKey(RunKey, writable: false);
        return !string.IsNullOrWhiteSpace(machine?.GetValue(ValueName) as string);
    }

    public static void Enable(bool minimized = true)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");

        var command = minimized
            ? $"\"{exe}\" --tray --no-browser"
            : $"\"{exe}\"";

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, command);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);

        // Чистим и запись установщика в HKLM — иначе автозапуск остался бы включён.
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
            machine?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Нет прав на HKLM — не критично: запись уберётся при удалении панели.
        }
    }

    public static void Set(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }
}
