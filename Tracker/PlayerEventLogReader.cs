using System.Text.Json;
using DayZPulse.Core;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>
/// Читает журнал событий игрока, который пишет мод
/// (<c>$profile:DayZPulseCore/events.jsonl</c>), и отдаёт новые строки панели
/// для записи в SQLite.
///
/// Мод не может писать в базу напрямую (Enforce Script не имеет доступа к
/// SQLite), поэтому события лежат построчно в JSONL. Файл только дополняется,
/// поэтому читаем по смещению и помним, сколько уже разобрали.
/// </summary>
public sealed class PlayerEventLogReader
{
    private readonly ILogger _logger;

    // Ключ в таблице meta: сколько байт журнала уже перенесено в базу.
    private const string MetaEventOffset = "events_offset";

    // Смещение прочитанного по каждому серверу (быстрый кэш в памяти).
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxLinesPerRead = 2000;

    public PlayerEventLogReader(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Путь к журналу событий сервера.</summary>
    public static string LogPath(ServerProfile profile)
    {
        var root = ModConfigWriter.ProfilesRoot(profile);
        return Path.Combine(root, "DayZPulseCore", "events.jsonl");
    }

    /// <summary>Читает новые строки журнала и разбирает их в события.</summary>
    public IReadOnlyList<PlayerEventRow> Read(ServerRuntime runtime, TrackerDatabase database)
    {
        var path = LogPath(runtime.Profile);
        if (!File.Exists(path))
        {
            return [];
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch
        {
            return [];
        }

        // Смещение храним в базе, а не только в памяти: при перезапуске панели
        // уже прочитанные строки не должны попасть в базу второй раз.
        var savedOffset = database.GetMeta(MetaEventOffset);
        var offset = savedOffset is { Length: > 0 } && long.TryParse(savedOffset, out var storedOffset)
            ? storedOffset
            : _offsets.GetValueOrDefault(runtime.Id, 0);

        // Файл переписали (ротация / первый запуск сервера) — читаем с начала.
        if (info.Length < offset)
        {
            offset = 0;
        }

        if (info.Length == offset)
        {
            return [];
        }

        var result = new List<PlayerEventRow>();

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            // Последняя строка может быть недописана (мод только что открыл
            // файл). Разбираем только завершённые строки, а её оставляем на
            // следующий тик: смещение двигаем ровно на разобранную часть.
            var lastNewline = text.LastIndexOf('\n');

            if (lastNewline < 0)
            {
                return [];
            }

            var complete = text[..lastNewline];
            var consumed = offset + System.Text.Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);

            var linesRead = 0;
            foreach (var line in complete.Split('\n'))
            {
                if (linesRead >= MaxLinesPerRead)
                {
                    break;
                }

                linesRead++;

                var parsed = Parse(line.TrimEnd('\r'));
                if (parsed is not null)
                {
                    result.Add(parsed);
                }
            }

            _offsets[runtime.Id] = Math.Min(consumed, info.Length);
            database.SetMeta(MetaEventOffset, _offsets[runtime.Id].ToString());
        }
        catch (IOException)
        {
            // Файл занят — прочитаем на следующем тике.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Server}] Не удалось прочитать журнал событий.", runtime.Name);
        }

        return result;
    }

    /// <summary>Сбрасывает смещение: сервер перезапустился, журнал начнётся заново.</summary>
    public void Reset(string serverId) => _offsets.Remove(serverId);

    /// <summary>Разбирает одну строку JSONL в событие.</summary>
    private static PlayerEventRow? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var type = Str(root, "t");
            if (type.Length == 0)
            {
                return null;
            }

            // Мод пишет миллисекунды с запуска движка; абсолютного времени у
            // него нет. Событие попало в файл сейчас, поэтому ставим текущее.
            var at = DateTimeOffset.Now;

            return new PlayerEventRow(
                at,
                type,
                Str(root, "id"),
                Str(root, "name"),
                Str(root, "killerId"),
                Str(root, "killerName"),
                Str(root, "class"),
                Str(root, "slot"),
                Str(root, "weapon"),
                Detail(root, type),
                Num(root, "x"),
                Num(root, "z"),
                line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Смысловое поле события: часть постройки, источник смерти, дистанция.</summary>
    private static string Detail(JsonElement root, string type)
    {
        // Для килла кладём дистанцию (в метрах) — показывается в киллфиде.
        if (type == "kill" && root.TryGetProperty("distance", out var distance) &&
            distance.ValueKind == JsonValueKind.Number)
        {
            return distance.GetDouble().ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }

        var part = Str(root, "part");
        if (part.Length > 0)
        {
            return part;
        }

        return Str(root, "source");
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double Num(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
}
