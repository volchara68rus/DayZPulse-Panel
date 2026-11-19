using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DayZPulse.Core;
using DayZPulse.Infrastructure;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

public sealed record AdminLogFile(string Path, string Name, DateTimeOffset Modified, long Size);

/// <summary>
/// Читает админ-логи DayZ (.ADM) одного сервера: убийства, смерти и чат.
/// Файлы лежат в <c>profiles/*.ADM</c> и переживают перезапуск сервера.
/// </summary>
public sealed partial class AdminLogService
{
    private readonly TrackerStore _store;
    private readonly ILogger<AdminLogService> _logger;
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    private ServerProfile _profile;
    private bool _initialized;

    public AdminLogService(ILogger<AdminLogService> logger, SettingsStore settings,
        ServerProfile profile, TrackerStore store)
    {
        _logger = logger;
        _profile = profile;
        _store = store;
    }

    public void UpdateProfile(ServerProfile profile) => _profile = profile;

    /// <summary>Каталог, где сервер хранит профили и админ-логи.</summary>
    public string ProfilesDirectory => ProfilesDirectoryOf(_profile.Server);

    /// <summary>
    /// Каталог профилей сервера (где лежат .ADM и папка BattlEye).
    /// Пусто в настройках — <c>&lt;рабочий каталог&gt;\profiles</c>.
    /// </summary>
    public static string ProfilesDirectoryOf(ServerSettings server)
    {
        var working = ServerPaths.WorkingDirectory(server);
        var profiles = server.ProfilesDirectory;

        return string.IsNullOrWhiteSpace(profiles)
            ? Path.Combine(working, "profiles")
            : Path.IsPathRooted(profiles) ? profiles : Path.Combine(working, profiles);
    }

    public IReadOnlyList<AdminLogFile> ListFiles()
    {
        try
        {
            var directory = ProfilesDirectory;
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return Directory.EnumerateFiles(directory, "*.ADM", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new AdminLogFile(f.FullName, f.Name, new DateTimeOffset(f.LastWriteTimeUtc), f.Length))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось перечислить админ-логи.");
            return [];
        }
    }

    /// <summary>Читает новые строки админ-логов и обновляет трекер.</summary>
    public int Poll()
    {
        var total = 0;

        foreach (var file in ListFiles().Take(8))
        {
            total += ReadFile(file);
        }

        _initialized = true;
        return total;
    }

    private int ReadFile(AdminLogFile file)
    {
        try
        {
            // При первом чтении пропускаем давнюю историю: она не относится к текущей
            // сессии, а файл может быть очень большим.
            if (!_initialized && !_offsets.ContainsKey(file.Path) && file.Size > 512 * 1024)
            {
                _offsets[file.Path] = file.Size - 512 * 1024;
            }

            var offset = _offsets.GetValueOrDefault(file.Path, 0);
            if (offset > file.Size)
            {
                // Файл перезаписан (ротация) — читаем с начала.
                offset = 0;
            }

            if (offset == file.Size)
            {
                return 0;
            }

            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();

            // Последняя строка обычно без перевода строки: пока файл растёт, ждём её
            // завершения; если он давно не менялся — разбираем и хвост.
            var lastNewline = text.LastIndexOf('\n');
            string complete;
            long advance;

            if (lastNewline < 0)
            {
                if (!IsTailReady(file))
                {
                    return 0;
                }

                complete = text;
                advance = file.Size;
            }
            else
            {
                complete = text[..lastNewline];
                advance = offset + Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);

                var tail = text[(lastNewline + 1)..];
                if (tail.Trim().Length > 0 && IsTailReady(file))
                {
                    complete += "\n" + tail;
                    advance = file.Size;
                }
            }

            _offsets[file.Path] = advance;

            var kills = new List<KillEvent>();
            var chat = new List<ChatEvent>();
            var date = DateOf(file);

            foreach (var line in complete.Split('\n'))
            {
                ParseLine(line.TrimEnd('\r'), date, kills, chat);
            }

            if (kills.Count > 0)
            {
                _store.AddKills(kills);
            }

            if (chat.Count > 0)
            {
                _store.AddChat(chat);
            }

            return kills.Count + chat.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось прочитать админ-лог {Path}.", file.Path);
            return 0;
        }
    }

    /// <summary>Хвост файла можно разбирать, если запись давно завершена.</summary>
    private static bool IsTailReady(AdminLogFile file) =>
        file.Modified < DateTimeOffset.Now.AddSeconds(-3);

    /// <summary>Дата из имени лога (dayz_YYYY-MM-DD_HH-MM-SS.ADM) задаёт день событий.</summary>
    private static DateTimeOffset DateOf(AdminLogFile file)
    {
        if (FileNameDate().Match(file.Name) is { Success: true } match &&
            DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
        }

        return file.Modified;
    }

    private static void ParseLine(string line, DateTimeOffset fallbackDate, List<KillEvent> kills, List<ChatEvent> chat)
    {
        if (line.Length == 0)
        {
            return;
        }

        var timeMatch = LogTime().Match(line);
        if (!timeMatch.Success)
        {
            return;
        }

        // Строка: " 0:15:23 | Player "Name"(id=... guid=...) ..."
        var at = fallbackDate.Date;
        if (TimeSpan.TryParse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var time))
        {
            at = fallbackDate.Date.Add(time);
        }

        var content = line[(timeMatch.Index + timeMatch.Length)..].TrimStart(' ', '|');

        if (content.Contains(" killed by ", StringComparison.OrdinalIgnoreCase))
        {
            var match = KillPattern().Match(content);
            if (match.Success)
            {
                kills.Add(new KillEvent
                {
                    At = at,
                    VictimName = match.Groups[1].Value,
                    VictimId = match.Groups[2].Value,
                    KillerName = match.Groups[3].Value,
                    KillerId = match.Groups[4].Value,
                    Weapon = NormalizeWeapon(match.Groups[5].Value),
                    Distance = double.TryParse(match.Groups[6].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var d) ? d : 0
                });
            }

            return;
        }

        var suicide = SuicidePattern().Match(content);
        if (suicide.Success)
        {
            kills.Add(new KillEvent
            {
                At = at,
                VictimName = suicide.Groups[1].Value,
                VictimId = suicide.Groups[2].Value,
                IsSuicide = true,
                Weapon = NormalizeWeapon(suicide.Groups[3].Value)
            });
            return;
        }

        var death = DeathPattern().Match(content);
        if (death.Success)
        {
            kills.Add(new KillEvent
            {
                At = at,
                VictimName = death.Groups[1].Value,
                VictimId = death.Groups[2].Value,
                IsEnvironment = true
            });
            return;
        }

        // Чат: Chat("Имя"(id=..)): текст (канал виден по префиксу).
        var chatMatch = ChatPattern().Match(content);
        if (chatMatch.Success)
        {
            var text = chatMatch.Groups[3].Value.Trim();

            // Служебные сообщения игры в логе не показываем как чат.
            if (text.Length > 0)
            {
                chat.Add(new ChatEvent
                {
                    At = at,
                    PlayerName = chatMatch.Groups[1].Value,
                    PlayerId = chatMatch.Groups[2].Value,
                    Text = text
                });
            }
        }
    }

    private static string NormalizeWeapon(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var name = raw.Trim();
        return name.Equals("Fists", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Melee", StringComparison.OrdinalIgnoreCase)
            ? "руками"
            : name;
    }

    [GeneratedRegex(@"(\d{1,2}:\d{2}:\d{2})\s*\|")]
    private static partial Regex LogTime();

    [GeneratedRegex(@"^Player\s+""([^""]*)""\s*\(id=([^\s)]*)[^)]*\)\s+killed by\s+Player\s+""([^""]*)""\s*\(id=([^\s)]*)[^)]*\)\s+with\s+(\S+?)(?:\s+from\s+([\d.]+)\s*meters)?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KillPattern();

    [GeneratedRegex(@"^Player\s+""([^""]*)""\s*\(id=([^\s)]*)[^)]*\)\s+committed suicide with\s+(\S+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SuicidePattern();

    // Смерть игрока. В логе бывает пометка "(DEAD)" между именем и id:
    //   Player "Name" (id=...) died. Stats> ...
    //   Player "Name" (DEAD) (id=...) died. Stats> ...
    [GeneratedRegex(@"^Player\s+""([^""]*)""(?:\s*\(DEAD\))?\s*\(id=([^\s)]*)[^)]*\)\s+died",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeathPattern();

    // Чат из админ-лога: Chat("Имя"(id=GUID)): текст.
    // Канал бывает разных видов: Chat/Global/Direct/Group/Vehicle/Transceiver.
    [GeneratedRegex(@"^(?:Chat|Global|Direct|Group|Vehicle|Radio|Transceiver|Command)\s*\(\s*""([^""]*)""\s*\(id=([^\s)]*)[^)]*\)\s*\)\s*:\s*(.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChatPattern();

    [GeneratedRegex(@"dayz_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.ADM$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameDate();
}
