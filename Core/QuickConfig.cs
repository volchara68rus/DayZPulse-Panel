using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DayZPulse.Models;

namespace DayZPulse.Core;

/// <summary>Погода из cfgweather.xml миссии сервера.</summary>
public sealed record WeatherSettings(
    bool Enabled,
    double Overcast, double Fog, double Rain, double Snow, double Wind,
    double StormDensity, double StormThreshold,
    bool Freeze);

/// <summary>
/// Правки конфигурации сервера поверх его файлов:
/// <list type="bullet">
/// <item><c>serverDZ.cfg</c> — основные настройки;</item>
/// <item><c>mpmissions/&lt;миссия&gt;/cfggameplay.json</c> — мир;</item>
/// <item><c>mpmissions/&lt;миссия&gt;/db/globals.xml</c> — население и очистка;</item>
/// <item><c>mpmissions/&lt;миссия&gt;/cfgweather.xml</c> — погода.</item>
/// </list>
/// Значения читаются и пишутся по схеме <see cref="QuickSchema"/>,
/// поэтому комментарии и посторонние поля в файлах сохраняются.
/// </summary>
public static class QuickConfig
{
    private static string ConfigFileName(ServerSettings options) =>
        string.IsNullOrWhiteSpace(options.ConfigFile) ? "serverDZ.cfg" : options.ConfigFile;

    /// <summary>
    /// Путь к конфигу. Если передан абсолютный путь — берём его как есть;
    /// иначе ищем файл рядом с исполняемым файлом, затем в рабочем каталоге.
    /// </summary>
    private static string ConfigPath(string workingDirectory, string configFile)
    {
        if (Path.IsPathRooted(configFile))
        {
            return Path.GetFullPath(configFile);
        }

        var options = new ServerSettings
        {
            WorkingDirectory = workingDirectory,
            ConfigFile = configFile
        };

        return ServerPaths.ConfigFile(options);
    }

    // =============================================================== serverDZ.cfg

    /// <summary>Читает все значения serverDZ.cfg по схеме.</summary>
    public static Dictionary<string, string> ReadServerCfg(string workingDirectory, string configFile)
    {
        var path = ConfigPath(workingDirectory, configFile);
        var text = File.Exists(path) ? ReadText(path) : DefaultServerCfg();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in QuickSchema.ServerCfg)
        {
            var raw = Value(text, field.Key);
            values[field.Key] = raw is null ? DefaultText(field) : Normalize(raw, field);
        }

        values["__mission"] = MissionTemplate(text);
        return values;
    }

    /// <summary>Пишет значения serverDZ.cfg по схеме, сохраняя комментарии.</summary>
    public static void WriteServerCfg(string workingDirectory, string configFile, Dictionary<string, string> values)
    {
        var path = ConfigPath(workingDirectory, configFile);
        var text = File.Exists(path) ? ReadText(path) : DefaultServerCfg();

        foreach (var field in QuickSchema.ServerCfg)
        {
            if (!values.TryGetValue(field.Key, out var value))
            {
                continue;
            }

            // Пароли, время и shardId пишутся в кавычках.
            var quoted = field.Kind == FieldKind.Text && field.Key is not "serverTime"
                ? Quote(value)
                : field.Key == "serverTime"
                    ? Quote(value)
                    : value;

            text = SetValue(text, field.Key, quoted, create: true);
        }

        if (values.TryGetValue("__mission", out var mission) && mission.Length > 0)
        {
            text = SetMissionTemplate(text, mission);
        }

        WriteText(path, text);
    }

    // ========================================================== cfggameplay.json

    public static string GameplayPath(ServerSettings options) =>
        Path.Combine(MissionDirectory(options) ?? ServerPaths.WorkingDirectory(options), "cfggameplay.json");

    public static Dictionary<string, string> ReadGameplay(ServerSettings options)
    {
        var path = GameplayPath(options);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        JsonObject? root = null;
        try
        {
            if (File.Exists(path))
            {
                root = JsonNode.Parse(ReadText(path))?.AsObject();
            }
        }
        catch
        {
            root = null;
        }

        foreach (var field in QuickSchema.Gameplay)
        {
            var node = ResolveNode(root, field.Key);
            values[field.Key] = node is null ? DefaultText(field) : NodeText(node);
        }

        return values;
    }

    public static void WriteGameplay(ServerSettings options, Dictionary<string, string> values)
    {
        var path = GameplayPath(options);

        JsonObject root;
        try
        {
            root = File.Exists(path) ? JsonNode.Parse(ReadText(path))?.AsObject() ?? new JsonObject() : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        if (root["version"] is null)
        {
            root["version"] = 1;
        }

        foreach (var field in QuickSchema.Gameplay)
        {
            if (!values.TryGetValue(field.Key, out var value))
            {
                continue;
            }

            SetNode(root, field.Key, value, field);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    // ============================================================= db/globals.xml

    public static string GlobalsPath(ServerSettings options) =>
        Path.Combine(MissionDirectory(options) ?? ServerPaths.WorkingDirectory(options), "db", "globals.xml");

    public static Dictionary<string, string> ReadGlobals(ServerSettings options)
    {
        var text = ReadText(GlobalsPath(options));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in QuickSchema.Globals)
        {
            var raw = XmlVar(text, field.Key);
            values[field.Key] = raw is null ? DefaultText(field) : Normalize(raw, field);
        }

        return values;
    }

    public static void WriteGlobals(ServerSettings options, Dictionary<string, string> values)
    {
        var path = GlobalsPath(options);
        var text = File.Exists(path) ? ReadText(path) : DefaultGlobals();

        foreach (var field in QuickSchema.Globals)
        {
            if (!values.TryGetValue(field.Key, out var value))
            {
                continue;
            }

            text = SetXmlVar(text, field.Key, value);
        }

        WriteText(path, text);
    }

    private static string? XmlVar(string text, string name)
    {
        var m = Regex.Match(text,
            $@"(?is)<var\s+name\s*=\s*""{Regex.Escape(name)}""[^>]*?\bvalue\s*=\s*""(?<v>[^""]*)""");
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string SetXmlVar(string text, string name, string value)
    {
        var pattern = $@"(?is)(<var\s+name\s*=\s*""{Regex.Escape(name)}""[^>]*?\bvalue\s*=\s*"")[^""]*("")";
        if (Regex.IsMatch(text, pattern))
        {
            return Regex.Replace(text, pattern, m => m.Groups[1].Value + value + m.Groups[2].Value);
        }

        return text.TrimEnd() + Environment.NewLine +
               $"<var name=\"{name}\" type=\"0\" value=\"{value}\"/>" + Environment.NewLine;
    }

    // ============================================================== погода

    public static string WeatherPath(ServerSettings options) =>
        Path.Combine(MissionDirectory(options) ?? ServerPaths.WorkingDirectory(options), "cfgweather.xml");

    public static WeatherSettings ReadWeather(ServerSettings options)
    {
        var text = ReadText(WeatherPath(options));

        return new WeatherSettings(
            Enabled: Attr(text, "weather", "enable") != "0",
            Overcast: Number(AttrIn(text, "overcast", "current", "actual"), 0.45),
            Fog: Number(AttrIn(text, "fog", "current", "actual"), 0.05),
            Rain: Number(AttrIn(text, "rain", "current", "actual"), 0.0),
            Snow: Number(AttrIn(text, "snowfall", "current", "actual"), 0.0),
            Wind: Number(AttrIn(text, "windMagnitude", "current", "actual"), 8.0),
            StormDensity: Number(Attr(text, "storm", "density"), 1.0),
            StormThreshold: Number(Attr(text, "storm", "threshold"), 0.9),
            Freeze: false);
    }

    public static void WriteWeather(ServerSettings options, WeatherSettings w)
    {
        var path = WeatherPath(options);
        var text = File.Exists(path) ? ReadText(path) : DefaultWeather();

        text = SetAttr(text, "weather", "enable", w.Enabled ? "1" : "0");

        text = SetAttrIn(text, "overcast", "current", "actual", Fmt(w.Overcast));
        text = SetAttrIn(text, "fog", "current", "actual", Fmt(w.Fog));
        text = SetAttrIn(text, "rain", "current", "actual", Fmt(w.Rain));
        text = SetAttrIn(text, "snowfall", "current", "actual", Fmt(w.Snow));
        text = SetAttrIn(text, "windMagnitude", "current", "actual", Fmt(w.Wind));

        // Границы задаём по текущему значению, чтобы CE не «уводил» погоду.
        text = SetAttrIn(text, "overcast", "limits", "min", Fmt(Math.Max(0, w.Overcast - 0.15)));
        text = SetAttrIn(text, "overcast", "limits", "max", Fmt(Math.Min(1, w.Overcast + 0.15)));
        text = SetAttrIn(text, "fog", "limits", "min", Fmt(Math.Max(0, w.Fog - 0.05)));
        text = SetAttrIn(text, "fog", "limits", "max", Fmt(Math.Min(1, w.Fog + 0.05)));
        text = SetAttrIn(text, "rain", "limits", "min", Fmt(Math.Max(0, w.Rain - 0.1)));
        text = SetAttrIn(text, "rain", "limits", "max", Fmt(Math.Max(w.Rain, 0.1)));
        text = SetAttrIn(text, "snowfall", "limits", "min", Fmt(Math.Max(0, w.Snow - 0.1)));
        text = SetAttrIn(text, "snowfall", "limits", "max", Fmt(Math.Min(1, w.Snow + 0.1)));
        text = SetAttrIn(text, "windMagnitude", "limits", "min", Fmt(Math.Max(0, w.Wind - 3)));
        text = SetAttrIn(text, "windMagnitude", "limits", "max", Fmt(Math.Min(20, w.Wind + 3)));

        text = SetAttr(text, "weather", "reset", "1");
        text = SetAttr(text, "storm", "density", Fmt(w.StormDensity));
        text = SetAttr(text, "storm", "threshold", Fmt(w.StormThreshold));

        WriteText(path, text);
    }

    // ============================================================== флаг мира

    /// <summary>Включает чтение cfggameplay.json (enableCfgGameplayFile = 1).</summary>
    public static void EnableGameplayFile(string workingDirectory, string configFile)
    {
        var path = ConfigPath(workingDirectory, configFile);
        var text = ReadText(path);
        if (text.Length == 0)
        {
            return;
        }

        var updated = SetValue(text, "enableCfgGameplayFile", "1", create: true);
        if (updated != text)
        {
            WriteText(path, updated);
        }
    }

    public static bool IsGameplayFileEnabled(string workingDirectory, string configFile)
    {
        var value = Value(ReadText(ConfigPath(workingDirectory, configFile)), "enableCfgGameplayFile");
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool GameplayFileExists(ServerSettings options) => File.Exists(GameplayPath(options));

    // ============================================================== миссия

    public static string? MissionDirectory(ServerSettings options)
    {
        var working = ServerPaths.WorkingDirectory(options);
        var missionsRoot = Path.Combine(working, "mpmissions");
        if (!Directory.Exists(missionsRoot))
        {
            return null;
        }

        var template = MissionTemplate(ReadText(Path.Combine(working, ConfigFileName(options))));
        var direct = Path.Combine(missionsRoot, template);
        if (template.Length > 0 && Directory.Exists(direct))
        {
            return direct;
        }

        return Directory.EnumerateDirectories(missionsRoot).FirstOrDefault();
    }

    /// <summary>
    /// Имя миссии для определения карты: сначала template из serverDZ.cfg,
    /// иначе — имя первой папки в mpmissions.
    /// </summary>
    public static string MissionTemplateOr(ServerSettings options)
    {
        var working = ServerPaths.WorkingDirectory(options);
        var template = MissionTemplate(ReadText(Path.Combine(working, ConfigFileName(options))));
        if (template.Length > 0)
        {
            return template;
        }

        var missionsRoot = Path.Combine(working, "mpmissions");
        if (Directory.Exists(missionsRoot))
        {
            var first = Directory.EnumerateDirectories(missionsRoot).Select(Path.GetFileName).FirstOrDefault();
            if (!string.IsNullOrEmpty(first))
            {
                return first!;
            }
        }

        return "";
    }

    /// <summary>Список папок миссий — для выбора карты.</summary>
    public static IReadOnlyList<string> Missions(ServerSettings options)    {
        var missionsRoot = Path.Combine(ServerPaths.WorkingDirectory(options), "mpmissions");
        if (!Directory.Exists(missionsRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(missionsRoot).Select(Path.GetFileName).NonNull().ToArray();
    }

    private static string MissionTemplate(string text)
    {
        var m = Regex.Match(text, @"template\s*=\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value.Trim() : "";
    }

    private static string SetMissionTemplate(string text, string template)
    {
        if (Regex.IsMatch(text, @"(?im)template\s*=\s*""[^""]*"""))
        {
            return Regex.Replace(text, @"(?im)(template\s*=\s*)""[^""]*""",
                m => m.Groups[1].Value + Quote(template));
        }

        return text.TrimEnd() + Environment.NewLine + Environment.NewLine +
               "class Missions" + Environment.NewLine + "{" + Environment.NewLine +
               "    class DayZ" + Environment.NewLine + "    {" + Environment.NewLine +
               $"        template={Quote(template)};" + Environment.NewLine +
               "    };" + Environment.NewLine + "};" + Environment.NewLine;
    }

    // ============================================================== JSON по пути

    private static JsonNode? ResolveNode(JsonObject? root, string path)
    {
        if (root is null)
        {
            return null;
        }

        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            node = node?[part];
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static void SetNode(JsonObject root, string path, string value, FieldDef field)
    {
        var parts = path.Split('.');
        var current = root;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is JsonObject next)
            {
                current = next;
                continue;
            }

            var created = new JsonObject();
            current[parts[i]] = created;
            current = created;
        }

        var name = parts[^1];

        current[name] = field.Kind switch
        {
            FieldKind.Bool => JsonValue.Create(value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)),
            FieldKind.Int => JsonValue.Create(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0),
            FieldKind.Float => JsonValue.Create(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0),
            _ => JsonValue.Create(value)
        };
    }

    private static string NodeText(JsonNode node) =>
        node is JsonValue v ? (v.TryGetValue<bool>(out var b) ? (b ? "1" : "0") : v.ToJsonString().Trim('"')) : node.ToJsonString();

    // ============================================================== разбор строк

    private static string? Value(string text, string key)
    {
        var m = Regex.Match(text, $@"(?im)^[ \t]*{Regex.Escape(key)}[ \t]*=[ \t]*(?<v>[^;\r\n]*)");
        if (!m.Success)
        {
            return null;
        }

        var value = m.Groups["v"].Value;
        var comment = value.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
        {
            value = value[..comment];
        }

        return value.Trim().Trim('"');
    }

    private static string SetValue(string text, string key, string value, bool create)
    {
        var pattern = $@"(?im)^([ \t]*{Regex.Escape(key)}[ \t]*=[ \t]*)[^;\r\n]*";
        if (Regex.IsMatch(text, pattern))
        {
            return Regex.Replace(text, pattern, m => m.Groups[1].Value + value);
        }

        return !create
            ? text
            : text.TrimEnd() + Environment.NewLine + key + " = " + value + ";" + Environment.NewLine;
    }

    private static string? Attr(string text, string element, string attribute)
    {
        var block = Regex.Match(text, $@"(?is)<{Regex.Escape(element)}\b[^>]*?(?:/>|>(?<inner>.*?)</{Regex.Escape(element)}>)");
        var m = Regex.Match(block.Value, $@"(?i)\b{Regex.Escape(attribute)}\s*=\s*""(?<v>[^""]*)""");
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string? AttrIn(string text, string element, string inner, string attribute)
    {
        var block = Regex.Match(text, $@"(?is)<{Regex.Escape(element)}\b[^>]*>(?<inner>.*?)</{Regex.Escape(element)}>");
        if (!block.Success)
        {
            return null;
        }

        var innerBlock = Regex.Match(block.Groups["inner"].Value, $@"(?is)<{Regex.Escape(inner)}\b[^>]*/?>");
        var m = Regex.Match(innerBlock.Value, $@"(?i)\b{Regex.Escape(attribute)}\s*=\s*""(?<v>[^""]*)""");
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string SetAttr(string text, string element, string attribute, string value)
    {
        var pattern = $@"(?is)(<{Regex.Escape(element)}\b[^>]*?\b{Regex.Escape(attribute)}\s*=\s*"")[^""]*("")";
        return Regex.IsMatch(text, pattern)
            ? Regex.Replace(text, pattern, m => m.Groups[1].Value + value + m.Groups[2].Value)
            : text;
    }

    private static string SetAttrIn(string text, string element, string inner, string attribute, string value)
    {
        var blockPattern = $@"(?is)<{Regex.Escape(element)}\b[^>]*>(?<body>.*?)</{Regex.Escape(element)}>";
        if (!Regex.IsMatch(text, blockPattern))
        {
            return text;
        }

        return Regex.Replace(text, blockPattern, block =>
        {
            var body = block.Groups["body"].Value;
            var innerPattern = $@"(?is)<{Regex.Escape(inner)}\b[^>]*/?>";
            var patched = Regex.Replace(body, innerPattern, tag =>
            {
                var attrPattern = $@"(?i)\b{Regex.Escape(attribute)}\s*=\s*""[^""]*""";
                return Regex.IsMatch(tag.Value, attrPattern)
                    ? Regex.Replace(tag.Value, attrPattern, $"{attribute}=\"{value}\"")
                    : tag.Value;
            });

            return block.Value.Replace(body, patched);
        });
    }

    // ============================================================== мелочи

    /// <summary>Приводит значение из файла к каноничному виду по типу поля.</summary>
    private static string Normalize(string raw, FieldDef field) => field.Kind switch
    {
        FieldKind.Bool => raw is "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0",
        FieldKind.Int => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i.ToString(CultureInfo.InvariantCulture) : DefaultText(field),
        FieldKind.Float => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? Fmt(d) : DefaultText(field),
        _ => raw
    };

    private static string DefaultText(FieldDef field) => field.Kind switch
    {
        FieldKind.Bool => "0",
        FieldKind.Int or FieldKind.Float => "0",
        _ => ""
    };

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Number(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "") + "\"";

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Когда сервер запущен, правки нельзя писать в рабочие файлы — они читаются
    /// при старте. Тогда панель складывает их в папку отложенных правок.
    /// Поле задаётся на время операции записи.
    /// </summary>
    [ThreadStatic]
    private static ServerSettings? _staging;

    /// <summary>Пишет правку напрямую либо откладывает до следующего запуска.</summary>
    private static void WriteText(string path, string text)
    {
        if (_staging is not null)
        {
            PendingConfig.Stage(_staging, path, text);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    /// <summary>
    /// Выполняет запись с учётом состояния сервера: если он запущен —
    /// правки откладываются (в параллельно собираемую временную папку).
    /// </summary>
    public static void WithStaging(ServerSettings options, bool running, Action write)
    {
        _staging = running ? options : null;
        try
        {
            write();
        }
        finally
        {
            _staging = null;
        }
    }

    private static string DefaultServerCfg() => string.Join(Environment.NewLine,
    [
        "hostname = \"DayZ Server\";",
        "password = \"\";",
        "passwordAdmin = \"\";",
        "description = \"\";",
        "enableWhitelist = 0;",
        "maxPlayers = 60;",
        "verifySignatures = 2;",
        "forceSameBuild = 1;",
        "disableVoN = 0;",
        "vonCodecQuality = 20;",
        "shardId = \"123abc\";",
        "thirdPersonView = 1;",
        "disableCrosshair = 0;",
        "serverTime = \"SystemTime\";",
        "serverTimeAcceleration = 1;",
        "serverNightTimeAcceleration = 1;",
        "serverTimePersistent = 0;",
        "lightingConfig = 0;",
        "instanceId = 1;",
        "enableCfgGameplayFile = 1;",
        ""
    ]);

    private static string DefaultGlobals() => string.Join(Environment.NewLine,
    [
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>",
        "<variables>",
        "</variables>",
        ""
    ]);

    private static string DefaultWeather() => string.Join(Environment.NewLine,
    [
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>",
        "<weather reset=\"1\" enable=\"1\">",
        "    <overcast>",
        "        <current actual=\"0.45\" time=\"120\" duration=\"240\" />",
        "        <limits min=\"0.3\" max=\"0.6\" />",
        "    </overcast>",
        "    <fog>",
        "        <current actual=\"0.05\" time=\"120\" duration=\"240\" />",
        "        <limits min=\"0.0\" max=\"0.1\" />",
        "    </fog>",
        "    <rain>",
        "        <current actual=\"0.0\" time=\"60\" duration=\"120\" />",
        "        <limits min=\"0.0\" max=\"0.1\" />",
        "        <thresholds min=\"0.6\" max=\"1.0\" end=\"60\" />",
        "    </rain>",
        "    <windMagnitude>",
        "        <current actual=\"8.0\" time=\"120\" duration=\"240\" />",
        "        <limits min=\"5\" max=\"11\" />",
        "    </windMagnitude>",
        "    <snowfall>",
        "        <current actual=\"0.0\" time=\"0\" duration=\"32768\" />",
        "        <limits min=\"0.0\" max=\"0.0\" />",
        "        <thresholds min=\"1.0\" max=\"1.0\" end=\"120\" />",
        "    </snowfall>",
        "    <storm density=\"1.0\" threshold=\"0.9\" timeout=\"45\" />",
        "</weather>",
        ""
    ]);
}

/// <summary>Небольшие расширения для работы с последовательностями.</summary>
internal static class SequenceExtensions
{
    public static IEnumerable<string> NonNull(this IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                yield return value;
            }
        }
    }
}
