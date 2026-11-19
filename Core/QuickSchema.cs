namespace DayZPulse.Core;

/// <summary>Тип поля в настройках.</summary>
public enum FieldKind
{
    Bool,
    Int,
    Float,
    Text,
    Select
}

/// <summary>Одно поле настройки в файле сервера.</summary>
public sealed record FieldDef(
    string Key,
    string Label,
    string Desc,
    FieldKind Kind,
    string Group = "",
    double? Min = null,
    double? Max = null,
    string[]? Options = null);

/// <summary>
/// Полные схемы трёх файлов конфигурации DayZ: serverDZ.cfg,
/// cfggameplay.json и db/globals.xml. Описания взяты из документации
/// DayZ и официальных скриптов (CfgGameplayHandler, cfggameplaydatajson.c).
/// </summary>
public static class QuickSchema
{
    // ---------------------------------------------------------- serverDZ.cfg

    public static readonly FieldDef[] ServerCfg =
    [
        new("hostname", "Название сервера", "Как сервер называется в списке серверов.", FieldKind.Text, "Основное"),
        new("password", "Пароль сервера", "Пароль для входа. Пусто — без пароля.", FieldKind.Text, "Основное"),
        new("passwordAdmin", "Пароль администратора", "Пароль для входа в админ-режим в игре.", FieldKind.Text, "Основное"),
        new("description", "Описание", "Текст под названием в списке серверов.", FieldKind.Text, "Основное"),
        new("enableWhitelist", "Вайтлист", "Включён — заходят только игроки из списка разрешённых.", FieldKind.Bool, "Основное"),
        new("maxPlayers", "Слотов", "Сколько игроков может быть одновременно.", FieldKind.Int, "Основное", 1, 200),
        new("shardId", "shardId", "6 символов. Отделяет сохранения частного сервера.", FieldKind.Text, "Основное"),
        new("instanceId", "instanceId", "Номер сервера на машине, чтобы сохранения не пересекались.", FieldKind.Int, "Основное", 1, 64),

        new("verifySignatures", "Проверка подписей", "2 — только так: сервер проверяет подписи модов.", FieldKind.Int, "Проверки", 0, 2),
        new("forceSameBuild", "Только одна сборка", "Разрешать вход только с той же версией игры.", FieldKind.Bool, "Проверки"),
        new("guaranteedUpdates", "guaranteedUpdates", "Протокол связи с игровым сервером. Должно быть 1.", FieldKind.Int, "Проверки", 1, 1),
        new("storageAutoFix", "Чинить сохранения", "Заменять повреждённые файлы сохранений пустыми.", FieldKind.Bool, "Проверки"),

        new("thirdPersonView", "Вид от третьего лица", "Разрешён ли вид из-за плеча.", FieldKind.Select, "Игровой процесс", Options: ["0|Только от первого", "1|Разрешён", "2|Только в транспорте"]),
        new("disableCrosshair", "Отключить прицел", "Убирает прицел на экране игрока.", FieldKind.Bool, "Игровой процесс"),
        new("disableVoN", "Отключить голосовой чат", "Запрещает голосовое общение.", FieldKind.Bool, "Игровой процесс"),
        new("vonCodecQuality", "Качество голоса", "0 — низкое, 30 — высокое.", FieldKind.Int, "Игровой процесс", 0, 30),
        new("disablePersonalLight", "Отключить личный свет", "Убирает мягкий свет вокруг игрока ночью.", FieldKind.Bool, "Игровой процесс"),
        new("lightingConfig", "Ночное освещение", "0 — светлее, 1 — темнее.", FieldKind.Select, "Игровой процесс", Options: ["0|Светлее", "1|Темнее"]),
        new("enableDebugMonitor", "Отладочный монитор", "Показывать служебную информацию на экране.", FieldKind.Bool, "Игровой процесс"),

        new("serverTime", "Время сервера", "«SystemTime» — время компьютера, либо «ГГГГ/ММ/ДД/ЧЧ/ММ».", FieldKind.Text, "Время"),
        new("serverTimeAcceleration", "Ускорение времени", "Во сколько раз быстрее идёт день. 24 — сутки за час.", FieldKind.Int, "Время", 0, 24),
        new("serverNightTimeAcceleration", "Ускорение ночи", "Дополнительное ускорение ночью (0.1–64).", FieldKind.Float, "Время", 0.1, 64),
        new("serverTimePersistent", "Сохранять время", "Время продолжится с момента остановки сервера.", FieldKind.Bool, "Время"),

        new("loginQueueConcurrentPlayers", "Одновременных входов", "Сколько игроков подключаются за раз. Защищает от просадки.", FieldKind.Int, "Очередь входа", 1, 50),
        new("loginQueueMaxPlayers", "Размер очереди", "Сколько игроков могут ждать входа.", FieldKind.Int, "Очередь входа", 1, 1000),

        new("enableCfgGameplayFile", "Читать cfggameplay.json", "Без этого флага DayZ не применяет настройки мира.", FieldKind.Bool, "Мир"),

        new("adminLogBuildActions", "Лог: строительство", "Писать в админ-лог действия со строительством.", FieldKind.Bool, "Журналы"),
        new("adminLogPlacement", "Лог: размещение", "Писать размещение предметов.", FieldKind.Bool, "Журналы"),
        new("adminLogBuildActionsOnly", "Лог: только базы", "Писать только постройки баз.", FieldKind.Bool, "Журналы"),
        new("adminLogPlayerHitsOnly", "Лог: только попадания", "Писать только попадания по игрокам.", FieldKind.Bool, "Журналы"),
        new("adminLogPlayerList", "Лог: список игроков", "Периодически писать список игроков.", FieldKind.Bool, "Журналы"),

        // Производительность: те же данные, что показывает команда #monitor.
        // Панель читает их из RPT-лога и выводит на дашборде.
        new("logAverageFps", "Метрики: FPS", "Как часто писать средний FPS сервера в лог (секунды). 0 — выключено.", FieldKind.Int, "Производительность", 0, 60),
        new("logMemory", "Метрики: память", "Как часто писать потребление памяти сервером (секунды). 0 — выключено.", FieldKind.Int, "Производительность", 0, 60),
        new("logPlayers", "Метрики: игроки", "Как часто писать число игроков (секунды). 0 — выключено.", FieldKind.Int, "Производительность", 0, 60),
        new("logAI", "Метрики: зомби и животные", "Как часто писать число зомби и животных (секунды). 0 — выключено.", FieldKind.Int, "Производительность", 0, 60)
    ];

    // ------------------------------------------------------ cfggameplay.json

    public static readonly FieldDef[] Gameplay =
    [
        // Общее
        new("GeneralData.disableBaseDamage", "Урон по строениям", "Выключено — постройки неуязвимы.", FieldKind.Bool, "Общее"),
        new("GeneralData.disableBaseDecay", "Затухание построек", "Выключено — базы не разрушаются со временем.", FieldKind.Bool, "Общее"),
        new("GeneralData.disableContainerDamage", "Урон по контейнерам", "Выключено — ящики и контейнеры защищены.", FieldKind.Bool, "Общее"),
        new("GeneralData.disableRespawnDialog", "Выбор возрождения", "Выключено — игрок сам выбирает точку появления.", FieldKind.Bool, "Общее"),
        new("GeneralData.disableRespawnInUnconsciousness", "Возрождение в обмороке", "Выключено — нельзя возродиться без сознания.", FieldKind.Bool, "Общее"),

        // Игрок
        new("PlayerData.disablePersonalLight", "Личный источник света", "Мягкий свет вокруг игрока ночью.", FieldKind.Bool, "Игрок"),
        new("PlayerData.disable2dMap", "Запретить 2D-карту", "Выключено — плоская карта доступна.", FieldKind.Bool, "Игрок"),

        // Выносливость
        new("PlayerData.StaminaData.staminaMax", "Максимум стамины", "Запас выносливости игрока.", FieldKind.Float, "Выносливость", 1, 5000),
        new("PlayerData.StaminaData.staminaWeightLimitThreshold", "Порог веса", "С какого веса груз начинает влиять.", FieldKind.Float, "Выносливость", 0, 200),
        new("PlayerData.StaminaData.staminaKgToStaminaPercentPenalty", "Штраф за вес", "Насколько вес снижает выносливость.", FieldKind.Float, "Выносливость", 0, 10),
        new("PlayerData.StaminaData.staminaMinCap", "Минимальный запас", "Ниже этого уровня стамина не падает.", FieldKind.Float, "Выносливость", 0, 100),
        new("PlayerData.StaminaData.sprintStaminaModifierErc", "Расход: бег стоя", "Множитель расхода при беге стоя.", FieldKind.Float, "Выносливость", 0, 10),
        new("PlayerData.StaminaData.sprintStaminaModifierCro", "Расход: бег в приседе", "Множитель расхода при беге в приседе.", FieldKind.Float, "Выносливость", 0, 10),
        new("PlayerData.StaminaData.sprintSwimmingStaminaModifier", "Расход: плавание", "Множитель расхода при плавании.", FieldKind.Float, "Выносливость", 0, 10),
        new("PlayerData.StaminaData.sprintLadderStaminaModifier", "Расход: лазание", "Множитель расхода при лазании.", FieldKind.Float, "Выносливость", 0, 10),
        new("PlayerData.StaminaData.meleeStaminaModifier", "Расход: ближний бой", "Множитель расхода в ближнем бою.", FieldKind.Float, "Выносливость", 0, 10),
        new("PlayerData.StaminaData.obstacleTraversalStaminaModifier", "Расход: препятствия", "Множитель расхода при перелазании.", FieldKind.Float, "Выносливость", 0, 10),
        new("PlayerData.StaminaData.holdBreathStaminaModifier", "Расход: задержка дыхания", "Множитель расхода при задержке дыхания.", FieldKind.Float, "Выносливость", 0, 10),

        // Шок
        new("PlayerData.ShockHandlingData.shockRefillSpeedConscious", "Восстановление шока", "Скорость восстановления шока в сознании.", FieldKind.Float, "Шок", 0, 100),
        new("PlayerData.ShockHandlingData.shockRefillSpeedUnconscious", "Восстановление в обмороке", "Скорость восстановления шока без сознания.", FieldKind.Float, "Шок", 0, 100),
        new("PlayerData.ShockHandlingData.allowRefillSpeedModifier", "Учитывать модификаторы", "Учитывать факторы, влияющие на восстановление.", FieldKind.Bool, "Шок"),

        // Движение
        new("PlayerData.MovementData.timeToSprint", "Время до спринта", "Сколько нужно удерживать бег.", FieldKind.Float, "Движение", 0, 5),
        new("PlayerData.MovementData.timeToStrafeJog", "Время до бега боком", "Задержка перехода в бег боком.", FieldKind.Float, "Движение", 0, 5),
        new("PlayerData.MovementData.rotationSpeedJog", "Скорость поворота (бег)", "Как быстро поворачивается игрок.", FieldKind.Float, "Движение", 0, 5),
        new("PlayerData.MovementData.timeToStrafeSprint", "Время до спринта боком", "Задержка спринта боком.", FieldKind.Float, "Движение", 0, 5),
        new("PlayerData.MovementData.rotationSpeedSprint", "Скорость поворота (спринт)", "Поворот при спринте.", FieldKind.Float, "Движение", 0, 5),
        new("PlayerData.MovementData.allowStaminaAffectInertia", "Инерция зависит от стамины", "Уставший игрок двигается тяжелее.", FieldKind.Bool, "Движение"),

        // Утопление
        new("PlayerData.DrowningData.staminaDepletionSpeed", "Утопление: стамина", "Как быстро тратится стамина под водой.", FieldKind.Float, "Утопление", 0, 100),
        new("PlayerData.DrowningData.healthDepletionSpeed", "Утопление: здоровье", "Как быстро тратится здоровье под водой.", FieldKind.Float, "Утопление", 0, 100),
        new("PlayerData.DrowningData.shockDepletionSpeed", "Утопление: шок", "Как быстро растёт шок под водой.", FieldKind.Float, "Утопление", 0, 100),

        // Мир
        new("WorldsData.lightingConfig", "Ночное освещение", "0 — светлее, 1 — темнее.", FieldKind.Select, "Мир", Options: ["0|Светлее", "1|Темнее"]),
        new("WorldsData.bunkerBroadcastEnabled", "Бункер: вещание", "Радиовещание в бункере (Livonia).", FieldKind.Bool, "Мир"),

        // Карта и интерфейс
        new("UIData.use3DMap", "3D-карта", "Показывать объёмную карту.", FieldKind.Bool, "Карта и интерфейс"),
        new("MapData.ignoreMapOwnership", "Карта без предмета", "Открывать карту без самой карты в руках.", FieldKind.Bool, "Карта и интерфейс"),
        new("MapData.ignoreNavItemsOwnership", "Навигация без предмета", "Компас и GPS работают без предметов.", FieldKind.Bool, "Карта и интерфейс"),
        new("MapData.displayPlayerPosition", "Позиция на карте", "Показывать своё положение на карте.", FieldKind.Bool, "Карта и интерфейс"),
        new("MapData.displayNavInfo", "Навигационная информация", "Показывать отметки навигации.", FieldKind.Bool, "Карта и интерфейс"),

        // Индикация попаданий
        new("UIData.HitIndicationData.hitDirectionOverrideEnabled", "Индикация попаданий", "Показывать, откуда прилетело.", FieldKind.Bool, "Индикация попаданий"),
        new("UIData.HitIndicationData.hitDirectionBehaviour", "Поведение индикатора", "0 — статичный, 1 — вращается.", FieldKind.Select, "Индикация попаданий", Options: ["0|Статичный", "1|Вращается"]),
        new("UIData.HitIndicationData.hitDirectionStyle", "Стиль индикатора", "0 — всплеск, 1 — полоса.", FieldKind.Select, "Индикация попаданий", Options: ["0|Всплеск", "1|Полоса"]),
        new("UIData.HitIndicationData.hitDirectionMaxDuration", "Длительность", "Как долго держится индикатор (с).", FieldKind.Float, "Индикация попаданий", 0, 10),
        new("UIData.HitIndicationData.hitDirectionBreakPointRelative", "Точка разрыва", "Порог для индикатора.", FieldKind.Float, "Индикация попаданий", 0, 1),
        new("UIData.HitIndicationData.hitDirectionScatter", "Разброс", "Насколько «плавает» индикатор.", FieldKind.Float, "Индикация попаданий", 0, 1),
        new("UIData.HitIndicationData.hitIndicationPostProcessEnabled", "Эффект экрана", "Красная вспышка при попадании.", FieldKind.Bool, "Индикация попаданий"),

        // Строительство
        new("BaseBuildingData.HologramData.disableIsCollidingBBoxCheck", "Строить без проверки габаритов", "Разрешить ставить объект с пересечением.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsCollidingPlayerCheck", "Строить на игроке", "Разрешить ставить постройку на игроках.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsClippingRoofCheck", "Строить без проверки крыши", "Разрешить постройку без свободной крыши.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsBaseViableCheck", "Строить без проверки основания", "Отключает проверку пригодности места.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsCollidingGPlotCheck", "Строить на грядке", "Разрешить ставить объект на грядке.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsCollidingAngleCheck", "Строить без проверки угла", "Отключает проверку наклона.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsPlacementPermittedCheck", "Строить в запретной зоне", "Разрешить постройку в запрещённых местах.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableHeightPlacementCheck", "Строить в воздухе", "Разрешить ставить объекты в воздухе.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsUnderwaterCheck", "Строить под водой", "Разрешить постройку под водой.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableIsInTerrainCheck", "Строить в земле", "Разрешить постройку внутри рельефа.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.HologramData.disableColdAreaPlacementCheck", "Строить в холодной зоне", "Разрешить постройку в холодных зонах.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.ConstructionData.disablePerformRoofCheck", "Без проверки крыши", "Отключает проверку крыши при сборке.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.ConstructionData.disableIsCollidingCheck", "Без проверки столкновений", "Отключает проверку пересечений.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.ConstructionData.disableDistanceCheck", "Без проверки расстояния", "Строить на любом расстоянии.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.ConstructionData.disableItemFalling", "Предметы не падают", "Собранные предметы не падают с высоты.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.ConstructionData.disableDropAttachmentsOnFall", "Не терять обвес при падении", "Обвесы не отваливаются при падении.", FieldKind.Bool, "Строительство"),
        new("BaseBuildingData.ConstructionData.disableRefundMaterialsOnFall", "Не возвращать материалы", "Материалы не возвращаются при падении.", FieldKind.Bool, "Строительство"),

        // Замки
        new("ExternalLockData.protectionCountStageOne", "Взломов: первая стадия", "Сколько раз нужно взломать до первой стадии.", FieldKind.Int, "Замки", 0, 50),
        new("ExternalLockData.protectionCountStageTwo", "Взломов: вторая стадия", "Сколько раз нужно взломать до второй стадии.", FieldKind.Int, "Замки", 0, 50),
        new("ExternalLockData.protectionTime", "Время защиты (с)", "Сколько держится защита после взлома.", FieldKind.Float, "Замки", 0, 3600),
        new("ExternalLockData.protectionResetTime", "Сброс защиты (с)", "Через сколько защита восстанавливается.", FieldKind.Float, "Замки", 0, 86400),

        // Транспорт
        new("VehicleData.boatDecayMultiplier", "Затухание лодок", "Во сколько раз быстрее разрушаются лодки.", FieldKind.Float, "Транспорт", 0, 10),
        new("VehicleData.canDetachAttachedCarWheels", "Снимать колёса машин", "Можно снять установленные колёса.", FieldKind.Bool, "Транспорт"),
        new("VehicleData.canDamageAttachedCarWheels", "Повреждать колёса машин", "Колёса можно повредить.", FieldKind.Bool, "Транспорт"),
        new("VehicleData.canDetachAttachedBikeWheels", "Снимать колёса мотоциклов", "Снимать колёса с мотоциклов.", FieldKind.Bool, "Транспорт"),
        new("VehicleData.canDamageAttachedBikeWheels", "Повреждать колёса мотоциклов", "Повреждать колёса мотоциклов.", FieldKind.Bool, "Транспорт"),

        // Погода и песчаная буря
        new("SandstormData.sandstormFrequency", "Частота песчаной бури", "0 — никогда, 1 — часто (карта Sakhal).", FieldKind.Float, "Погода и буря", 0, 1)
    ];

    // ---------------------------------------------------------- db/globals.xml

    public static readonly FieldDef[] Globals =
    [
        new("ZombieMaxCount", "Максимум зомби", "Сколько заражённых может быть на карте.", FieldKind.Int, "Население", 0, 100000),
        new("AnimalMaxCount", "Максимум животных", "Сколько животных может быть на карте.", FieldKind.Int, "Население", 0, 100000),
        new("SpawnInitial", "Стартовое заражение", "Сколько зомби появляется при старте сервера.", FieldKind.Int, "Население", 0, 100000),
        new("InitialSpawn", "Начальный спавн", "Начальное заполнение зон лутом (%).", FieldKind.Int, "Население", 0, 100),
        new("RestartSpawn", "Спавн при рестарте", "Заполнение зон лутом после рестарта (%).", FieldKind.Int, "Население", 0, 100),
        new("ZoneSpawnDist", "Дистанция спавна", "Радиус, в котором появляются объекты.", FieldKind.Int, "Население", 0, 5000),

        new("CleanupLifetimeDeadPlayer", "Труп игрока (с)", "Через сколько исчезает тело игрока.", FieldKind.Int, "Очистка", 0, 86400),
        new("CleanupLifetimeDeadInfected", "Труп зомби (с)", "Через сколько исчезает тело зомби.", FieldKind.Int, "Очистка", 0, 86400),
        new("CleanupLifetimeDeadAnimal", "Труп животного (с)", "Через сколько исчезает тело животного.", FieldKind.Int, "Очистка", 0, 86400),
        new("CleanupLifetimeDefault", "Прочее (с)", "Общее время жизни прочего лута.", FieldKind.Int, "Очистка", 0, 86400),
        new("CleanupLifetimeRuined", "Разрушенное (с)", "Через сколько исчезает разрушенный предмет.", FieldKind.Int, "Очистка", 0, 86400),
        new("CleanupLifetimeLimit", "Лимит очистки", "Сколько объектов чистить за один проход.", FieldKind.Int, "Очистка", 0, 10000),
        new("CleanupAvoidance", "Радиус избегания", "Не чистить рядом с игроками в этом радиусе.", FieldKind.Int, "Очистка", 0, 5000),

        new("LootSpawnAvoidance", "Избегание лута", "Радиус, где лут не появляется рядом с игроком.", FieldKind.Int, "Лут", 0, 5000),
        new("LootProxyPlacement", "Размещение прокси", "Как расставляется лут (0/1).", FieldKind.Int, "Лут", 0, 1),
        new("LootDamageMin", "Минимальный износ", "Наименьший износ нового лута (0–1).", FieldKind.Float, "Лут", 0, 1),
        new("LootDamageMax", "Максимальный износ", "Наибольший износ нового лута (0–1).", FieldKind.Float, "Лут", 0, 1),
        new("FoodDecay", "Порча еды", "1 — еда портится, 0 — нет.", FieldKind.Int, "Лут", 0, 1),

        new("RespawnLimit", "Лимит возрождений", "Максимум попыток возрождения.", FieldKind.Int, "Возрождение", 0, 100),
        new("RespawnAttempt", "Попыток возрождения", "Сколько попыток даётся за раз.", FieldKind.Int, "Возрождение", 0, 20),
        new("RespawnTypes", "Типы возрождения", "Битовая маска доступных типов.", FieldKind.Int, "Возрождение", 0, 255),

        new("IdleModeCountdown", "Отсчёт простоя (мин)", "Через сколько выключать пустой сервер.", FieldKind.Int, "Простой", 0, 1440),
        new("IdleModeStartup", "Простой при старте", "1 — сервер выключится, если никто не зашёл.", FieldKind.Int, "Простой", 0, 1),
        new("TimeLogin", "Время входа (с)", "Сколько ждать при подключении игрока.", FieldKind.Int, "Сеть", 0, 300),
        new("TimeLogout", "Время выхода (с)", "Сколько ждать при отключении игрока.", FieldKind.Int, "Сеть", 0, 300),
        new("TimePenalty", "Штраф времени (с)", "Задержка при превышении времени входа.", FieldKind.Int, "Сеть", 0, 300),
        new("TimeHopping", "Защита от хоппинга (с)", "Запрет быстрого перезахода на сервер.", FieldKind.Int, "Сеть", 0, 3600),

        new("WorldWetTempUpdate", "Учёт сырости", "Обновлять температуру от промокания.", FieldKind.Int, "Мир", 0, 1),
        new("FlagRefreshFrequency", "Обновление флага (с)", "Как часто обновляется флаг территории.", FieldKind.Int, "Мир", 0, 10000000),
        new("FlagRefreshMaxDuration", "Флаг: максимум (с)", "Сколько флаг держится без обновления.", FieldKind.Int, "Мир", 0, 100000000)
    ];
}
