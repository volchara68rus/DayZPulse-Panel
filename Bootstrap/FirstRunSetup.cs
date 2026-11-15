using DayZPulse.Core;
using DayZPulse.Infrastructure;

namespace DayZPulse.Bootstrap;

/// <summary>
/// Первый запуск: пока владелец не создан, панель показывает страницу
/// создания владельца. Учётных данных по умолчанию не создаётся.
/// </summary>
public static class FirstRunSetup
{
    public static bool IsSetupRequired(SettingsStore settings)
    {
        var auth = settings.Current.Auth;
        return !auth.OwnerCreated || string.IsNullOrWhiteSpace(auth.PasswordHash);
    }

    /// <summary>Создаёт базовые каталоги всех серверов, чтобы их можно было настроить сразу.</summary>
    public static void EnsureServerDirectories(SettingsStore settings)
    {
        foreach (var profile in settings.Current.Servers)
        {
            ServerPaths.EnsureDataDirectories(profile.Server);

            var directory = ServerPaths.WorkingDirectory(profile.Server);
            TryCreate(directory);
            TryCreate(Path.Combine(directory, "profiles"));
            TryCreate(Path.Combine(directory, "mpmissions"));
            TryCreate(Path.Combine(directory, "keys"));
        }
    }

    private static void TryCreate(string directory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch
        {
            // Папку может создать и пользователь позже — это не критично.
        }
    }
}
