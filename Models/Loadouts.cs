namespace DayZPulse.Models;

/// <summary>Куда попадает предмет набора: слот экипировки или карго/руки.</summary>
public enum LoadoutTarget
{
    /// <summary>Надеть в слот экипировки (Head, Body, Back, …).</summary>
    Slot,

    /// <summary>Положить в руки.</summary>
    Hands,

    /// <summary>Положить в карго (рюкзак/карманы).</summary>
    Cargo
}

/// <summary>Один предмет набора: класс, количество, состояние, слот и обвес.</summary>
public sealed class LoadoutItem
{
    /// <summary>Имя класса DayZ (например AK101).</summary>
    public string ClassName { get; set; } = "";

    /// <summary>Количество (1 для одиночных предметов).</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Состояние 0..1 (1 — целое). -1 — заводское.</summary>
    public double Health { get; set; } = 1;

    /// <summary>Слот экипировки, если предмет надевается.</summary>
    public string Slot { get; set; } = "";

    /// <summary>Куда положить: слот, руки или карго.</summary>
    public LoadoutTarget Target { get; set; } = LoadoutTarget.Cargo;

    /// <summary>Список классов-обвесов (магазин, прицел, глушитель).</summary>
    public List<string> Attachments { get; set; } = [];
}

/// <summary>
/// Набор предметов: стартовый набор игрока или пресет для выдачи.
/// Хранится в JSON и пишется моду.
/// </summary>
public sealed class Loadout
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<LoadoutItem> Items { get; set; } = [];

    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Стартовые наборы сервера: общий и по полу.</summary>
public sealed class SpawnKits
{
    /// <summary>Набор для всех новых игроков.</summary>
    public Loadout Common { get; set; } = new() { Name = "Общий" };

    /// <summary>Набор для мужских персонажей (переопределяет общий).</summary>
    public Loadout? Male { get; set; }

    /// <summary>Набор для женских персонажей (переопределяет общий).</summary>
    public Loadout? Female { get; set; }
}

/// <summary>Пресет для выдачи конкретным игрокам на время.</summary>
public sealed class GrantPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<LoadoutItem> Items { get; set; } = [];

    /// <summary>SteamID64/GetId игроков, кому выдан пресет. Пусто — всем.</summary>
    public List<string> Players { get; set; } = [];

    /// <summary>Действует до этого времени. null — бессрочно.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
