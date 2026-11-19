namespace DayZPulse.Tracker;

/// <summary>
/// Событие игрока из журнала мода (events.jsonl), разложенное по полям.
/// Хранится в базе SQLite (таблица events) и показывается в интерфейсе.
/// </summary>
public sealed record PlayerEventRow(
    DateTimeOffset At,
    string Type,
    string PlayerId,
    string PlayerName,
    string KillerId,
    string KillerName,
    string Class,
    string Slot,
    string Weapon,
    string Detail,
    double X,
    double Z,
    string Raw);
