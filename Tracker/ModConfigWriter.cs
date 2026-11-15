using DayZPulse.Core;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

public sealed record ModConfigInfo(string Path, bool Exists, string? Error);

/// <summary>
/// Пишет config.json для мода DayZPulseCore в profiles/DayZPulseCore каждого
/// сервера, чтобы владельцу не нужно было править его вручную.
/// </summary>
public static class ModConfigWriter
{
    private const string RelativePath = @"DayZPulseCore\config.json";
    private const string AntiCheatRelativePath = @"DayZPulseAntiCheat\config.json";

    /// <summary>Каталог profiles сервера.</summary>
    public static string ProfilesRoot(ServerProfile profile)
    {
        var working = ServerPaths.WorkingDirectory(profile.Server);
        var profiles = profile.Server.ProfilesDirectory;

        return string.IsNullOrWhiteSpace(profiles)
            ? Path.Combine(working, "profiles")
            : Path.IsPathRooted(profiles) ? profiles : Path.Combine(working, profiles);
    }

    public static ModConfigInfo Describe(ServerProfile profile)
    {
        try
        {
            var path = Path.Combine(ProfilesRoot(profile), RelativePath);
            return new ModConfigInfo(path, File.Exists(path), null);
        }
        catch (Exception ex)
        {
            return new ModConfigInfo("", false, ex.Message);
        }
    }

    /// <summary>
    /// Обновляет config.json античита (@DayZPulseAntiCheat): адрес панели и токен.
    /// Правки других полей (пороги проверок) сохраняются — их задаёт пользователь.
    /// </summary>
    public static bool WriteAntiCheatConfig(ServerProfile profile, string baseUrl, Models.TrackerSettings tracker)
    {
        try
        {
            var path = Path.Combine(ProfilesRoot(profile), AntiCheatRelativePath);

            // Файла нет и мод не установлен — создавать нечего.
            var antiCheatInstalled = Directory.Exists(
                Path.Combine(ServerPaths.WorkingDirectory(profile.Server), "@DayZPulse")) ||
                Directory.Exists(Path.Combine(ServerPaths.WorkingDirectory(profile.Server), "@DayZPulseAntiCheat"));

            if (!File.Exists(path) && !antiCheatInstalled)
            {
                return false;
            }

            var config = new Dictionary<string, object>
            {
                ["ENABLE_HTTP"] = true,
                ["HTTP_URL"] = $"{baseUrl}/tracker/ac/ingest",
                ["API_TOKEN"] = tracker.ApiToken,
                ["SERVER_ID"] = string.IsNullOrWhiteSpace(tracker.ServerId) ? profile.Id : tracker.ServerId
            };

            if (File.Exists(path))
            {
                try
                {
                    var existing = Infrastructure.Json.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                        File.ReadAllText(path));

                    if (existing is not null)
                    {
                        foreach (var (key, value) in existing)
                        {
                            // Адрес, токен и id контролирует панель.
                            if (key is "HTTP_URL" or "API_TOKEN" or "SERVER_ID")
                            {
                                continue;
                            }

                            config[key] = value.ValueKind switch
                            {
                                System.Text.Json.JsonValueKind.Number => value.GetDouble(),
                                System.Text.Json.JsonValueKind.True => true,
                                System.Text.Json.JsonValueKind.False => false,
                                System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
                                _ => config[key]
                            };
                        }
                    }
                }
                catch
                {
                    // Повреждённый файл перезапишем основными полями.
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temp = path + ".tmp";
            File.WriteAllText(temp, Infrastructure.Json.Serialize(config));
            File.Move(temp, path, overwrite: true);

            return true;
        }
        catch
        {
            // Античит не критичен для основной работы панели.
            return false;
        }
    }

    /// <summary>
    /// Записывает config.json для мода. Существующий файл обновляется,
    /// чтобы сохранить значения остальных полей, поправленных вручную.
    /// </summary>
    public static bool TryWrite(Infrastructure.SettingsStore settingsStore, ServerProfile profile, out string? error)
    {
        error = null;

        try
        {
            var tracker = profile.Tracker;
            var path = Path.Combine(ProfilesRoot(profile), RelativePath);
            var directory = Path.GetDirectoryName(path)!;

            if (!tracker.Enabled)
            {
                return false;
            }

            if (string.IsNullOrEmpty(tracker.ApiToken))
            {
                error = "Не задан токен трекера.";
                return false;
            }

            var port = settingsStore.EffectiveWeb.Port;
            var baseUrl = $"http://127.0.0.1:{port}";

            // Значения по умолчанию совпадают с PT_ConfigData в моде.
            var config = new Dictionary<string, object>
            {
                // Позиции мод пишет в players.json (файловый режим), а панель
                // читает файл. HTTP-отправка позиций выключена: POST синхронный
                // и бьёт по FPS сервера. По HTTP остаются только команды.
                ["ENABLE_HTTP"] = false,
                ["ENABLE_FILE"] = true,
                ["TICK_INTERVAL_MS"] = 2000,
                ["INGEST_URL"] = $"{baseUrl}/tracker/ingest",

                ["ENABLE_COMMANDS"] = true,
                ["COMMAND_INTERVAL_MS"] = 2000,
                ["COMMAND_MAX_PER_POLL"] = 25,
                ["COMMAND_URL"] = $"{baseUrl}/tracker/commands",
                ["ACK_URL"] = $"{baseUrl}/tracker/commands/ack",

                ["ENABLE_INVENTORY"] = true,
                ["INVENTORY_URL"] = $"{baseUrl}/tracker/inventory",

                ["API_TOKEN"] = tracker.ApiToken,
                ["SERVER_ID"] = string.IsNullOrWhiteSpace(tracker.ServerId) ? profile.Id : tracker.ServerId,
                ["INCLUDE_PLAIN_ID"] = true,
                ["SPAWN_MAX_QTY"] = 25,
                ["REGISTRY_MAX"] = 2000,
                ["VEHICLE_MAX"] = 300,
                ["WORLD_OBJECT_MAX"] = 3000,
                ["SPAWN_FOOD"] = -1,
                ["SPAWN_WARMTH"] = -1,
                ["SPAWN_WATER"] = -1
            };

            if (File.Exists(path))
            {
                try
                {
                    var existing = Infrastructure.Json.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                        File.ReadAllText(path));

                    if (existing is not null)
                    {
                        foreach (var (key, value) in existing)
                        {
                            // Адреса, токен и идентификатор панель контролирует сама:
                            // их нельзя переносить из старого файла (там мог быть другой порт).
                            if (key is "INGEST_URL" or "COMMAND_URL" or "ACK_URL" or "INVENTORY_URL" or "API_TOKEN" or "SERVER_ID"
                                or "ENABLE_HTTP" or "ENABLE_FILE")
                            {
                                continue;
                            }

                            config[key] = value.ValueKind switch
                            {
                                System.Text.Json.JsonValueKind.Number => value.GetDouble(),
                                System.Text.Json.JsonValueKind.True => true,
                                System.Text.Json.JsonValueKind.False => false,
                                System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
                                _ => config[key]
                            };
                        }
                    }
                }
                catch
                {
                    // Повреждённый файл просто перезаписываем целиком.
                }
            }

            Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            File.WriteAllText(temp, Infrastructure.Json.Serialize(config));
            File.Move(temp, path, overwrite: true);

            // Заодно обновляем config.json античита: там адрес панели и токен.
            WriteAntiCheatConfig(profile, baseUrl, tracker);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
