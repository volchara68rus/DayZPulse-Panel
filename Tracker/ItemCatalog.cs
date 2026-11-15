using System.Text.RegularExpressions;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>Один предмет из types.xml: класс и его категория DayZ.</summary>
public sealed record CatalogItem(string ClassName, string Category, string Label, string Icon);

/// <summary>
/// Каталог предметов сервера из <c>db/types.xml</c>. Читает классы и их
/// категории (<c>weapons</c>, <c>clothes</c>, <c>food</c>, …), чтобы панель
/// могла предложить только совместимые с выбранным слотом предметы.
/// </summary>
public sealed class ItemCatalog
{
    private readonly ILogger<ItemCatalog> _logger;

    // Кэш по пути к types.xml: файл большой, читаем один раз.
    private readonly object _gate = new();
    private readonly Dictionary<string, (List<CatalogItem> Items, DateTimeOffset At)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    public ItemCatalog(ILogger<ItemCatalog> logger)
    {
        _logger = logger;
    }

    /// <summary>Каталог предметов сервера; пустой, если types.xml не найден.</summary>
    public IReadOnlyList<CatalogItem> ForServer(ServerSettings server, string profilesDirectory)
    {
        var path = ResolveTypesXml(server, profilesDirectory);
        if (path is null)
        {
            return [];
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
            {
                return cached.Items;
            }
        }

        var items = Parse(path);

        lock (_gate)
        {
            _cache[path] = (items, DateTimeOffset.UtcNow);
        }

        return items;
    }

    /// <summary>Ищет db/types.xml в миссии сервера.</summary>
    private static string? ResolveTypesXml(ServerSettings server, string profilesDirectory)
    {
        try
        {
            var working = Core.ServerPaths.WorkingDirectory(server);

            // Прямой кандидат: db/types.xml рядом с рабочим каталогом.
            var direct = Path.Combine(working, "db", "types.xml");
            if (File.Exists(direct))
            {
                return direct;
            }

            // Иначе — первая миссия в mpmissions со своим db/types.xml.
            var missions = Path.Combine(working, "mpmissions");
            if (Directory.Exists(missions))
            {
                foreach (var mission in Directory.EnumerateDirectories(missions))
                {
                    var candidate = Path.Combine(mission, "db", "types.xml");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
            // Каталоги могут быть недоступны.
        }

        return null;
    }

    /// <summary>
    /// Разбирает types.xml: имя класса и категорию. Категорию приводим к
    /// нашим слотам/группам, чтобы UI фильтровал совместимость.
    /// </summary>
    private List<CatalogItem> Parse(string path)
    {
        var result = new List<CatalogItem>();

        try
        {
            var text = File.ReadAllText(path);

            foreach (Match match in TypePattern().Matches(text))
            {
                var className = match.Groups["name"].Value;
                var category = match.Groups["cat"].Success ? match.Groups["cat"].Value : "";

                var (group, label, icon) = Classify(className, category);
                result.Add(new CatalogItem(className, group, label, icon));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать каталог предметов {Path}", path);
        }

        return result
            .GroupBy(i => i.ClassName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(i => i.ClassName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Определяет группу предмета. Сначала по имени класса (точнее), затем по
    /// категории из types.xml. Группа совпадает с ролью слота экипировки.
    /// </summary>
    public static (string Group, string Label, string Icon) Classify(string cls, string category)
    {
        var c = cls ?? "";
        var cat = (category ?? "").ToLowerInvariant();

        // --- точные классы/префиксы: важно для слотов экипировки ---
        if (Regex.IsMatch(c, @"(Helmet|GorkaHelmet|BallisticHelmet|MotoHelmet|TankerHelmet|ConstructionHelmet)")) return ("Head", "Голова", "🪖");
        if (Regex.IsMatch(c, @"(Balaclava|Mask_|SurgicalMask|GasMask|Respirator|NBCMask)")) return ("Mask", "Маска", "😷");
        if (Regex.IsMatch(c, @"(Glasses|Goggles|Eyewear|AviatorGlasses|Sunglasses)")) return ("Eyewear", "Очки", "🕶");
        if (Regex.IsMatch(c, @"(Backpack|HuntingBag|AssaultBag|CoyoteBag|TortillaBag|TaloonBag|DryBag|Bergen)")) return ("Back", "Рюкзак", "🎒");
        if (Regex.IsMatch(c, @"(Vest|PlateCarrier|PressVest|SmershVest|ChestHolster|Chestplate)")) return ("Vest", "Жилет", "🦺");
        if (Regex.IsMatch(c, @"(Jacket|Sweater|Hoodie|Shirt|GorkaE|TTSKO|TacticalShirt|TrackSuitJacket|Raincoat)")) return ("Body", "Торс", "👕");
        if (Regex.IsMatch(c, @"(Pants|Trousers|Shorts|GorkaPants|TTSKOPants|TrackSuitPants|CargoPants)")) return ("Legs", "Штаны", "👖");
        if (Regex.IsMatch(c, @"(Boots|Shoes|Sneakers|Wellies|NBCBoots|CombatBoots|HikingBoots)")) return ("Feet", "Обувь", "🥾");
        if (Regex.IsMatch(c, @"(Gloves|WoolGloves|TacticalGloves|SurgicalGloves)")) return ("Gloves", "Перчатки", "🧤");
        if (Regex.IsMatch(c, @"(Belt_|_Belt|HipPack|Holster)")) return ("Hips", "Пояс", "🧵");

        // --- предметы (не экипировка) по категории и имени ---
        // Патроны и магазины — раньше «еды», иначе AmmoBox ловится на «Box».
        if (Regex.IsMatch(c, @"(AmmoBox|Ammo_|_Mag$|Mag_|_Ammo$|Rounds|Rnd$)")) return ("Ammo", "Патроны", "🎯");
        if (Regex.IsMatch(c, @"^(Animal_|Animal)") || Regex.IsMatch(c, @"(Wolf|Bear|Deer|Boar|Cow|Goat|Sheep|Hen|Rooster|Fox|Rabbit|Hare|Reindeer)")) return ("Animal", "Животные", "🐾");
        if (Regex.IsMatch(c, @"(Boat_|CivilianSedan|OffroadHatchback|Hatchback|Truck_|V3S|Sedan_|Bus_|Tractor|Vehicle|Car_)")) return ("VehicleObj", "Транспорт", "🚗");
        if (Regex.IsMatch(c, @"(Doors_|Hood|Trunk|Wheel|Tire|Battery|Radiator|SparkPlug|CarBattery|TruckBattery|TireRepairKit|Canister|CarRadiator)")) return ("Vehicle", "Запчасти авто", "🔧");
        if (Regex.IsMatch(c, @"(Grenade|Mine|Explosive|Claymore|FlashGrenade|SmokeGrenade|RGD5|M67|LandMine)")) return ("Explosive", "Взрывчатка", "💥");
        if (Regex.IsMatch(c, @"(Optic|Scope|Suppressor|Buttstock|Handguard|Bipod|_Muzzle|_Stock|_Barrel)")) return ("WeaponParts", "Обвес оружия", "🔩");

        if (Regex.IsMatch(c, @"(Bandage|Rag|SalineBag|BloodBag|Epinephrine|Morphine|Tetracycline|Painkiller|Charcoal|Vitamin|FirstAidKit|Syringe|IVStartKit|Defibrillator|Splint|Thermometer|BloodTestKit)")) return ("Medical", "Медицина", "💊");
        if (Regex.IsMatch(c, @"(Can$|Can_|_Can|Cereal|Powder|Rice|Sardines|Tuna|Peaches|Spaghetti|Beans|Meat|Apple|Pear|Plum|Banana|Potato|Tomato|Pepper|Zucchini|Pumpkin|Fish|Mushroom|Berries|Chocolate|Candy|PowderedMilk|Honey|WaterBottle|Canteen|SodaCan|Vodka|Beer|Wine|JerryCan|Achar|Carp|Bitterroot|Bunch)")) return ("Food", "Еда и вода", "🥫");

        if (Regex.IsMatch(c, @"(AK101|AK74|AKM|M4A1|M16A2|Mosin|SKS|Winchester|Izh43|MP5|UMP|Scorpion|CZ|Glock|Makarov|Colt|Revolver|Ruger|SVD|FAL|K98|Tundra|Blaze|Trumpet|Savanna|Vaiga|Famas|Aug|PP19|Vikhr|Crossbow|LongHorn|Derringer|Flaregun)")) return ("Weapon", "Оружие", "🔫");
        if (Regex.IsMatch(c, @"(Axe|Hatchet|Knife|Machete|Crowbar|Hammer|Sledge|Shovel|Wrench|Screwdriver|Pliers|Hacksaw|HandSaw|HandDrill|Sickle|FarmingHoe|Pickaxe|PipeWrench|SledgeHammer|WeaponCleaningKit|Whetstone)")) return ("Tool", "Инструменты", "🔪");

        if (Regex.IsMatch(c, @"(Tent|Shelter|Barrel|Crate|WoodenCrate|SeaChest|GunSafe|Safe_|TerritoryFlag|Flag_|Watchtower|Fence|Wall_|Gate_|CamoNet|ShelterKit|BarrelHoles)")) return ("Base", "База и хранение", "⛺");
        if (Regex.IsMatch(c, @"(Compass|Map_|GPS|Radio|Binoculars|Rangefinder|Flashlight|Torch|HeadTorch|Chemlight|Lighter|Matchbox|Battery|PetrolLighter|DuctTape|Rope|Netting|FishingRod|HuntingOptics|NVGoggles|Canteen)")) return ("Gear", "Снаряжение", "📻");
        if (Regex.IsMatch(c, @"(CarBattery|Radiator|SparkPlug|CarTent|TruckBattery|TireRepairKit|Wheel|Canister|CarRadiator)")) return ("Vehicle", "Запчасти авто", "🔧");

        // --- фолбэк по категории из types.xml ---
        return cat switch
        {
            "weapons" => ("Weapon", "Оружие", "🔫"),
            "clothes" => ("Body", "Торс", "👕"),
            "containers" => ("Back", "Рюкзак", "🎒"),
            "food" => ("Food", "Еда и вода", "🥫"),
            "tools" => ("Tool", "Инструменты", "🔪"),
            "medical" or "medicine" => ("Medical", "Медицина", "💊"),
            _ => ("Other", "Прочее", "🔹")
        };
    }

    // Запись типа: имя класса + первая категория внутри блока.
    private static Regex TypePattern() => TypeRegex;

    private static readonly Regex TypeRegex = new(
        @"<type\s+name=""(?<name>[^""]+)""[\s\S]*?(?:<category\s+name=""(?<cat>[^""]+)""|</type>)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
