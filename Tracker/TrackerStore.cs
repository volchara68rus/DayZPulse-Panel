using System.Text.Json;

namespace DayZPulse.Tracker;

/// <summary>
/// Хранилище трекера: живой снимок от мода, история игроков, треки, килфид,
/// чат и нарушения. Всё в памяти процесса с периодическим сбросом на диск.
/// </summary>
public sealed class TrackerStore
{
    private readonly object _gate = new();
    private readonly ILogger<TrackerStore> _logger;

    private readonly Dictionary<string, LivePlayer> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlayerRecord> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TrackPoint>> _tracks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Снимки инвентаря игроков (JSON от мода), ключ — id игрока.</summary>
    private readonly Dictionary<string, PlayerInventory> _inventory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<KillEvent> _kills = new();
    private readonly Queue<ChatEvent> _chat = new();
    private readonly Queue<Violation> _violations = new();

    private TrackedServerState? _server;
    private DateTimeOffset _lastIngest = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAcIngest = DateTimeOffset.MinValue;
    private long _sequence;
    private long _violationId;
    private bool _dirty;

    /// <summary>Включён ли демонстрационный игрок (для проверки интерфейса).</summary>
    private bool _demoPlayer;

    private const int MaxKills = 1000;
    private const int MaxChat = 500;
    private const int MaxViolations = 2000;
    private const int MaxTrackPointsPerPlayer = 5000;

    /// <summary>История онлайна за 24 часа: точка на каждые 5 минут.</summary>
    private readonly List<OnlinePoint> _online = [];

    private DateTimeOffset _lastOnlineSample = DateTimeOffset.MinValue;

    private const int OnlineRetentionHours = 24;
    private static readonly TimeSpan OnlineSampleInterval = TimeSpan.FromMinutes(5);

    public TrackerStore(ILogger<TrackerStore> logger)
    {
        _logger = logger;
    }

    public DateTimeOffset LastIngest
    {
        get
        {
            lock (_gate)
            {
                return _lastIngest;
            }
        }
    }

    public DateTimeOffset LastAcIngest
    {
        get
        {
            lock (_gate)
            {
                return _lastAcIngest;
            }
        }
    }

    public bool IsConnected(int staleSeconds)
    {
        lock (_gate)
        {
            return _lastIngest != DateTimeOffset.MinValue &&
                   (DateTimeOffset.Now - _lastIngest).TotalSeconds <= Math.Max(5, staleSeconds);
        }
    }

    /// <summary>Полный снимок от мода заменяет живое состояние целиком.</summary>
    public void ApplySnapshot(TrackedServerState server, IEnumerable<LivePlayer> players, DateTimeOffset now)
    {
        lock (_gate)
        {
            _server = server;
            _lastIngest = now;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var player in players)
            {
                // Игрок без личности (unknown_N) — пропускаем: такое в базе
                // только мусорит (id вроде unknown_0 без имени).
                if (string.IsNullOrWhiteSpace(player.Id) || IsPlaceholderId(player.Id))
                {
                    continue;
                }

                player.UpdatedAt = now;
                seen.Add(player.Id);
                _live[player.Id] = player;

                var track = _tracks.TryGetValue(player.Id, out var existing) ? existing : [];
                if (track.Count == 0 || (now - track[^1].At).TotalSeconds >= 2)
                {
                    track.Add(new TrackPoint(now, player.X, player.Z));
                    while (track.Count > MaxTrackPointsPerPlayer)
                    {
                        track.RemoveAt(0);
                    }
                }

                _tracks[player.Id] = track;
                TouchPlayer(player, now);
            }

            foreach (var id in _live.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                _live.Remove(id);
            }

            // Демонстрационный игрок не должен исчезать при снимке от мода.
            if (_demoPlayer)
            {
                AddDemoPlayer(now);
            }

            _dirty = true;

            // Точку онлайна пишем не чаще, чем раз в 5 минут.
            if (now - _lastOnlineSample >= OnlineSampleInterval)
            {
                _lastOnlineSample = now;
                _online.Add(new OnlinePoint(now, server.Players, server.Alive));
                PruneOnline(now);
            }
        }
    }

    private void PruneOnline(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromHours(OnlineRetentionHours);
        _online.RemoveAll(p => p.At < cutoff);
    }

    /// <summary>Отмечает сессию игрока: считает заходы и накапливает наигранное время.</summary>
    private void TouchPlayer(LivePlayer player, DateTimeOffset now)
    {
        if (!_players.TryGetValue(player.Id, out var record))
        {
            record = new PlayerRecord
            {
                Id = player.Id,
                PlainId = player.PlainId,
                Name = player.Name,
                FirstSeen = now,
                LastSeen = now,
                Sessions = 1
            };
            record.Aliases.Add(player.Name);
            _players[player.Id] = record;
            return;
        }

        var lastGap = (now - record.LastSeen).TotalSeconds;
        // Новый заход: разрыв больше минуты.
        if (lastGap > 60)
        {
            record.Sessions++;
        }
        else if (lastGap > 0)
        {
            record.PlaytimeSeconds += lastGap;
        }

        record.LastSeen = now;
        if (!string.IsNullOrWhiteSpace(player.PlainId))
        {
            record.PlainId = player.PlainId;
        }

        if (!string.IsNullOrWhiteSpace(player.Name) && record.Name != player.Name)
        {
            record.Name = player.Name;
            if (!record.Aliases.Contains(player.Name))
            {
                record.Aliases.Add(player.Name);
            }
        }
    }

    public void AddKills(IEnumerable<KillEvent> kills)
    {
        lock (_gate)
        {
            foreach (var kill in kills)
            {
                kill.Seq = ++_sequence;
                _kills.Enqueue(kill);
                while (_kills.Count > MaxKills)
                {
                    _kills.Dequeue();
                }

                ApplyKillToPlayers(kill);
            }

            _dirty = true;
        }
    }

    private void ApplyKillToPlayers(KillEvent kill)
    {
        if (!kill.IsSuicide && !kill.IsEnvironment && !string.IsNullOrWhiteSpace(kill.KillerId))
        {
            var killer = EnsureRecord(kill.KillerId, kill.KillerName);
            killer.Kills++;
        }

        if (!string.IsNullOrWhiteSpace(kill.VictimId))
        {
            var victim = EnsureRecord(kill.VictimId, kill.VictimName);
            victim.Deaths++;
        }
    }

    private PlayerRecord EnsureRecord(string id, string name)
    {
        if (_players.TryGetValue(id, out var record))
        {
            return record;
        }

        record = new PlayerRecord { Id = id, Name = name, FirstSeen = DateTimeOffset.Now, LastSeen = DateTimeOffset.Now };
        if (!string.IsNullOrWhiteSpace(name))
        {
            record.Aliases.Add(name);
        }

        _players[id] = record;
        return record;
    }

    public void AddChat(IEnumerable<ChatEvent> messages)
    {
        lock (_gate)
        {
            foreach (var message in messages)
            {
                message.Seq = ++_sequence;
                _chat.Enqueue(message);
                while (_chat.Count > MaxChat)
                {
                    _chat.Dequeue();
                }
            }
        }
    }

    public int AddViolations(IEnumerable<Violation> violations, DateTimeOffset now)
    {
        lock (_gate)
        {
            var added = 0;
            foreach (var violation in violations)
            {
                violation.Id = ++_violationId;
                violation.At = now;
                _violations.Enqueue(violation);
                while (_violations.Count > MaxViolations)
                {
                    _violations.Dequeue();
                }

                if (!string.IsNullOrWhiteSpace(violation.PlayerId))
                {
                    var record = EnsureRecord(violation.PlayerId, violation.PlayerName);
                    record.TotalViolationPoints = Math.Max(record.TotalViolationPoints, violation.TotalPoints);
                }

                added++;
            }

            _lastAcIngest = now;
            _dirty = true;
            return added;
        }
    }

    // ------------------------------------------------------------- выборки

    public IReadOnlyList<LivePlayer> LivePlayers()
    {
        lock (_gate)
        {
            return _live.Values
                .OrderByDescending(p => p.Alive)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public LivePlayer? LivePlayer(string id)
    {
        lock (_gate)
        {
            return _live.TryGetValue(id, out var player) ? player : null;
        }
    }

    /// <summary>Сохраняет снимок инвентаря, присланный модом.</summary>
    public void SetInventory(string playerId, string playerName, string json, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        lock (_gate)
        {
            _inventory[playerId] = new PlayerInventory
            {
                PlayerId = playerId,
                PlayerName = playerName ?? "",
                Json = string.IsNullOrWhiteSpace(json) ? "{}" : json,
                At = at
            };
        }
    }

    /// <summary>Снимок инвентаря игрока или null, если мод ещё не присылал.</summary>
    public PlayerInventory? Inventory(string playerId)
    {
        lock (_gate)
        {
            return _inventory.TryGetValue(playerId, out var inventory) ? inventory : null;
        }
    }

    /// <summary>Добавляет или убирает демонстрационного игрока (для проверки UI).</summary>
    public void SetDemoPlayer(bool enabled, DateTimeOffset now)
    {
        lock (_gate)
        {
            _demoPlayer = enabled;

            if (enabled)
            {
                AddDemoPlayer(now);
                AddDemoInventory(now);
            }
            else
            {
                // Убираем из онлайна, но запись и инвентарь оставляем —
                // так демо-игрок ведёт себя как реальный оффлайн-игрок.
                _live.Remove(DemoPlayerId);
            }
        }
    }

    /// <summary>Раскладывает демонстрационный инвентарь, чтобы было что смотреть.</summary>
    private void AddDemoInventory(DateTimeOffset now)
    {
        _inventory[DemoPlayerId] = new PlayerInventory
        {
            PlayerId = DemoPlayerId,
            PlayerName = "Тестовый Выживший",
            At = now,
            Json = """
                {
                  "hands": { "class": "AK101", "qty": 1, "health": 0.82 },
                  "slots": [
                    { "slot": "Head", "item": { "class": "GorkaHelmet", "qty": 1, "health": 1 } },
                    { "slot": "Mask", "item": { "class": "BalaclavaMask_Black", "qty": 1, "health": 1 } },
                    { "slot": "Body", "item": { "class": "GorkaEJacket_Summer", "qty": 1, "health": 0.95 } },
                    { "slot": "Vest", "item": { "class": "PlateCarrierVest", "qty": 1, "health": 1 } },
                    { "slot": "Back", "item": { "class": "HuntingBackpack", "qty": 1, "health": 1 } },
                    { "slot": "Hips", "item": { "class": "ChernarusMap", "qty": 1, "health": 1 } },
                    { "slot": "Legs", "item": { "class": "GorkaPants_Summer", "qty": 1, "health": 1 } },
                    { "slot": "Feet", "item": { "class": "HikingBoots_Black", "qty": 1, "health": 1 } },
                    { "slot": "Gloves", "item": { "class": "TacticalGloves_Black", "qty": 1, "health": 1 } }
                  ],
                  "cargo": [
                    { "cat": "item", "item": { "class": "Bandage", "qty": 4, "health": 1 } },
                    { "cat": "item", "item": { "class": "Ammo_762x39", "qty": 60, "health": 1 } },
                    { "cat": "item", "item": { "class": "BakedBeansCan", "qty": 2, "health": 1 } },
                    { "cat": "item", "item": { "class": "WaterBottle", "qty": 1, "health": 0.6 } },
                    { "cat": "item", "item": { "class": "Morphine", "qty": 1, "health": 1 } },
                    { "cat": "item", "item": { "class": "Compass", "qty": 1, "health": 1 } },
                    { "cat": "item", "item": { "class": "Matchbox", "qty": 1, "health": 1 } }
                  ]
                }
                """
        };
    }

    /// <summary>Проверяет, включён ли демонстрационный игрок.</summary>
    public bool DemoPlayerEnabled
    {
        get
        {
            lock (_gate)
            {
                return _demoPlayer;
            }
        }
    }

    private const string DemoPlayerId = "demo-player";

    /// <summary>Кладёт демонстрационного игрока в живой список. Вызывать под _gate.</summary>
    private void AddDemoPlayer(DateTimeOffset now)
    {
        var player = new LivePlayer
        {
            Id = DemoPlayerId,
            PlainId = "76561198000000000",
            Name = "Тестовый Выживший",
            Alive = true,
            Health = 87.5,
            MaxHealth = 100,
            X = 6730,
            Y = 0,
            Z = 2420,
            Ping = 42,
            UpdatedAt = now,
            StatsJson = """
                {"water":{"v":72,"max":100},"energy":{"v":58,"max":100},
                 "heatComfort":{"v":0.7,"max":1},"stamina":{"v":86,"max":100},
                 "wet":{"v":0,"max":1},"toxicity":{"v":0,"max":1},
                 "health":{"v":87.5,"max":100},"blood":{"v":4900,"max":5000},
                 "shock":{"v":0,"max":100},"alive":true,"temperature":{"v":36.8}}
                """
        };

        _live[DemoPlayerId] = player;
        TouchPlayer(player, now);
    }

    public TrackedServerState? ServerState()
    {
        lock (_gate)
        {
            return _server;
        }
    }

    /// <summary>Есть ли живой снимок (кто-то онлайн).</summary>
    public bool HasLive
    {
        get
        {
            lock (_gate)
            {
                return _live.Count > 0 || _lastIngest != DateTimeOffset.MinValue;
            }
        }
    }

    /// <summary>
    /// Сбрасывает живое состояние: игроки онлайна, метрики сервера и признак
    /// связи. Вызывается при остановке сервера, иначе панель показывала бы
    /// «в сети 1» вечно. История игроков, треки, киллы и чат не трогаются.
    /// </summary>
    public void ClearLive()
    {
        lock (_gate)
        {
            _live.Clear();
            _server = null;
            _lastIngest = DateTimeOffset.MinValue;
            _dirty = true;
        }
    }

    /// <summary>История онлайна за последние 24 часа (точка раз в 5 минут).</summary>
    public IReadOnlyList<OnlinePoint> OnlineHistory()
    {
        lock (_gate)
        {
            return _online.ToArray();
        }
    }

    public IReadOnlyList<TrackPoint> Track(string id, DateTimeOffset? from, DateTimeOffset? to, int maxPoints)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(id, out var points))
            {
                return [];
            }

            var filtered = points
                .Where(p => (from is null || p.At >= from) && (to is null || p.At <= to))
                .ToArray();

            if (filtered.Length <= maxPoints)
            {
                return filtered;
            }

            // Прореживание с сохранением формы.
            var step = (double)filtered.Length / maxPoints;
            var result = new List<TrackPoint>(maxPoints);
            for (var i = 0; i < maxPoints; i++)
            {
                result.Add(filtered[(int)(i * step)]);
            }

            result[^1] = filtered[^1];
            return result;
        }
    }

    public IReadOnlyList<KillEvent> Kills(int limit = 100) =>
        Snapshot(_kills, limit);

    /// <summary>
    /// События конкретного игрока: его смерти, убийства и сообщения чата.
    /// Сопоставляем по id (BI/SteamID) или имени — записи мода и ADM-лога
    /// могут использовать разные идентификаторы.
    /// </summary>
    public IReadOnlyList<PlayerEvent> PlayerEvents(string id, string name, int limit)
    {
        lock (_gate)
        {
            var events = new List<PlayerEvent>();
            var byName = !string.IsNullOrWhiteSpace(name);

            bool Matches(string eventId, string eventName) =>
                (!string.IsNullOrWhiteSpace(id) && eventId == id) ||
                (byName && string.Equals(eventName, name, StringComparison.OrdinalIgnoreCase));

            foreach (var kill in _kills)
            {
                if (Matches(kill.VictimId, kill.VictimName))
                {
                    var text = kill.IsSuicide
                        ? "покончил с собой"
                        : kill.IsEnvironment
                            ? "погиб (окружение)"
                            : $"убит: {kill.KillerName}" + (string.IsNullOrWhiteSpace(kill.Weapon) ? "" : $" ({kill.Weapon})");
                    events.Add(new PlayerEvent(kill.At, "death", text));
                }

                if (!kill.IsSuicide && !kill.IsEnvironment && Matches(kill.KillerId, kill.KillerName))
                {
                    events.Add(new PlayerEvent(kill.At, "kill",
                        $"убил: {kill.VictimName}" + (kill.Distance > 0 ? $" ({kill.Distance:0} м)" : "")));
                }
            }

            foreach (var chat in _chat)
            {
                if (Matches(chat.PlayerId, chat.PlayerName))
                {
                    events.Add(new PlayerEvent(chat.At, "chat", chat.Text));
                }
            }

            return events
                .OrderByDescending(e => e.At)
                .Take(Math.Clamp(limit, 1, 200))
                .ToArray();
        }
    }

    public IReadOnlyList<ChatEvent> Chat(int limit = 200) =>
        Snapshot(_chat, limit);

    public IReadOnlyList<Violation> Violations(int limit = 200) =>
        Snapshot(_violations, limit);

    private IReadOnlyList<T> Snapshot<T>(Queue<T> source, int limit)
    {
        lock (_gate)
        {
            var array = source.ToArray();
            return array.Length <= limit ? array : array[^limit..];
        }
    }

    public IReadOnlyList<PlayerRecord> Players()
    {
        lock (_gate)
        {
            return _players.Values
                .OrderByDescending(p => p.LastSeen)
                .ToArray();
        }
    }

    public PlayerRecord? Player(string id)
    {
        lock (_gate)
        {
            return _players.TryGetValue(id, out var record) ? record : null;
        }
    }

    public void SetNote(string id, string note, bool watch)
    {
        lock (_gate)
        {
            var record = EnsureRecord(id, "");
            record.Note = note ?? "";
            record.Watch = watch;
            _dirty = true;
        }
    }

    // ------------------------------------------------------------- хранение

    public bool ConsumeDirty()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return false;
            }

            _dirty = false;
            return true;
        }
    }

    public PersistedState Export(int retentionDays)
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-Math.Clamp(retentionDays, 1, 365));

            return new PersistedState
            {
                Players = _players.Values.ToArray(),
                Kills = _kills.Where(k => k.At >= cutoff).ToArray(),
                Violations = _violations.Where(v => v.At >= cutoff).ToArray(),
                Tracks = _tracks.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Where(p => p.At >= cutoff).TakeLast(MaxTrackPointsPerPlayer).ToArray()),
                Online = _online.Where(p => p.At >= cutoff).ToArray(),
                // Инвентарь храним целиком — он небольшой и нужен для оффлайн-профилей.
                Inventory = _inventory.ToDictionary(kv => kv.Key, kv => kv.Value),
                LastIngest = _lastIngest,
                Sequence = _sequence,
                ViolationId = _violationId
            };
        }
    }

    /// <summary>Идентификаторы-заглушки вида unknown_N (игрок без личности).</summary>
    private static bool IsPlaceholderId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.StartsWith("unknown", StringComparison.OrdinalIgnoreCase);

    public void Import(PersistedState state)
    {
        lock (_gate)
        {
            _players.Clear();
            foreach (var player in state.Players ?? [])
            {
                // Записи вида unknown_N копились, когда мод присылал игроков без
                // личности. Такие данные бесполезны — не переносим их.
                if (IsPlaceholderId(player.Id))
                {
                    continue;
                }

                _players[player.Id] = player;
            }

            _kills.Clear();
            foreach (var kill in state.Kills ?? [])
            {
                _kills.Enqueue(kill);
            }

            _violations.Clear();
            foreach (var violation in state.Violations ?? [])
            {
                _violations.Enqueue(violation);
            }

            _tracks.Clear();
            foreach (var (id, points) in state.Tracks ?? [])
            {
                _tracks[id] = [.. points];
            }

            _online.Clear();
            _online.AddRange(state.Online ?? []);

            _inventory.Clear();
            foreach (var (id, inventory) in state.Inventory ?? [])
            {
                _inventory[id] = inventory;
            }

            _lastIngest = DateTimeOffset.MinValue;
            _sequence = state.Sequence;
            _violationId = state.ViolationId;
        }
    }
}

/// <summary>Состояние трекера, которое переносится между запусками.</summary>
public sealed class PersistedState
{
    public PlayerRecord[] Players { get; set; } = [];
    public KillEvent[] Kills { get; set; } = [];
    public Violation[] Violations { get; set; } = [];
    public Dictionary<string, TrackPoint[]> Tracks { get; set; } = new();
    public Dictionary<string, PlayerInventory> Inventory { get; set; } = new();
    public OnlinePoint[] Online { get; set; } = [];
    public DateTimeOffset LastIngest { get; set; }
    public long Sequence { get; set; }
    public long ViolationId { get; set; }
}
