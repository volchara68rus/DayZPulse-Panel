using System.Text.Json;

namespace DayZPulse.Tracker;

/// <summary>
/// Разбор JSON-протокола мода DayZPulseCore. Всё построено на JsonDocument,
/// поэтому лишние поля игнорируются, а непонятные значения не роняют запрос.
/// </summary>
public static class TrackerProtocol
{
    public static TrackedServerState ParseServer(JsonElement root)
    {
        var state = new TrackedServerState();

        if (root.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.Object)
        {
            state.Id = Str(server, "id");
            state.Tick = Long(server, "tick");
            state.Players = Int(server, "players");
            state.Alive = Int(server, "alive");
            state.Vehicles = Int(server, "vehicles");
            state.Infected = Int(server, "infected");
            state.WorldObjects = Int(server, "worldObjects");
            state.PingAvg = Int(server, "pingAvg");
        }

        if (root.TryGetProperty("vehicleList", out var vehicles) && vehicles.ValueKind == JsonValueKind.Array)
        {
            state.VehicleList = vehicles.EnumerateArray().Select(ParseVehicle).ToArray();
        }

        if (root.TryGetProperty("worldObjectList", out var objects) && objects.ValueKind == JsonValueKind.Array)
        {
            state.WorldObjectList = objects.EnumerateArray().Select(ParseWorldObject).ToArray();
        }

        return state;
    }

    private static TrackedVehicle ParseVehicle(JsonElement element) => new()
    {
        Class = Str(element, "class"),
        X = Double(element, "x"),
        Z = Double(element, "z"),
        Health = Double(element, "health"),
        Fuel = Double(element, "fuel"),
        HasBattery = Bool(element, "hasBattery"),
        HasRadiator = Bool(element, "hasRadiator"),
        DoorsMissing = Int(element, "doorsMissing"),
        DoorsOpen = Int(element, "doorsOpen"),
        Crew = element.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array
            ? string.Join(", ", crew.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString()))
            : ""
    };

    private static TrackedWorldObject ParseWorldObject(JsonElement element) => new()
    {
        Kind = Str(element, "kind"),
        Label = Str(element, "label"),
        Class = Str(element, "class"),
        X = Double(element, "x"),
        Z = Double(element, "z")
    };

    public static List<LivePlayer> ParsePlayers(JsonElement root)
    {
        var result = new List<LivePlayer>();

        if (!root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in players.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var player = new LivePlayer
            {
                Id = Str(element, "id"),
                PlainId = Str(element, "plainId"),
                Name = Str(element, "name"),
                Alive = Bool(element, "alive"),
                Health = Double(element, "health"),
                MaxHealth = Double(element, "maxHealth"),
                Ping = Int(element, "ping")
            };

            if (player.MaxHealth <= 0)
            {
                player.MaxHealth = 100;
            }

            if (element.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                player.X = Double(pos, "x");
                player.Y = Double(pos, "y");
                player.Z = Double(pos, "z");
            }

            if (element.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                player.StatsJson = stats.GetRawText();
            }

            if (element.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in mods.EnumerateArray())
                {
                    if (mod.ValueKind == JsonValueKind.String && mod.GetString() is { Length: > 0 } value)
                    {
                        player.Mods.Add(value);
                    }
                }
            }

            if (player.Id.Length > 0)
            {
                result.Add(player);
            }
        }

        return result;
    }

    public static List<(long Id, bool Ok, string? Message, string? Oids)> ParseAck(JsonElement root)
    {
        var results = new List<(long, bool, string?, string?)>();

        if (!root.TryGetProperty("results", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            if (!idElement.TryGetInt64(out var id))
            {
                if (idElement.ValueKind != JsonValueKind.String || !long.TryParse(idElement.GetString(), out id))
                {
                    continue;
                }
            }

            var ok = Bool(element, "ok");
            var message = element.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            string? oids = null;
            if (element.TryGetProperty("oids", out var oidsElement) && oidsElement.ValueKind == JsonValueKind.Array)
            {
                oids = string.Join(",", oidsElement.EnumerateArray().Select(o => o.ToString()));
            }

            results.Add((id, ok, message, oids));
        }

        return results;
    }

    public static List<Violation> ParseViolations(JsonElement root)
    {        var result = new List<Violation>();

        if (!root.TryGetProperty("violations", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var violation = new Violation
            {
                Type = Str(element, "type"),
                PlayerId = Str(element, "id"),
                PlayerName = Str(element, "name"),
                Points = Int(element, "points"),
                TotalPoints = Int(element, "totalPoints"),
                Action = Str(element, "action") is { Length: > 0 } action ? action : "log",
                Details = Str(element, "details")
            };

            if (element.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                violation.X = Double(pos, "x");
                violation.Z = Double(pos, "z");
            }
            // Мод может присылать и старое написание.
            else
            {
                violation.X = element.TryGetProperty("position", out var legacy) && legacy.ValueKind == JsonValueKind.Object
                    ? Double(legacy, "x")
                    : Double(element, "x");
                violation.Z = element.TryGetProperty("position", out legacy) && legacy.ValueKind == JsonValueKind.Object
                    ? Double(legacy, "z")
                    : Double(element, "z");
            }

            result.Add(violation);
        }

        return result;
    }

    /// <summary>Разбирает команды от панели: одиночный объект, массив или { commands: [] }.</summary>
    public static (List<(string Action, Dictionary<string, string> Args)> Commands, string Group)? ParseCommandInput(JsonElement root)
    {
        var commands = new List<(string, Dictionary<string, string>)>();
        var group = Str(root, "group");

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                if (ParseOneCommand(element) is { } single)
                {
                    commands.Add(single);
                }
            }

            return (commands, group);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("commands", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (ParseOneCommand(element) is { } single)
                {
                    commands.Add(single);
                }
            }

            return (commands, group);
        }

        if (ParseOneCommand(root) is { } one)
        {
            return ([one], group);
        }

        return null;
    }

    private static (string Action, Dictionary<string, string> Args)? ParseOneCommand(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var action = Str(element, "action").Trim().ToLowerInvariant();
        if (action.Length == 0)
        {
            return null;
        }

        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in argsElement.EnumerateObject())
            {
                if (!IsValidArgName(property.Name))
                {
                    continue;
                }

                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "1",
                    JsonValueKind.False => "0",
                    _ => ""
                };

                args[property.Name] = Sanitize(value);
            }
        }

        return (action, args);
    }

    private static bool IsValidArgName(string name) =>
        name.Length is > 0 and <= 32 &&
        char.IsAsciiLetter(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>'|' — разделитель протокола, переводы строк ломают формат.</summary>
    public static string Sanitize(string value)
    {
        var cleaned = value.Replace("|", "").Replace("\r", "").Replace("\n", " ").Trim();
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    // ------------------------------------------------------------- хелперы

    /// <summary>
    /// Разбирает снимок инвентаря от мода: id и имя игрока плюс сырой JSON
    /// с предметами. Раскладку слотов панель понимает сама.
    /// </summary>
    public static (string Id, string Name, string ItemsJson)? ParseInventory(JsonElement root)
    {
        var id = Str(root, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = Str(root, "name");

        // «items» — либо строка с JSON, либо вложенный объект/массив.
        string items;
        if (root.TryGetProperty("items", out var itemsElement))
        {
            items = itemsElement.ValueKind == JsonValueKind.String
                ? itemsElement.GetString() ?? "{}"
                : itemsElement.GetRawText();
        }
        else
        {
            items = "{}";
        }

        return (id, name, items);
    }

    public static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public static double Double(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    public static int Int(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    public static float Float(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetSingle(out var f) => f,
            JsonValueKind.Number when value.TryGetDouble(out var d) => (float)d,
            JsonValueKind.String when float.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    public static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    public static bool Bool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            JsonValueKind.Number when value.TryGetInt32(out var i) => i != 0,
            _ => false
        };
    }
}
