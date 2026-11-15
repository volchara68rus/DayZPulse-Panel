using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DayZPulse.Models;

namespace DayZPulse.Core;

/// <summary>Параметры тайловой карты, полученные от xam.nu.</summary>
public sealed record MapInfo(
    string Key,
    int Size,
    string Tiles,
    string Tilev,
    string Format);

/// <summary>
/// Карта DayZ для «живой карты»: тайлы и метаданные берём с dayz.xam.nu,
/// ничего не вшивая в сборку. Сайт отдаёт конфигурацию карты только
/// с заголовком <c>Referer</c>, поэтому запросы идут через панель,
/// а браузер получает тайлы уже со своего origin (строгий CSP панели
/// запрещает внешние картинки).
/// </summary>
public sealed class DayZMapService
{
    private const string SiteRoot = "https://dayz.xam.nu/";
    private const string StaticRoot = "https://static.xam.nu";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly ILogger<DayZMapService> _logger;

    // Небольшой кэш тайлов в памяти: при перетаскивании одни и те же
    // квадраты запрашиваются повторно. Ограничиваем размер, чтобы
    // не занимать память бесконечно.
    private readonly object _tileLock = new();
    private readonly Dictionary<string, byte[]> _tileCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _tileOrder = new();
    private const int TileCacheLimit = 512;

    // Кэш метаданных карты: тайл-сервер меняет tilev со временем,
    // поэтому периодически перечитываем конфигурацию.
    private static readonly TimeSpan InfoTtl = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (MapInfo Info, DateTimeOffset At)> _infoCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Таблица версий карт из бандла сайта (обновляется вместе с игрой).
    private static readonly TimeSpan VersionsTtl = TimeSpan.FromHours(12);
    private IReadOnlyDictionary<string, MapVersion>? _versions;
    private DateTimeOffset _versionsAt = DateTimeOffset.MinValue;

    /// <summary>Версия карты на xam.nu: «1.30» и итерация (0 — без суффикса).</summary>
    private sealed record MapVersion(string Version, string Iteration);

    public DayZMapService(ILogger<DayZMapService> logger)
    {
        _logger = logger;
    }

    /// <summary>Имя карты xam.nu по имени миссии или профиля сервера.</summary>
    public static string? NormalizeMap(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0)
        {
            return null;
        }

        // «dayzOffline.chernarusplus» → «chernarusplus».
        if (v.StartsWith("dayzoffline.", StringComparison.Ordinal))
        {
            v = v["dayzoffline.".Length..];
        }
        else
        {
            var dot = v.LastIndexOf('.');
            if (dot >= 0 && dot < v.Length - 1)
            {
                var tail = v[(dot + 1)..];
                v = tail.Length > 0 ? tail : v;
            }
        }

        return v switch
        {
            "chernarus" or "chernarusplus" or "chernarusplusexp" or "chernarus2035" => "chernarusplus",
            "enoch" or "livonia" or "livoniaexp" => "livonia",
            "sakhal" or "sakhalexp" => "sakhal",
            "" => null,
            _ => v
        };
    }

    /// <summary>Метаданные карты; null — карта не поддерживается.</summary>
    public Task<MapInfo?> ResolveAsync(ServerSettings server, string? fallbackMap, CancellationToken ct)
    {
        var mission = QuickConfig.MissionTemplateOr(server);
        return ResolveAsync(string.IsNullOrWhiteSpace(mission) ? fallbackMap : mission, ct);
    }

    /// <summary>Метаданные карты; null — карта не поддерживается.</summary>
    public async Task<MapInfo?> ResolveAsync(string? mapName, CancellationToken ct)
    {
        var key = NormalizeMap(mapName);
        if (key is null)
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_infoCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < InfoTtl)
            {
                return cached.Info;
            }

            var info = await FetchInfoAsync(key, ct);
            if (info is not null)
            {
                _infoCache[key] = (info, DateTimeOffset.UtcNow);
            }

            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Скачивает один тайл карты. null — тайла нет.</summary>
    public async Task<byte[]?> GetTileAsync(MapInfo info, string layer, int z, int x, int y, CancellationToken ct)
    {
        if (layer is not ("topographic" or "satellite"))
        {
            return null;
        }

        var max = (1 << z) - 1;
        if (z is < 0 or > 9 || x < 0 || y < 0 || x > max || y > max)
        {
            return null;
        }

        var url = $"{StaticRoot}/dayz/maps/{info.Tiles}/{info.Tilev}/{layer}/{z}/{x}/{y}.{info.Format}";

        lock (_tileLock)
        {
            if (_tileCache.TryGetValue(url, out var cached))
            {
                return cached;
            }
        }

        var bytes = await DownloadAsync(url, ct);
        if (bytes is null)
        {
            return null;
        }

        lock (_tileLock)
        {
            if (!_tileCache.ContainsKey(url))
            {
                _tileCache[url] = bytes;
                _tileOrder.Enqueue(url);
                while (_tileOrder.Count > TileCacheLimit)
                {
                    _tileCache.Remove(_tileOrder.Dequeue());
                }
            }
        }

        return bytes;
    }

    // ------------------------------------------------------------ внутреннее

    private async Task<MapInfo?> FetchInfoAsync(string key, CancellationToken ct)
    {
        var versions = await GetVersionsAsync(ct);

        var candidates = new List<string>();
        if (versions?.TryGetValue(key, out var v) == true)
        {
            candidates.Add(v.Iteration == "0" ? v.Version : $"{v.Version}-{v.Iteration}");
        }

        // Запасной вариант, если таблица версий недоступна.
        candidates.AddRange(["1.30", "1.29", "1.28-2"]);

        foreach (var file in candidates.Distinct())
        {
            var url = $"{StaticRoot}/dayz/json/{key}/{file}.json";
            var bytes = await DownloadAsync(url, ct);
            if (bytes is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (!doc.RootElement.TryGetProperty("info", out var info))
                {
                    continue;
                }

                var tiles = info.TryGetProperty("tiles", out var t) ? t.GetString() : key;
                var tilev = info.TryGetProperty("tilev", out var tv) ? tv.GetString() : null;
                var format = info.TryGetProperty("format", out var f) ? f.GetString() : "webp";
                var size = info.TryGetProperty("size", out var s) && s.TryGetInt32(out var sz) ? sz : 15360;

                if (string.IsNullOrEmpty(tiles) || string.IsNullOrEmpty(tilev))
                {
                    continue;
                }

                return new MapInfo(key, size, tiles!, tilev!, string.IsNullOrEmpty(format) ? "webp" : format!);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Некорректная конфигурация карты {Key} ({File})", key, file);
            }
        }

        _logger.LogWarning("Не удалось получить карту {Key} с xam.nu", key);
        return null;
    }

    private async Task<IReadOnlyDictionary<string, MapVersion>?> GetVersionsAsync(CancellationToken ct)
    {
        if (_versions is not null && DateTimeOffset.UtcNow - _versionsAt < VersionsTtl)
        {
            return _versions;
        }

        try
        {
            // 1) Со страницы сайта узнаём имя бандла (оно меняется при обновлении).
            var html = await DownloadAsync(SiteRoot, ct);
            if (html is null)
            {
                return _versions;
            }

            var page = System.Text.Encoding.UTF8.GetString(html);
            var match = Regex.Match(page, @"/js/bundle\.\d+\.[a-f0-9]+\.js");
            if (!match.Success)
            {
                return _versions;
            }

            // 2) В бандле лежит таблица соответствия карта → версия/итерация.
            var bundle = await DownloadAsync(SiteRoot.TrimEnd('/') + match.Value, ct);
            if (bundle is null)
            {
                return _versions;
            }

            var script = System.Text.Encoding.UTF8.GetString(bundle);
            var table = Regex.Match(script, @"JSON\.parse\('(\{""chernarusplus"".*?\})'\)", RegexOptions.Singleline);
            if (!table.Success)
            {
                return _versions;
            }

            var parsed = ParseVersionTable(table.Groups[1].Value);
            if (parsed.Count > 0)
            {
                _versions = parsed;
                _versionsAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать таблицу версий карт xam.nu");
        }

        return _versions;
    }

    private static IReadOnlyDictionary<string, MapVersion> ParseVersionTable(string json)
    {
        var result = new Dictionary<string, MapVersion>(StringComparer.OrdinalIgnoreCase);

        // Строки экранированы как в JS-литерале: \" и \/ — снимаем лишние слэши.
        var cleaned = json.Replace("\\\"", "\"").Replace("\\/", "/");

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var version = property.Value.TryGetProperty("v", out var v) ? v.GetString() : null;
                var iteration = property.Value.TryGetProperty("i", out var i)
                    ? (i.ValueKind == JsonValueKind.Number ? i.GetInt32().ToString() : i.GetString())
                    : "0";
                iteration ??= "0";

                if (!string.IsNullOrEmpty(version))
                {
                    result[property.Name] = new MapVersion(version!, iteration);
                }
            }
        }
        catch (JsonException)
        {
            // Таблица не разобралась — вернём пустую, сработает запасной вариант.
        }

        return result;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(SiteRoot);
            request.Headers.UserAgent.ParseAdd("DayZPulse/1.0 (+local panel)");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Запрос к xam.nu не удался: {Url}", url);
            return null;
        }
    }
}
