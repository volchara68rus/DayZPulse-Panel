using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>Статус мода DayZPulse на сервере: установлен ли и как он подключён.</summary>
public sealed record ModStatus(
    bool Installed,
    string Source,
    bool InLaunchMods,
    bool FolderExists,
    bool EverConnected,
    string ConfigPath);

/// <summary>
/// Определяет, установлен ли мод DayZPulse на конкретном сервере.
///
/// Наличие <c>profiles\DayZPulseCore\config.json</c> намеренно НЕ считается
/// признаком установки: этот файл пишет сама панель по настройке
/// «автоматически писать config.json». Признаки установки другие:
/// <list type="bullet">
/// <item>мод указан в параметрах запуска (<c>-mod=@DayZPulseCore</c>);</item>
/// <item>папка мода <c>@DayZPulseCore</c> лежит рядом с сервером;</item>
/// <item>мод хотя бы раз выходил на связь (присылал данные) — значит он реально запущен.</item>
/// </list>
/// </summary>
public static class ModStatusService
{
    public const string CoreMod = "DayZPulseCore";
    public const string AntiCheatMod = "DayZPulseAntiCheat";

    /// <summary>Папка объединённого мода панели (внутри Core и AntiCheat).</summary>
    public const string PanelMod = "DayZPulse";

    /// <summary>
    /// Является ли папка мода нашим собственным модом панели: @DayZPulse,
    /// @DayZPulseCore, @DayZPulseAntiCheat (в любом регистре и с @ или без).
    /// Такой мод серверный — его нельзя подключать как клиентский (-mod=).
    /// </summary>
    public static bool IsOwnModFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var name = Path.GetFileName(folder.Trim().TrimEnd('\\', '/')).TrimStart('@').Trim();

        return name.Equals(PanelMod, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(CoreMod, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(AntiCheatMod, StringComparison.OrdinalIgnoreCase);
    }

    public static ModStatus Describe(ServerProfile profile, bool everConnected = false)
    {
        // Наш мод — серверный, поэтому ищем его как в -mod=, так и в -serverMod=.
        var allLaunch = (profile.Server.Mods ?? "") + ";" + (profile.Server.ServerMods ?? "");

        var inLaunch = allLaunch
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.TrimStart('@').Trim())
            .Any(entry =>
                entry.Equals(CoreMod, StringComparison.OrdinalIgnoreCase) ||
                entry.Equals(PanelMod, StringComparison.OrdinalIgnoreCase) ||
                entry.Equals(AntiCheatMod, StringComparison.OrdinalIgnoreCase));

        var folderExists = DetectModFolder(profile);

        var (installed, source) = (inLaunch, folderExists, everConnected) switch
        {
            (true, _, _) => (true, "mods"),
            (_, true, _) => (true, "folder"),
            (_, _, true) => (true, "connected"),
            _ => (false, "none")
        };

        string configPath = "";
        try
        {
            configPath = ModConfigWriter.Describe(profile).Path;
        }
        catch
        {
            // Каталог профилей может быть недоступен.
        }

        return new ModStatus(installed, source, inLaunch, folderExists, everConnected, configPath);
    }

    /// <summary>
    /// Ищет папку мода рядом с сервером: сам <c>@DayZPulseCore</c> или
    /// одноимённый подкаталог в каталоге сервера либо в каталоге модов.
    /// </summary>
    private static bool DetectModFolder(ServerProfile profile)
    {
        try
        {
            var working = Core.ServerPaths.WorkingDirectory(profile.Server);
            var baseDir = Core.ServerPaths.BaseDirectory(profile.Server);

            var candidates = new List<string>
            {
                Path.Combine(working, "@" + PanelMod),
                Path.Combine(working, PanelMod),
                Path.Combine(working, "@" + CoreMod),
                Path.Combine(working, CoreMod),
                Path.Combine(baseDir, "@" + PanelMod),
                Path.Combine(baseDir, PanelMod),
                Path.Combine(baseDir, "@" + CoreMod),
                Path.Combine(baseDir, CoreMod)
            };

            foreach (var candidate in candidates)
            {
                // Установленным считаем только каталог с реальным содержимым мода:
                // пустая папка @DayZPulse (создана вручную) — не установка.
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, "*.pbo", SearchOption.AllDirectories).Any())
                {
                    return true;
                }
            }
        }
        catch
        {
            // Пути могут быть недоступны — тогда ориентируемся на другие признаки.
        }

        return false;
    }
}
