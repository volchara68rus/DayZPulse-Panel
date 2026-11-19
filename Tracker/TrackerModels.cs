namespace DayZPulse.Tracker;

/// <summary>Живое состояние игрока, полученное от мода DayZPulse.</summary>
public sealed class LivePlayer
{
    public string Id { get; set; } = "";
    public string PlainId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Alive { get; set; }
    public double Health { get; set; }
    public double MaxHealth { get; set; } = 100;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string? StatsJson { get; set; }
    public List<string> Mods { get; set; } = [];

    /// <summary>Средний пинг игрока, мс (0 — неизвестно).</summary>
    public int Ping { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Состояние сервера из снимка мода.</summary>
public sealed class TrackedServerState
{
    public string Id { get; set; } = "";
    public long Tick { get; set; }
    public int Players { get; set; }
    public int Alive { get; set; }
    public int Vehicles { get; set; }
    public int Infected { get; set; }
    public int WorldObjects { get; set; }

    /// <summary>Средний пинг игроков, мс (0 — нет данных).</summary>
    public int PingAvg { get; set; }

    public TrackedVehicle[] VehicleList { get; set; } = [];
    public TrackedWorldObject[] WorldObjectList { get; set; } = [];
}

public sealed class TrackedVehicle
{
    public string Class { get; set; } = "";
    public double X { get; set; }
    public double Z { get; set; }
    public double Health { get; set; }
    public double Fuel { get; set; }
    public bool HasBattery { get; set; }
    public bool HasRadiator { get; set; }
    public int DoorsMissing { get; set; }
    public int DoorsOpen { get; set; }
    public string Crew { get; set; } = "";
}

public sealed class TrackedWorldObject
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string Class { get; set; } = "";
    public double X { get; set; }
    public double Z { get; set; }
}

/// <summary>Одна точка трека игрока.</summary>
public sealed record TrackPoint(DateTimeOffset At, double X, double Z);

/// <summary>Событие игрока для карточки: смерть, убийство или чат.</summary>
public sealed record PlayerEvent(DateTimeOffset At, string Kind, string Text);

/// <summary>Точка истории онлайна: сколько игроков было в этот момент.</summary>
public sealed record OnlinePoint(DateTimeOffset At, int Players, int Alive);

/// <summary>Событие убийства из админ-лога.</summary>
public sealed class KillEvent
{
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string KillerId { get; set; } = "";
    public string KillerName { get; set; } = "";
    public string VictimId { get; set; } = "";
    public string VictimName { get; set; } = "";
    public string Weapon { get; set; } = "";
    public double Distance { get; set; }
    public bool IsSuicide { get; set; }
    public bool IsEnvironment { get; set; }
}

/// <summary>Сообщение чата из админ-лога.</summary>
public sealed class ChatEvent
{
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Снимок инвентаря игрока, пришедший от мода. Храним сырой JSON, чтобы
/// панель рисовала его без жёсткой привязки к раскладке слотов.
/// </summary>
public sealed class PlayerInventory
{
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTimeOffset At { get; set; }
}

/// <summary>Нарушение, присланное аддоном AntiCheat.</summary>
public sealed class Violation
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Type { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public double X { get; set; }
    public double Z { get; set; }
    public int Points { get; set; }
    public int TotalPoints { get; set; }
    public string Action { get; set; } = "log";
    public string Details { get; set; } = "";
}

/// <summary>Агрегированная запись игрока, которая живёт между сессиями.</summary>
public sealed class PlayerRecord
{
    public string Id { get; set; } = "";
    public string PlainId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int Sessions { get; set; }
    public double PlaytimeSeconds { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public string Note { get; set; } = "";
    public bool Watch { get; set; }
    public long TotalViolationPoints { get; set; }
}
