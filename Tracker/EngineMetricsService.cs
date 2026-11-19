using System.Globalization;
using System.Text.RegularExpressions;
using DayZPulse.Core;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>
/// Метрики производительности, которые движок пишет в RPT-лог: те же данные,
/// что показывает админ-команда <c>#monitor</c> (средний FPS, потребление
/// памяти, число игроков, зомби и животных).
///
/// Зачем из лога, а не командой: <c>#monitor</c> — игровая чат-команда, её вывод
/// идёт в чат админа и в консоль сервера не попадает, перехватить его нельзя.
/// Но при включённых <c>logAverageFps</c>/<c>logMemory</c>/<c>logPlayers</c>/<c>logAI</c>
/// движок печатает ровно те же строки в RPT, откуда панель их и читает.
///
/// Формат строк (взят из DayZServer_x64.exe):
///   Average server FPS: %0.2f (measured interval: %d s)
///   Used memory: %d KB
///   Players: %d in total
///   Infected: alive=%d; dead=%d; sleeping=%d; distant=%d;
///   Animals: alive=%d; dead=%d; sleeping=%d; distant=%d;
/// </summary>
public sealed class EngineMetricsService
{
    private readonly ILogger<EngineMetricsService> _logger;

    // Смещение уже прочитанной части RPT по каждому серверу (чтобы не парсить
    // весь файл заново: он растёт и к концу сессии бывает десятков мегабайт).
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EngineMetrics> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileByServer = new(StringComparer.OrdinalIgnoreCase);

    public EngineMetricsService(ILogger<EngineMetricsService> logger)
    {
        _logger = logger;
    }

    /// <summary>Последние метрики движка для сервера (null — ещё не читали).</summary>
    public EngineMetrics? Latest(string serverId) =>
        _latest.TryGetValue(serverId, out var m) ? m : null;

    /// <summary>
    /// Полный сброс: смещение чтения, привязка к файлу и последние метрики.
    /// Вызывается при остановке (данные прошлого запуска больше не актуальны)
    /// и при перезапуске (RPT начинается заново).
    /// </summary>
    public void Reset(string serverId)
    {
        _offsets.Remove(serverId);
        _fileByServer.Remove(serverId);
        _latest.Remove(serverId);
    }

    /// <summary>
    /// Читает новые строки RPT и обновляет метрики. Вызывается периодически;
    /// читает только дописанную часть файла.
    /// </summary>
    /// <param name="notBefore">
    /// Момент запуска сервера. RPT старше него — это лог прошлого запуска
    /// (новый файл создаётся не сразу), его метрики неактуальны.
    /// </param>
    public void Poll(ServerProfile profile, string serverId, DateTimeOffset? notBefore = null)
    {
        var path = NewestRpt(profile);
        if (path is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);

            // Лог прошлого запуска: движок ещё не создал новый RPT.
            // Иначе панель показала бы старые FPS и память как свежие.
            // Запас в 2 минуты — на разницу часов и момент создания файла.
            if (notBefore is { } start &&
                info.LastWriteTimeUtc < start.UtcDateTime.AddMinutes(-2))
            {
                return;
            }

            // Сменился файл (новый запуск сервера) — читаем с начала.
            if (_fileByServer.TryGetValue(serverId, out var previous) &&
                !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
            {
                _offsets[serverId] = 0;
            }
            _fileByServer[serverId] = path;

            var offset = _offsets.GetValueOrDefault(serverId, 0);

            // Файл переписали (ротация) — начинаем заново.
            if (info.Length < offset)
            {
                offset = 0;
            }

            if (info.Length == offset)
            {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            // Последняя строка может быть недописана — разбираем только
            // завершённые, а смещение двигаем ровно на разобранную часть.
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                return;
            }

            var complete = text[..lastNewline];
            var consumed = offset + System.Text.Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);
            _offsets[serverId] = Math.Min(consumed, info.Length);

            var metrics = _latest.TryGetValue(serverId, out var current)
                ? current
                : new EngineMetrics();

            var changed = false;

            foreach (var line in complete.Split('\n'))
            {
                changed |= Apply(line, metrics);
            }

            if (changed)
            {
                metrics.At = DateTimeOffset.Now;
                _latest[serverId] = metrics;
            }
        }
        catch (IOException)
        {
            // Файл занят — прочитаем в следующий раз.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Server}] Не удалось прочитать метрики движка из RPT.", profile.Name);
        }
    }

    /// <summary>Разбирает одну строку лога. Возвращает true, если что-то обновилось.</summary>
    private static bool Apply(string line, EngineMetrics metrics)
    {
        if (line.Length == 0)
        {
            return false;
        }

        // Average server FPS: 2838.40 (measured interval: 5 s)
        var m = Fps.Match(line);
        if (m.Success && TryFloat(m.Groups["v"].Value, out var fps))
        {
            metrics.Fps = fps;
            return true;
        }

        // Used memory: 3040108 KB
        m = Memory.Match(line);
        if (m.Success && long.TryParse(m.Groups["v"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var kb))
        {
            metrics.MemoryBytes = kb * 1024;
            return true;
        }

        // Players: 3 in total
        m = Players.Match(line);
        if (m.Success && int.TryParse(m.Groups["v"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var players))
        {
            metrics.Players = players;
            return true;
        }

        // Infected: alive=0; dead=0; sleeping=45; distant=0;
        m = Infected.Match(line);
        if (m.Success)
        {
            metrics.InfectedAlive = Int(m, "a");
            metrics.InfectedDead = Int(m, "d");
            metrics.InfectedSleeping = Int(m, "s");
            metrics.InfectedDistant = Int(m, "t");
            return true;
        }

        // Animals: alive=0; dead=0; sleeping=0; distant=171;
        m = Animals.Match(line);
        if (m.Success)
        {
            metrics.AnimalsAlive = Int(m, "a");
            metrics.AnimalsDead = Int(m, "d");
            metrics.AnimalsSleeping = Int(m, "s");
            metrics.AnimalsDistant = Int(m, "t");
            return true;
        }

        return false;
    }

    private static int Int(Match m, string group) =>
        int.TryParse(m.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static bool TryFloat(string value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>Самый свежий RPT-лог сервера.</summary>
    private static string? NewestRpt(ServerProfile profile)
    {
        try
        {
            var directory = ServerLogService.ProfilesDirectory(profile);
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory.EnumerateFiles(directory, "*.RPT", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex Fps = new(
        @"Average server FPS:\s*(?<v>[-\d.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Memory = new(
        @"Used memory:\s*(?<v>\d+)\s*KB", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Players = new(
        @"Players:\s*(?<v>\d+)\s+in total", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Infected = new(
        @"Infected:\s*alive=(?<a>\d+);\s*dead=(?<d>\d+);\s*sleeping=(?<s>\d+);\s*distant=(?<t>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Animals = new(
        @"Animals:\s*alive=(?<a>\d+);\s*dead=(?<d>\d+);\s*sleeping=(?<s>\d+);\s*distant=(?<t>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}

/// <summary>
/// Метрики производительности из лога движка — аналог вывода команды
/// <c>#monitor</c>. Значения ступенчато обновляются движком (интервал в
/// секундах задаётся флагами logAverageFps/logMemory/logPlayers/logAI).
/// </summary>
public sealed class EngineMetrics
{
    /// <summary>Средний FPS сервера.</summary>
    public float Fps { get; set; }

    /// <summary>Потребление памяти серверным процессом, байты.</summary>
    public long MemoryBytes { get; set; }

    /// <summary>Игроков на сервере.</summary>
    public int Players { get; set; }

    public int InfectedAlive { get; set; }
    public int InfectedDead { get; set; }
    public int InfectedSleeping { get; set; }
    public int InfectedDistant { get; set; }

    public int AnimalsAlive { get; set; }
    public int AnimalsDead { get; set; }
    public int AnimalsSleeping { get; set; }
    public int AnimalsDistant { get; set; }

    /// <summary>Когда метрики обновились в последний раз.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Всего зомби: живые + спящие + далёкие.</summary>
    public int InfectedTotal => InfectedAlive + InfectedSleeping + InfectedDistant;

    public int AnimalsTotal => AnimalsAlive + AnimalsSleeping + AnimalsDistant;
}
