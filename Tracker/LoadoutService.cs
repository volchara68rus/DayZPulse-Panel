using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayZPulse.Core;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>
/// Пишет стартовые наборы и пресеты в файлы, которые читает мод:
/// <list type="bullet">
/// <item><c>spawngear.json</c> — наборы новых игроков (общий/мужской/женский);</item>
/// <item><c>presets.json</c> — пресеты для выдачи игрокам на время.</item>
/// </list>
/// </summary>
public sealed class LoadoutService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<LoadoutService> _logger;

    public LoadoutService(ILogger<LoadoutService> logger)
    {
        _logger = logger;
    }

    /// <summary>Каталог файлов мода: <c>profiles\DayZPulseCore</c>.</summary>
    public static string ConfigDirectory(ServerProfile profile) =>
        ModConfigWriter.ProfilesRoot(profile) is { } root
            ? Path.Combine(root, "DayZPulseCore")
            : Path.Combine(ServerPaths.WorkingDirectory(profile.Server), "profiles", "DayZPulseCore");

    /// <summary>Записывает стартовые наборы в формате, который понимает мод.</summary>
    public void WriteSpawnGear(ServerProfile profile)
    {
        try
        {
            var path = Path.Combine(ConfigDirectory(profile), "spawngear.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var payload = BuildSpawnGear(profile.SpawnKits, profile.GrantPresets);
            File.WriteAllText(path, JsonSerializer.Serialize(payload, Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось записать spawngear.json.", profile.Name);
        }
    }

    /// <summary>
    /// Строит JSON в формате мода: наборы по полу плюс выданные пресеты.
    /// <c>assign</c> — список наборов, <c>exclude</c> — пусто.
    /// </summary>
    private static object BuildSpawnGear(SpawnKits kits, List<GrantPreset> presets)
    {
        var assign = new List<object>();

        // Общий набор и наборы по полу: id — маркер, пол разбирает мод.
        if (kits.Common.Items.Count > 0)
        {
            assign.Add(SpawnAssign("all", kits.Common, "Общий"));
        }

        if (kits.Male is { Items.Count: > 0 })
        {
            assign.Add(SpawnAssign("male", kits.Male, "Мужской"));
        }

        if (kits.Female is { Items.Count: > 0 })
        {
            assign.Add(SpawnAssign("female", kits.Female, "Женский"));
        }

        // Активные пресеты (не истёкшие) — по игрокам.
        var now = DateTimeOffset.Now;
        foreach (var preset in presets.Where(p => p.Enabled).Where(p => p.ExpiresAt is null || p.ExpiresAt > now))
        {
            foreach (var player in preset.Players.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                assign.Add(SpawnAssign(player, new Loadout { Items = preset.Items }, preset.Name));
            }
        }

        return new { assign, exclude = Array.Empty<string>() };
    }

    /// <summary>
    /// Приводит имя слота к имени, которое понимает движок (Slot_&lt;name&gt;).
    /// Ранее в UI головной слот назывался "Head", а вместо "Armband" был
    /// несуществующий "Neck" — такие наборы нужно починить на лету.
    /// </summary>
    private static string NormalizeSlot(string slot)
    {
        var s = slot?.Trim() ?? "";

        return s switch
        {
            "" => "",
            "Head" => "Headgear",
            "Neck" => "Armband",
            _ => s
        };
    }

    private static object SpawnAssign(string id, Loadout loadout, string label)
    {
        // Мод различает слоты экипировки (attachmentSlotItemSets) и карго (discreteUnsortedItemSets).
        var slots = loadout.Items
            .Where(i => i.Target == LoadoutTarget.Slot && !string.IsNullOrWhiteSpace(i.Slot))
            .Select(i => new
            {
                slotName = NormalizeSlot(i.Slot),
                discreteItemSets = new[]
                {
                    new
                    {
                        itemType = i.ClassName,
                        spawnWeight = 1,
                        quickBarSlot = i.Target == LoadoutTarget.Hands ? 0 : -1,
                        attributes = new
                        {
                            healthMin = i.Health < 0 ? 1 : i.Health,
                            healthMax = i.Health < 0 ? 1 : i.Health,
                            quantityMin = i.Quantity > 1 ? 1.0 : 0.0,
                            quantityMax = i.Quantity > 1 ? 1.0 : 0.0
                        }
                    }
                }
            })
            .ToArray();

        var cargo = loadout.Items
            .Where(i => i.Target != LoadoutTarget.Slot)
            .SelectMany(i => Enumerable.Range(0, Math.Max(1, i.Quantity)).Select(_ => new
            {
                slotName = "",
                discreteItemSets = new[]
                {
                    new
                    {
                        itemType = i.ClassName,
                        spawnWeight = 1,
                        quickBarSlot = i.Target == LoadoutTarget.Hands ? 0 : -1,
                        attributes = new
                        {
                            healthMin = i.Health < 0 ? 1 : i.Health,
                            healthMax = i.Health < 0 ? 1 : i.Health,
                            quantityMin = 0.0,
                            quantityMax = 0.0
                        }
                    }
                }
            }))
            .ToArray();

        return new
        {
            id,
            label,
            attachmentSlotItemSets = slots,
            discreteUnsortedItemSets = cargo
        };
    }

    /// <summary>Записывает отдельный файл пресетов (для ручного просмотра и модов).</summary>
    public void WritePresets(ServerProfile profile)
    {
        try
        {
            var path = Path.Combine(ConfigDirectory(profile), "presets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(profile.GrantPresets, Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Server}] Не удалось записать presets.json.", profile.Name);
        }
    }

    /// <summary>Перезаписывает файлы после изменения наборов или пресетов.</summary>
    public void SyncAll(ServerProfile profile)
    {
        WriteSpawnGear(profile);
        WritePresets(profile);
    }
}
