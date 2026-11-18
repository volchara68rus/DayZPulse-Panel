using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DayZPulse.Tracker;

/// <summary>
/// Локальная база данных трекера (SQLite): игроки, киллы, чат, нарушения,
/// треки, история онлайна и снимки инвентаря. Заменяет прежний JSON-файл
/// <c>tracker.json</c> — данные хранятся построчно и доступны через SQL.
///
/// Запись инкрементальная: при каждом сохранении добавляются только новые
/// строки (киллы по seq, треки по времени и т.д.), а не перезаписывается всё.
/// Файл базы лежит в папке данных сервера, поэтому удаление сервера убирает
/// и его базу.
/// </summary>
public sealed class TrackerDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    // Время храним строкой UTC: такой формат сортируется лексикографически
    // как хронологический и не зависит от часового пояса и культуры.
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private readonly string _path;

    public TrackerDatabase(string path)
    {
        _path = path;
    }

    /// <summary>Путь к файлу базы (для журнала и интерфейса).</summary>
    public string Path => _path;

    /// <summary>Открывает соединение и создаёт схему при первом обращении.</summary>
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // WAL ускоряет запись и не мешает чтению.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        EnsureSchema(connection);
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS players (
                id               TEXT PRIMARY KEY,
                plain_id         TEXT NOT NULL DEFAULT '',
                name             TEXT NOT NULL DEFAULT '',
                aliases          TEXT NOT NULL DEFAULT '[]',
                first_seen       TEXT NOT NULL,
                last_seen        TEXT NOT NULL,
                sessions         INTEGER NOT NULL DEFAULT 0,
                playtime_seconds REAL NOT NULL DEFAULT 0,
                kills            INTEGER NOT NULL DEFAULT 0,
                deaths           INTEGER NOT NULL DEFAULT 0,
                note             TEXT NOT NULL DEFAULT '',
                watch            INTEGER NOT NULL DEFAULT 0,
                violation_points INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS kills (
                seq            INTEGER PRIMARY KEY,
                at             TEXT NOT NULL,
                killer_id      TEXT NOT NULL DEFAULT '',
                killer_name    TEXT NOT NULL DEFAULT '',
                victim_id      TEXT NOT NULL DEFAULT '',
                victim_name    TEXT NOT NULL DEFAULT '',
                weapon         TEXT NOT NULL DEFAULT '',
                distance       REAL NOT NULL DEFAULT 0,
                is_suicide     INTEGER NOT NULL DEFAULT 0,
                is_environment INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_kills_at ON kills(at);

            CREATE TABLE IF NOT EXISTS violations (
                id           INTEGER PRIMARY KEY,
                at           TEXT NOT NULL,
                type         TEXT NOT NULL DEFAULT '',
                player_id    TEXT NOT NULL DEFAULT '',
                player_name  TEXT NOT NULL DEFAULT '',
                x            REAL NOT NULL DEFAULT 0,
                z            REAL NOT NULL DEFAULT 0,
                points       INTEGER NOT NULL DEFAULT 0,
                total_points INTEGER NOT NULL DEFAULT 0,
                action       TEXT NOT NULL DEFAULT '',
                details      TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_violations_at ON violations(at);

            CREATE TABLE IF NOT EXISTS tracks (
                player_id TEXT NOT NULL,
                at        TEXT NOT NULL,
                x         REAL NOT NULL,
                z         REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tracks_player ON tracks(player_id, at);

            CREATE TABLE IF NOT EXISTS online (
                at      TEXT NOT NULL,
                players INTEGER NOT NULL,
                alive   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_online_at ON online(at);

            CREATE TABLE IF NOT EXISTS inventory (
                player_id   TEXT PRIMARY KEY,
                player_name TEXT NOT NULL DEFAULT '',
                json        TEXT NOT NULL DEFAULT '{}',
                at          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            """;

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Синхронизирует базу с состоянием трекера: обновляет игроков и снимки
    /// инвентаря, добавляет только новые киллы, нарушения, точки треков и
    /// онлайна. Старые записи (за пределами срока хранения) удаляются.
    /// </summary>
    public void Sync(PersistedState state, int retentionDays)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        var cutoff = FormatTime(DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 365)));

        UpsertPlayers(connection, tx, state.Players ?? []);
        UpsertInventory(connection, tx, state.Inventory ?? new());
        InsertNewKills(connection, tx, state.Kills ?? []);
        InsertNewViolations(connection, tx, state.Violations ?? []);
        InsertNewTracks(connection, tx, state.Tracks ?? new());
        InsertNewOnline(connection, tx, state.Online ?? []);

        // Чистим то, что вышло за срок хранения.
        Prune(connection, tx, cutoff);

        SetMeta(connection, tx, "last_ingest", FormatTime(state.LastIngest));
        SetMeta(connection, tx, "sequence", state.Sequence.ToString(CultureInfo.InvariantCulture));
        SetMeta(connection, tx, "violation_id", state.ViolationId.ToString(CultureInfo.InvariantCulture));
        SetMeta(connection, tx, "schema_version", "1");

        tx.Commit();

        // Переносим WAL в основной файл: так свежие данные видны сторонним
        // читателям (бэкап, диагностика) и журнал не разрастается.
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
    }

    /// <summary>Полная перезапись (используется при переносе из старого JSON).</summary>
    public void Replace(PersistedState state, int retentionDays)
    {
        using (var connection = Open())
        {
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM players; DELETE FROM kills; DELETE FROM violations; DELETE FROM tracks; DELETE FROM online; DELETE FROM inventory;";
            clear.ExecuteNonQuery();
        }

        Sync(state, retentionDays);
    }

    // -------------------------------------------------------------- запись

    private static void UpsertPlayers(SqliteConnection connection, SqliteTransaction tx, PlayerRecord[] players)
    {
        if (players.Length == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO players
                (id, plain_id, name, aliases, first_seen, last_seen, sessions,
                 playtime_seconds, kills, deaths, note, watch, violation_points)
            VALUES
                ($id, $plain, $name, $aliases, $first, $last, $sessions,
                 $playtime, $kills, $deaths, $note, $watch, $points)
            ON CONFLICT(id) DO UPDATE SET
                plain_id = $plain, name = $name, aliases = $aliases,
                last_seen = $last, sessions = $sessions, playtime_seconds = $playtime,
                kills = $kills, deaths = $deaths, note = $note, watch = $watch,
                violation_points = $points;
            """;

        var pId = command.Parameters.Add("$id", SqliteType.Text);
        var pPlain = command.Parameters.Add("$plain", SqliteType.Text);
        var pName = command.Parameters.Add("$name", SqliteType.Text);
        var pAliases = command.Parameters.Add("$aliases", SqliteType.Text);
        var pFirst = command.Parameters.Add("$first", SqliteType.Text);
        var pLast = command.Parameters.Add("$last", SqliteType.Text);
        var pSessions = command.Parameters.Add("$sessions", SqliteType.Integer);
        var pPlaytime = command.Parameters.Add("$playtime", SqliteType.Real);
        var pKills = command.Parameters.Add("$kills", SqliteType.Integer);
        var pDeaths = command.Parameters.Add("$deaths", SqliteType.Integer);
        var pNote = command.Parameters.Add("$note", SqliteType.Text);
        var pWatch = command.Parameters.Add("$watch", SqliteType.Integer);
        var pPoints = command.Parameters.Add("$points", SqliteType.Integer);

        foreach (var player in players)
        {
            pId.Value = player.Id ?? "";
            pPlain.Value = player.PlainId ?? "";
            pName.Value = player.Name ?? "";
            pAliases.Value = JsonSerializer.Serialize(player.Aliases ?? [], JsonOptions);
            pFirst.Value = FormatTime(player.FirstSeen);
            pLast.Value = FormatTime(player.LastSeen);
            pSessions.Value = player.Sessions;
            pPlaytime.Value = player.PlaytimeSeconds;
            pKills.Value = player.Kills;
            pDeaths.Value = player.Deaths;
            pNote.Value = player.Note ?? "";
            pWatch.Value = player.Watch ? 1 : 0;
            pPoints.Value = player.TotalViolationPoints;
            command.ExecuteNonQuery();
        }
    }

    private static void UpsertInventory(SqliteConnection connection, SqliteTransaction tx, Dictionary<string, PlayerInventory> inventory)
    {
        if (inventory.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO inventory (player_id, player_name, json, at)
            VALUES ($pid, $pn, $json, $at)
            ON CONFLICT(player_id) DO UPDATE SET
                player_name = $pn, json = $json, at = $at;
            """;

        var pid = command.Parameters.Add("$pid", SqliteType.Text);
        var pn = command.Parameters.Add("$pn", SqliteType.Text);
        var json = command.Parameters.Add("$json", SqliteType.Text);
        var at = command.Parameters.Add("$at", SqliteType.Text);

        foreach (var (playerId, snapshot) in inventory)
        {
            pid.Value = playerId;
            pn.Value = snapshot.PlayerName ?? "";
            json.Value = string.IsNullOrWhiteSpace(snapshot.Json) ? "{}" : snapshot.Json;
            at.Value = FormatTime(snapshot.At);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertNewKills(SqliteConnection connection, SqliteTransaction tx, KillEvent[] kills)
    {
        if (kills.Length == 0)
        {
            return;
        }

        var max = QueryLong(connection, tx, "SELECT COALESCE(MAX(seq), 0) FROM kills;");

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT OR IGNORE INTO kills
                (seq, at, killer_id, killer_name, victim_id, victim_name,
                 weapon, distance, is_suicide, is_environment)
            VALUES
                ($seq, $at, $kid, $kn, $vid, $vn, $weapon, $dist, $suicide, $env);
            """;

        var seq = command.Parameters.Add("$seq", SqliteType.Integer);
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var kid = command.Parameters.Add("$kid", SqliteType.Text);
        var kn = command.Parameters.Add("$kn", SqliteType.Text);
        var vid = command.Parameters.Add("$vid", SqliteType.Text);
        var vn = command.Parameters.Add("$vn", SqliteType.Text);
        var weapon = command.Parameters.Add("$weapon", SqliteType.Text);
        var dist = command.Parameters.Add("$dist", SqliteType.Real);
        var suicide = command.Parameters.Add("$suicide", SqliteType.Integer);
        var env = command.Parameters.Add("$env", SqliteType.Integer);

        foreach (var kill in kills)
        {
            if (kill.Seq <= max)
            {
                continue;
            }

            seq.Value = kill.Seq;
            at.Value = FormatTime(kill.At);
            kid.Value = kill.KillerId ?? "";
            kn.Value = kill.KillerName ?? "";
            vid.Value = kill.VictimId ?? "";
            vn.Value = kill.VictimName ?? "";
            weapon.Value = kill.Weapon ?? "";
            dist.Value = kill.Distance;
            suicide.Value = kill.IsSuicide ? 1 : 0;
            env.Value = kill.IsEnvironment ? 1 : 0;
            command.ExecuteNonQuery();
        }
    }

    private static void InsertNewViolations(SqliteConnection connection, SqliteTransaction tx, Violation[] violations)
    {
        if (violations.Length == 0)
        {
            return;
        }

        var max = QueryLong(connection, tx, "SELECT COALESCE(MAX(id), 0) FROM violations;");

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT OR IGNORE INTO violations
                (id, at, type, player_id, player_name, x, z, points, total_points, action, details)
            VALUES
                ($id, $at, $type, $pid, $pn, $x, $z, $points, $total, $action, $details);
            """;

        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var type = command.Parameters.Add("$type", SqliteType.Text);
        var pid = command.Parameters.Add("$pid", SqliteType.Text);
        var pn = command.Parameters.Add("$pn", SqliteType.Text);
        var x = command.Parameters.Add("$x", SqliteType.Real);
        var z = command.Parameters.Add("$z", SqliteType.Real);
        var points = command.Parameters.Add("$points", SqliteType.Integer);
        var total = command.Parameters.Add("$total", SqliteType.Integer);
        var action = command.Parameters.Add("$action", SqliteType.Text);
        var details = command.Parameters.Add("$details", SqliteType.Text);

        foreach (var violation in violations)
        {
            if (violation.Id <= max)
            {
                continue;
            }

            id.Value = violation.Id;
            at.Value = FormatTime(violation.At);
            type.Value = violation.Type ?? "";
            pid.Value = violation.PlayerId ?? "";
            pn.Value = violation.PlayerName ?? "";
            x.Value = violation.X;
            z.Value = violation.Z;
            points.Value = violation.Points;
            total.Value = violation.TotalPoints;
            action.Value = violation.Action ?? "";
            details.Value = violation.Details ?? "";
            command.ExecuteNonQuery();
        }
    }

    private static void InsertNewTracks(SqliteConnection connection, SqliteTransaction tx, Dictionary<string, TrackPoint[]> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        // Предельное время уже сохранённой точки по каждому игроку.
        var marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT player_id, MAX(at) FROM tracks GROUP BY player_id;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                marks[reader.GetString(0)] = reader.GetString(1);
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO tracks (player_id, at, x, z) VALUES ($pid, $at, $x, $z);";
        var pid = command.Parameters.Add("$pid", SqliteType.Text);
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var x = command.Parameters.Add("$x", SqliteType.Real);
        var z = command.Parameters.Add("$z", SqliteType.Real);

        foreach (var (playerId, points) in tracks)
        {
            marks.TryGetValue(playerId, out var mark);

            foreach (var point in points)
            {
                var time = FormatTime(point.At);
                if (mark is not null && string.CompareOrdinal(time, mark) <= 0)
                {
                    continue;
                }

                pid.Value = playerId;
                at.Value = time;
                x.Value = point.X;
                z.Value = point.Z;
                command.ExecuteNonQuery();
            }
        }
    }

    private static void InsertNewOnline(SqliteConnection connection, SqliteTransaction tx, OnlinePoint[] points)
    {
        if (points.Length == 0)
        {
            return;
        }

        var mark = QueryString(connection, tx, "SELECT COALESCE(MAX(at), '') FROM online;");

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO online (at, players, alive) VALUES ($at, $players, $alive);";
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var players = command.Parameters.Add("$players", SqliteType.Integer);
        var alive = command.Parameters.Add("$alive", SqliteType.Integer);

        foreach (var point in points)
        {
            var time = FormatTime(point.At);
            if (mark.Length > 0 && string.CompareOrdinal(time, mark) <= 0)
            {
                continue;
            }

            at.Value = time;
            players.Value = point.Players;
            alive.Value = point.Alive;
            command.ExecuteNonQuery();
        }
    }

    private static void Prune(SqliteConnection connection, SqliteTransaction tx, string cutoff)
    {
        foreach (var table in new[] { "kills", "violations", "tracks", "online" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"DELETE FROM {table} WHERE at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.ExecuteNonQuery();
        }
    }

    // --------------------------------------------------------------- чтение

    /// <summary>Читает состояние из базы. Пустое (без игроков) — null.</summary>
    public PersistedState? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        using var connection = Open();
        var state = new PersistedState();

        var players = new List<PlayerRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, plain_id, name, aliases, first_seen, last_seen, sessions,
                       playtime_seconds, kills, deaths, note, watch, violation_points
                FROM players;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                List<string> aliases;
                try
                {
                    aliases = JsonSerializer.Deserialize<List<string>>(reader.GetString(3), JsonOptions) ?? [];
                }
                catch
                {
                    aliases = [];
                }

                players.Add(new PlayerRecord
                {
                    Id = reader.GetString(0),
                    PlainId = reader.GetString(1),
                    Name = reader.GetString(2),
                    Aliases = aliases,
                    FirstSeen = ParseTime(reader.GetString(4)),
                    LastSeen = ParseTime(reader.GetString(5)),
                    Sessions = reader.GetInt32(6),
                    PlaytimeSeconds = reader.GetDouble(7),
                    Kills = reader.GetInt32(8),
                    Deaths = reader.GetInt32(9),
                    Note = reader.GetString(10),
                    Watch = reader.GetInt32(11) != 0,
                    TotalViolationPoints = reader.GetInt32(12)
                });
            }
        }

        var kills = new List<KillEvent>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT seq, at, killer_id, killer_name, victim_id, victim_name,
                       weapon, distance, is_suicide, is_environment
                FROM kills ORDER BY seq;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                kills.Add(new KillEvent
                {
                    Seq = reader.GetInt64(0),
                    At = ParseTime(reader.GetString(1)),
                    KillerId = reader.GetString(2),
                    KillerName = reader.GetString(3),
                    VictimId = reader.GetString(4),
                    VictimName = reader.GetString(5),
                    Weapon = reader.GetString(6),
                    Distance = reader.GetDouble(7),
                    IsSuicide = reader.GetInt32(8) != 0,
                    IsEnvironment = reader.GetInt32(9) != 0
                });
            }
        }

        var violations = new List<Violation>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, at, type, player_id, player_name, x, z, points, total_points, action, details
                FROM violations ORDER BY id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                violations.Add(new Violation
                {
                    Id = reader.GetInt64(0),
                    At = ParseTime(reader.GetString(1)),
                    Type = reader.GetString(2),
                    PlayerId = reader.GetString(3),
                    PlayerName = reader.GetString(4),
                    X = reader.GetDouble(5),
                    Z = reader.GetDouble(6),
                    Points = reader.GetInt32(7),
                    TotalPoints = reader.GetInt32(8),
                    Action = reader.GetString(9),
                    Details = reader.GetString(10)
                });
            }
        }

        var tracks = new Dictionary<string, List<TrackPoint>>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT player_id, at, x, z FROM tracks ORDER BY player_id, at;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var playerId = reader.GetString(0);
                if (!tracks.TryGetValue(playerId, out var points))
                {
                    points = [];
                    tracks[playerId] = points;
                }

                points.Add(new TrackPoint(ParseTime(reader.GetString(1)), reader.GetDouble(2), reader.GetDouble(3)));
            }
        }

        var online = new List<OnlinePoint>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT at, players, alive FROM online ORDER BY at;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                online.Add(new OnlinePoint(ParseTime(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        var inventory = new Dictionary<string, PlayerInventory>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT player_id, player_name, json, at FROM inventory;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var playerId = reader.GetString(0);
                inventory[playerId] = new PlayerInventory
                {
                    PlayerId = playerId,
                    PlayerName = reader.GetString(1),
                    Json = reader.GetString(2),
                    At = ParseTime(reader.GetString(3))
                };
            }
        }

        state.Players = [.. players];
        state.Kills = [.. kills];
        state.Violations = [.. violations];
        state.Tracks = tracks.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        state.Online = [.. online];
        state.Inventory = inventory;
        state.LastIngest = ParseTime(GetMeta(connection, "last_ingest"));
        state.Sequence = ParseLong(GetMeta(connection, "sequence"));
        state.ViolationId = ParseLong(GetMeta(connection, "violation_id"));

        // Пустая база (только что создана) — считаем, что данных нет.
        return players.Count == 0 && kills.Count == 0 && violations.Count == 0 ? null : state;
    }

    // ------------------------------------------------------------- служебное

    private static long QueryLong(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static string QueryString(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command.ExecuteScalar() as string ?? "";
    }

    private static void SetMeta(SqliteConnection connection, SqliteTransaction tx, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value ?? "");
        command.ExecuteNonQuery();
    }

    private static string? GetMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.MinValue;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
