using System.Text.Json;
using DayZPulse.Core;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Security;

namespace DayZPulse.Tracker;

/// <summary>
/// Обмен с модом DayZPulse (приём данных, выдача команд) и REST API трекера
/// для панели. У каждого сервера свой токен, поэтому запросы мода
/// маршрутизируются по нему автоматически.
/// </summary>
public static class TrackerEndpoints
{
    public static void MapTrackerEndpoints(this WebApplication app)
    {
        // ============================================ со стороны мода (X-Api-Token)

        app.MapPost("/tracker/ingest", async (TrackerService tracker, HttpContext context) =>
        {
            var runtime = tracker.Authorize(context);
            if (runtime is null)
            {
                return Results.Unauthorized();
            }

            var body = await tracker.ReadBodyAsync(context, context.RequestAborted);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Некорректный JSON." });
            }

            var server = TrackerProtocol.ParseServer(body.Value);
            var players = TrackerProtocol.ParsePlayers(body.Value);
            runtime.Tracker.ApplySnapshot(server, players, DateTimeOffset.Now);
            tracker.MarkConnected(runtime, players.Count);

            return Results.Ok(new { ok = true, server = runtime.Id });
        });

        app.MapGet("/tracker/commands", (TrackerService tracker, HttpContext context) =>
        {
            var runtime = tracker.Authorize(context);
            if (runtime is null)
            {
                return Results.Unauthorized();
            }

            if (!runtime.Profile.Tracker.Enabled)
            {
                return Results.NoContent();
            }

            var entries = runtime.Commands.TakePending(25);
            if (entries.Count == 0)
            {
                return Results.NoContent();
            }

            var text = string.Join("\n", entries.Select(CommandQueue.BuildLine));
            return Results.Text(text, "text/plain; charset=utf-8");
        });

        app.MapPost("/tracker/commands/ack", async (TrackerService tracker, HttpContext context) =>
        {
            var runtime = tracker.Authorize(context);
            if (runtime is null)
            {
                return Results.Unauthorized();
            }

            var body = await tracker.ReadBodyAsync(context, context.RequestAborted);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Некорректный JSON." });
            }

            var results = TrackerProtocol.ParseAck(body.Value);
            runtime.Commands.Acknowledge(results);
            return Results.Ok(new { ok = true, applied = results.Count });
        });

        app.MapPost("/tracker/ac/ingest", async (TrackerService tracker, HttpContext context) =>
        {
            var runtime = tracker.Authorize(context);
            if (runtime is null)
            {
                return Results.Unauthorized();
            }

            var body = await tracker.ReadBodyAsync(context, context.RequestAborted);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Некорректный JSON." });
            }

            var violations = TrackerProtocol.ParseViolations(body.Value);
            var added = runtime.Tracker.AddViolations(violations, DateTimeOffset.Now);
            return Results.Ok(new { ok = true, added });
        });

        // Снимок инвентаря игрока от мода (по токену сервера).
        app.MapPost("/tracker/inventory", async (TrackerService tracker, HttpContext context) =>
        {
            var runtime = tracker.Authorize(context);
            if (runtime is null)
            {
                return Results.Unauthorized();
            }

            var body = await tracker.ReadBodyAsync(context, context.RequestAborted);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Некорректный JSON." });
            }

            var parsed = TrackerProtocol.ParseInventory(body.Value);
            if (parsed is not null)
            {
                var (id, name, itemsJson) = parsed.Value;
                runtime.Tracker.SetInventory(id, name, itemsJson, DateTimeOffset.Now);
            }

            return Results.Ok(new { ok = true });
        });

        // ================================================= со стороны панели (сессия)

        var api = app.MapGroup("/api").RequireRateLimiting("api");

        // ---------------------- сводка по серверам (без привязки к конкретному)

        api.MapGet("/servers", (ServerRegistry registry) => Results.Ok(registry.Summary()));

        // ------------------- всё остальное работает в контексте {serverId}

        api.MapGet("/servers/{serverId}/status", (string serverId, ServerRegistry registry) =>
        {
            var runtime = registry.Get(serverId) ?? throw new KeyNotFoundException($"Сервер не найден: {serverId}");
            return Results.Ok(ServerDtos.Status(runtime));
        });

        api.MapPost("/servers/{serverId}/start", async (string serverId, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            await runtime.Supervisor.StartAsync(Actor(ctx), ctx.RequestAborted);
            audit.Write(Actor(ctx), "server.start", runtime.Name, null);
            return Results.Ok(ServerDtos.Status(runtime));
        });

        api.MapPost("/servers/{serverId}/stop", async (string serverId, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            await runtime.Supervisor.StopAsync(Actor(ctx), ctx.RequestAborted);
            audit.Write(Actor(ctx), "server.stop", runtime.Name, null);
            return Results.Ok(ServerDtos.Status(runtime));
        });

        api.MapPost("/servers/{serverId}/restart", async (string serverId, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            await runtime.Supervisor.RestartAsync(Actor(ctx), ctx.RequestAborted);
            audit.Write(Actor(ctx), "server.restart", runtime.Name, null);
            return Results.Ok(ServerDtos.Status(runtime));
        });

        api.MapPost("/servers/{serverId}/command", async (CommandRequest request, string serverId,
            ServerRegistry registry, RconService rcon, AuditLog audit, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Command))
            {
                return Results.BadRequest(new { error = "Команда не может быть пустой." });
            }

            var runtime = Require(registry, serverId);
            var session = rcon.Session(serverId);

            // Команды уходят через RCON: это то, что видит и выполняет сервер.
            if (!session.IsConnected)
            {
                return Results.BadRequest(new
                {
                    error = session.LastError ?? "RCON не подключён. Запустите сервер и дождитесь подключения.",
                    code = "rcon_offline"
                });
            }

            try
            {
                var response = await session.SendAsync(request.Command, ctx.RequestAborted);
                audit.Write(Actor(ctx), "server.command", request.Command, null);

                return Results.Ok(new { ok = true, response });
            }
            catch (TimeoutException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = "rcon_timeout" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = "rcon_offline" });
            }
        });

        // --------------------------------------------------------------- RCON

        api.MapGet("/servers/{serverId}/rcon/status", (string serverId, ServerRegistry registry, RconService rcon) =>
        {
            var runtime = Require(registry, serverId);
            var session = rcon.Session(serverId);
            var server = runtime.ServerSettings;

            // Подсказка, почему RCON может не работать: чаще всего BattlEye
            // не стартует вместе с сервером, и порт просто никто не слушает.
            string? hint = null;
            if (!session.IsConnected && runtime.Supervisor.IsRunning)
            {
                hint = "BattlEye не отвечает. Убедитесь, что в сборке сервера есть " +
                       "battleye\\BEServer_x64.dll и запуск идёт без -noBE. " +
                       "На перепакованных сборках без BattlEye RCON недоступен — " +
                       "команды и сообщения в панели работают через мод.";
            }

            return Results.Ok(new
            {
                connected = session.IsConnected,
                error = session.LastError,
                hint,
                host = "127.0.0.1",
                port = RconService.PortFor(server),
                passwordSet = !string.IsNullOrWhiteSpace(server.RconPassword),
                lastMessageAt = session.LastMessageAt
            });
        });

        // Переподключение к RCON (например после смены пароля).
        api.MapPost("/servers/{serverId}/rcon/reconnect", async (string serverId, ServerRegistry registry,
            RconService rcon, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var session = rcon.Session(serverId);

            await session.DisconnectAsync();

            var password = rcon.EnsureConfig(runtime);
            var port = RconService.PortFor(runtime.ServerSettings);
            var ok = await session.ConnectAsync("127.0.0.1", port, password, ctx.RequestAborted);

            audit.Write(Actor(ctx), "rcon.reconnect", runtime.Name, null);

            return Results.Ok(new { connected = ok, error = session.LastError, port });
        });

        // Живая консоль RCON: накопленные строки.
        api.MapGet("/servers/{serverId}/rcon/console", (string serverId, int? lines,
            ServerRegistry registry, RconService rcon) =>
        {
            Require(registry, serverId);
            var session = rcon.Session(serverId);
            var snapshot = session.Snapshot(lines ?? 500);

            return Results.Ok(new
            {
                connected = session.IsConnected,
                error = session.LastError,
                lines = snapshot.Select(l => new { timestamp = l.At, text = l.Text })
            });
        });

        // Список игроков от RCON-команды players.
        api.MapGet("/servers/{serverId}/rcon/players", async (string serverId, ServerRegistry registry,
            RconService rcon, HttpContext ctx) =>
        {
            Require(registry, serverId);
            var session = rcon.Session(serverId);

            if (!session.IsConnected)
            {
                return Results.Ok(new { connected = false, players = Array.Empty<object>() });
            }

            try
            {
                var players = await session.PlayersAsync(ctx.RequestAborted);
                return Results.Ok(new
                {
                    connected = true,
                    players = players.Select(p => new
                    {
                        number = p.Number,
                        name = p.Name,
                        ip = p.Ip,
                        port = p.Port,
                        ping = p.Ping,
                        guid = p.Guid
                    })
                });
            }
            catch (TimeoutException)
            {
                return Results.Ok(new { connected = true, players = Array.Empty<object>(), error = "RCON не ответил" });
            }
        });

        api.MapPost("/servers/{serverId}/clear-console", (string serverId, ServerRegistry registry) =>
        {
            Require(registry, serverId).Supervisor.ClearConsole();
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/servers/{serverId}/console", (string serverId, int? lines, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            var snapshot = runtime.Console.Snapshot(lines ?? 500);
            return Results.Ok(new
            {
                lines = snapshot.Select(l => new { sequence = l.Sequence, timestamp = l.Timestamp, stream = l.Stream, text = l.Text }),
                lastSequence = runtime.Console.LastSequence,
                launchCommand = runtime.Supervisor.LastLaunchCommandLine,
                workingDirectory = ServerPaths.WorkingDirectory(runtime.ServerSettings),
                errors = runtime.Supervisor.RecentErrors
            });
        });

        // ------------------------------------------------ конфигурация сервера

        api.MapGet("/servers/{serverId}/configs", (string serverId, ServerRegistry registry) =>
            Results.Ok(Require(registry, serverId).Configs.List()));

        api.MapGet("/servers/{serverId}/configs/content", async (string serverId, string path,
            ServerRegistry registry, CancellationToken ct) =>
        {
            var configs = Require(registry, serverId).Configs;
            return Results.Ok(new { path, editable = configs.IsEditable(path), content = await configs.ReadAsync(path, ct) });
        });

        api.MapPut("/servers/{serverId}/configs/content", async (ConfigSaveRequest request, string serverId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest(new { error = "Не указан путь к файлу." });
            }

            var runtime = Require(registry, serverId);
            await runtime.Configs.WriteAsync(request.Path, request.Content ?? "", ctx.RequestAborted);
            audit.Write(Actor(ctx), "config.save", $"{runtime.Name}: {request.Path}", null);
            return Results.Ok(new { ok = true });
        });

        // ---------------------------------------------- быстрые настройки

        // Схемы и значения всех файлов конфигурации.
        api.MapGet("/servers/{serverId}/quick", (string serverId, ServerRegistry registry, RconService rcon) =>
        {
            var runtime = Require(registry, serverId);
            var server = runtime.ServerSettings;
            var working = ServerPaths.WorkingDirectory(server);
            var configFile = string.IsNullOrWhiteSpace(server.ConfigFile) ? "serverDZ.cfg" : server.ConfigFile;

            return Results.Ok(new
            {
                schema = new
                {
                    server = QuickSchema.ServerCfg,
                    gameplay = QuickSchema.Gameplay,
                    globals = QuickSchema.Globals
                },
                values = new
                {
                    server = QuickConfig.ReadServerCfg(working, configFile),
                    gameplay = QuickConfig.ReadGameplay(server),
                    globals = QuickConfig.ReadGlobals(server)
                },
                weather = QuickConfig.ReadWeather(server),
                missions = QuickConfig.Missions(server),
                rcon = new
                {
                    enabled = server.RconEnabled,
                    port = RconService.PortFor(server),
                    password = server.RconPassword
                },
                paths = new
                {
                    serverCfg = ServerPaths.ConfigFile(server),
                    weather = QuickConfig.WeatherPath(server),
                    gameplay = QuickConfig.GameplayPath(server),
                    globals = QuickConfig.GlobalsPath(server),
                    mission = QuickConfig.MissionDirectory(server) ?? ""
                },
                // Настройки мира работают только при enableCfgGameplayFile=1.
                gameplayFile = new
                {
                    exists = QuickConfig.GameplayFileExists(server),
                    enabled = QuickConfig.IsGameplayFileEnabled(working, configFile)
                },
                running = runtime.Supervisor.IsRunning,
                pending = PendingConfig.HasAny(server),
                pendingFiles = PendingConfig.List(server)
            });
        });

        // Сохранение одного из файлов: server, gameplay или globals.
        // Если сервер запущен — правки откладываются до следующего запуска.
        api.MapPut("/servers/{serverId}/quick/{section}", (string serverId, string section,
            Dictionary<string, string> values, ServerRegistry registry, SettingsStore settings,
            AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var server = runtime.ServerSettings;
            var working = ServerPaths.WorkingDirectory(server);
            var configFile = string.IsNullOrWhiteSpace(server.ConfigFile) ? "serverDZ.cfg" : server.ConfigFile;
            var running = runtime.Supervisor.IsRunning;

            QuickConfig.WithStaging(server, running, () =>
            {
                switch (section.ToLowerInvariant())
                {
                    case "server":
                        QuickConfig.WriteServerCfg(working, configFile, values);
                        break;
                    case "gameplay":
                        QuickConfig.WriteGameplay(server, values);
                        break;
                    case "globals":
                        QuickConfig.WriteGlobals(server, values);
                        break;
                }
            });

            if (section.Equals("server", StringComparison.OrdinalIgnoreCase))
            {
                // RCON хранится в профиле сервера, а не в файле.
                if (values.TryGetValue("__rconEnabled", out var rconEnabled))
                {
                    settings.UpdateServer(serverId, p => p.Server.RconEnabled =
                        rconEnabled is "1" || rconEnabled.Equals("true", StringComparison.OrdinalIgnoreCase));
                }

                if (values.TryGetValue("__rconPort", out var rconPort) &&
                    int.TryParse(rconPort, out var port) && port is > 0 and <= 65535)
                {
                    settings.UpdateServer(serverId, p => p.Server.RconPort = port);
                }
            }
            else if (!section.Equals("gameplay", StringComparison.OrdinalIgnoreCase) &&
                     !section.Equals("globals", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"Неизвестный раздел: {section}" });
            }

            audit.Write(Actor(ctx), $"config.quick.{section}", running ? "отложено" : "записано", null);
            return Results.Ok(new { ok = true, pending = running });
        });

        api.MapPut("/servers/{serverId}/quick/weather", (QuickWeatherRequest request, string serverId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var running = runtime.Supervisor.IsRunning;

            QuickConfig.WithStaging(runtime.ServerSettings, running, () =>
                QuickConfig.WriteWeather(runtime.ServerSettings, request.Weather));

            audit.Write(Actor(ctx), "config.quick.weather", running ? "отложено" : "записано", null);
            return Results.Ok(new { ok = true, pending = running });
        });

        // Отложенные правки: список и применение (обычно применяется при старте).
        api.MapGet("/servers/{serverId}/pending", (string serverId, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            return Results.Ok(new
            {
                files = PendingConfig.List(runtime.ServerSettings),
                any = PendingConfig.HasAny(runtime.ServerSettings)
            });
        });

        api.MapPost("/servers/{serverId}/pending/apply", (string serverId, ServerRegistry registry,
            AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);

            if (runtime.Supervisor.IsRunning)
            {
                return Results.BadRequest(new { error = "Остановите сервер, чтобы применить отложенные правки." });
            }

            var applied = PendingConfig.Apply(runtime.ServerSettings);
            audit.Write(Actor(ctx), "config.pending.apply", $"{applied} файл(ов)", null);
            return Results.Ok(new { ok = true, applied });
        });

        api.MapPost("/servers/{serverId}/pending/discard", (string serverId, ServerRegistry registry,
            AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            PendingConfig.Clear(runtime.ServerSettings);
            audit.Write(Actor(ctx), "config.pending.discard", null, null);
            return Results.Ok(new { ok = true });
        });

        // Включает чтение cfggameplay.json (enableCfgGameplayFile = 1).
        api.MapPost("/servers/{serverId}/quick/gameplay/enable", (string serverId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var server = runtime.ServerSettings;
            var working = ServerPaths.WorkingDirectory(server);
            var configFile = string.IsNullOrWhiteSpace(server.ConfigFile) ? "serverDZ.cfg" : server.ConfigFile;

            QuickConfig.EnableGameplayFile(working, configFile);

            audit.Write(Actor(ctx), "config.quick.gameplay.enable", runtime.Name, null);
            return Results.Ok(new
            {
                ok = true,
                enabled = QuickConfig.IsGameplayFileEnabled(working, configFile)
            });
        });

        // ---------------------------------------------------- резервные копии

        api.MapGet("/servers/{serverId}/backups", (string serverId, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            return Results.Ok(new { items = runtime.Backups.List(), settings = runtime.Profile.Backups, directory = runtime.Backups.Directory });
        });

        api.MapPost("/servers/{serverId}/backups", async (string serverId, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var result = await runtime.Backups.CreateAsync("manual", ctx.RequestAborted);
            audit.Write(Actor(ctx), "backup.create", runtime.Name, null);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        api.MapPost("/servers/{serverId}/backups/restore", async (RestoreRequest request, string serverId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var message = await runtime.Backups.RestoreAsync(request.FileName, ctx.RequestAborted);
            audit.Write(Actor(ctx), "backup.restore", $"{runtime.Name}: {request.FileName}", null);
            return Results.Ok(new { ok = true, message });
        });

        api.MapDelete("/servers/{serverId}/backups/{fileName}", (string serverId, string fileName,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            runtime.Backups.Delete(fileName);
            audit.Write(Actor(ctx), "backup.delete", $"{runtime.Name}: {fileName}", null);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/servers/{serverId}/backups/download/{fileName}", (string serverId, string fileName, ServerRegistry registry) =>
        {
            var path = Require(registry, serverId).Backups.ResolveBackupPath(fileName);
            return Results.File(path, "application/zip", fileName);
        });

        // ---------------------------------------------------------- расписание

        api.MapGet("/servers/{serverId}/schedule", (string serverId, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            return Results.Ok(new
            {
                jobs = runtime.Profile.Schedule.Select(j => new
                {
                    j.Id, j.Name, j.Type, j.Enabled, j.Mode, j.Time, j.IntervalMinutes, j.Payload,
                    lastRun = runtime.Schedule.LastRunOf(j.Id)
                }),
                history = runtime.Schedule.History
            });
        });

        api.MapPost("/servers/{serverId}/schedule", (ScheduledJob job, string serverId, SettingsStore settings,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var created = Json.Clone(job);
            created.Id = string.IsNullOrWhiteSpace(created.Id) ? Guid.NewGuid().ToString("n") : created.Id;

            settings.UpdateServer(serverId, profile => profile.Schedule.Add(created));
            registry.Sync();

            audit.Write(Actor(ctx), "schedule.create", created.Name, null);
            return Results.Ok(created);
        });

        api.MapPut("/servers/{serverId}/schedule/{id}", (string id, ScheduledJob job, string serverId,
            SettingsStore settings, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            settings.UpdateServer(serverId, profile =>
            {
                var index = profile.Schedule.FindIndex(j => j.Id == id);
                if (index < 0)
                {
                    throw new KeyNotFoundException($"Задача не найдена: {id}");
                }

                var updated = Json.Clone(job);
                updated.Id = id;
                profile.Schedule[index] = updated;
            });

            registry.Sync();
            audit.Write(Actor(ctx), "schedule.update", id, null);
            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/servers/{serverId}/schedule/{id}", (string id, string serverId, SettingsStore settings,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            settings.UpdateServer(serverId, profile => profile.Schedule.RemoveAll(j => j.Id == id));
            registry.Sync();
            audit.Write(Actor(ctx), "schedule.delete", id, null);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/servers/{serverId}/schedule/{id}/run", async (string id, string serverId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var job = runtime.Profile.Schedule.FirstOrDefault(j => j.Id == id)
                      ?? throw new KeyNotFoundException($"Задача не найдена: {id}");

            var execution = await runtime.Schedule.RunAsync(job, Actor(ctx), ctx.RequestAborted);
            audit.Write(Actor(ctx), "schedule.run", job.Name, null);
            return Results.Ok(execution);
        });

        // -------------------------------------------------------------- трекер

        api.MapGet("/servers/{serverId}/tracker/config", (string serverId, ServerRegistry registry, TrackerService tracker) =>
        {
            var runtime = Require(registry, serverId);
            var options = runtime.Profile.Tracker;
            var mod = ModConfigWriter.Describe(runtime.Profile);
            var status = ModStatusService.Describe(runtime.Profile,
                runtime.Tracker.LastIngest != DateTimeOffset.MinValue);

            return Results.Ok(new
            {
                enabled = options.Enabled,
                serverId = options.ServerId,
                staleSeconds = options.StaleSeconds,
                retentionDays = options.RetentionDays,
                readAdminLogs = options.ReadAdminLogs,
                autoConfigureMod = options.AutoConfigureMod,
                tokenMasked = Mask(options.ApiToken),
                hasToken = !string.IsNullOrEmpty(options.ApiToken),
                connected = runtime.Tracker.IsConnected(options.StaleSeconds),
                lastIngest = runtime.Tracker.LastIngest == DateTimeOffset.MinValue ? (DateTimeOffset?)null : runtime.Tracker.LastIngest,
                modConfigPath = mod.Path,
                modConfigExists = mod.Exists,
                // Установлен ли мод: определяет, показывать ли карточку связи.
                modInstalled = status.Installed,
                modSource = status.Source
            });
        });

        api.MapPost("/servers/{serverId}/tracker/config", (TrackerConfigRequest request, string serverId,
            SettingsStore settings, ServerRegistry registry, TrackerService tracker, AuditLog audit, HttpContext ctx) =>
        {
            var rotate = string.Equals(request.Token, "__rotate__", StringComparison.Ordinal);

            settings.UpdateServer(serverId, profile =>
            {
                var t = profile.Tracker;
                if (request.Enabled is not null) t.Enabled = request.Enabled.Value;
                if (request.ServerId is { Length: > 0 }) t.ServerId = request.ServerId.Trim()[..Math.Min(48, request.ServerId.Trim().Length)];
                if (request.StaleSeconds is > 4 and < 3600) t.StaleSeconds = request.StaleSeconds.Value;
                if (request.RetentionDays is > 0 and <= 365) t.RetentionDays = request.RetentionDays.Value;
                if (request.ReadAdminLogs is not null) t.ReadAdminLogs = request.ReadAdminLogs.Value;
                if (request.AutoConfigureMod is not null) t.AutoConfigureMod = request.AutoConfigureMod.Value;
                if (rotate) t.ApiToken = TrackerService.NewToken();
            });

            registry.Sync();
            tracker.WriteModConfig(Require(registry, serverId));

            audit.Write(Actor(ctx), rotate ? "tracker.token.rotate" : "tracker.config", serverId, null);

            return Results.Ok(new
            {
                ok = true,
                token = rotate ? registry.Get(serverId)?.Profile.Tracker.ApiToken : null
            });
        });

        api.MapGet("/servers/{serverId}/tracker/players", (string serverId, bool? online, ServerRegistry registry,
            RconService rcon) =>
        {
            var runtime = Require(registry, serverId);
            var options = runtime.Profile.Tracker;
            var connected = runtime.Tracker.IsConnected(options.StaleSeconds);
            var players = runtime.Tracker.LivePlayers();

            var list = online == true ? players.Where(p => p.Alive).ToArray() : players.ToArray();

            // Пинг и IP есть только у RCON (мод их не отдаёт с адресом).
            // Сопоставляем по BI id (Guid в RCON) или по имени — как запасной вариант.
            var rconById = new Dictionary<string, RconPlayerSnapshot>(StringComparer.OrdinalIgnoreCase);
            var rconByName = new Dictionary<string, RconPlayerSnapshot>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var snapshot = rcon.RecentPlayers(serverId);
                foreach (var entry in snapshot)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Guid))
                    {
                        rconById[entry.Guid] = entry;
                    }

                    if (!string.IsNullOrWhiteSpace(entry.Name))
                    {
                        rconByName[entry.Name] = entry;
                    }
                }
            }
            catch
            {
                // RCON может быть недоступен — просто без пинга и IP.
            }

            return Results.Ok(new
            {
                connected,
                staleAfterMs = options.StaleSeconds * 1000,
                lastIngest = runtime.Tracker.LastIngest == DateTimeOffset.MinValue ? (DateTimeOffset?)null : runtime.Tracker.LastIngest,
                server = runtime.Tracker.ServerState(),
                count = list.Length,
                players = list.Select(p =>
                {
                    var rconInfo = rconById.GetValueOrDefault(p.Id) ?? rconByName.GetValueOrDefault(p.Name);
                    var ping = rconInfo?.Ping is > 0 ? rconInfo.Ping : p.Ping;

                    return new
                    {
                        id = p.Id,
                        plainId = p.PlainId,
                        name = p.Name,
                        alive = p.Alive,
                        health = Math.Round(p.Health, 1),
                        maxHealth = p.MaxHealth,
                        pos = new { x = Math.Round(p.X, 2), y = Math.Round(p.Y, 2), z = Math.Round(p.Z, 2) },
                        ageMs = (long)(DateTimeOffset.Now - p.UpdatedAt).TotalMilliseconds,
                        stale = (DateTimeOffset.Now - p.UpdatedAt).TotalSeconds > options.StaleSeconds,
                        mods = p.Mods,
                        stats = ParseStats(p.StatsJson),
                        ping,
                        // IP-адрес и номер в BattlEye приходят только от RCON.
                        ip = rconInfo?.Ip,
                        rconNumber = rconInfo?.Number,
                        // Метка наблюдения и заметка живут в записи игрока.
                        watch = runtime.Tracker.Player(p.Id)?.Watch ?? false,
                        note = runtime.Tracker.Player(p.Id)?.Note ?? ""
                    };
                })
            });
        });

        api.MapGet("/servers/{serverId}/tracker/players/{id}", (string id, string serverId, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            var player = runtime.Tracker.LivePlayer(id);
            var record = runtime.Tracker.Player(id);

            if (player is null && record is null)
            {
                return Results.NotFound(new { error = "Игрок не найден." });
            }

            return Results.Ok(new
            {
                live = player is null ? null : new
                {
                    player.Id, player.PlainId, player.Name, player.Alive, player.Health, player.MaxHealth,
                    pos = new { player.X, player.Y, player.Z },
                    mods = player.Mods,
                    stats = ParseStats(player.StatsJson),
                    updatedAt = player.UpdatedAt
                },
                record
            });
        });

        api.MapGet("/servers/{serverId}/tracker/players/{id}/track", (string id, string serverId,
            string? from, string? to, int? points, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            var now = DateTimeOffset.Now;
            var fromAt = ParseDate(from) ?? now.AddHours(-1);
            var toAt = ParseDate(to) ?? now;
            var samples = runtime.Tracker.Track(id, fromAt, toAt, Math.Clamp(points ?? 600, 20, 5000));

            return Results.Ok(new
            {
                id,
                from = fromAt,
                to = toAt,
                count = samples.Count,
                points = samples.Select(p => new { at = p.At, x = Math.Round(p.X, 1), z = Math.Round(p.Z, 1) })
            });
        });

        api.MapGet("/servers/{serverId}/tracker/players/{id}/events", (string id, string serverId,
            int? limit, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            var name = runtime.Tracker.LivePlayer(id)?.Name ?? runtime.Tracker.Player(id)?.Name ?? "";
            var events = runtime.Tracker.PlayerEvents(id, name, limit ?? 40);
            return Results.Ok(events);
        });

        api.MapGet("/servers/{serverId}/tracker/dashboard", (string serverId, ServerRegistry registry,
            SettingsStore settings) =>
        {            var runtime = Require(registry, serverId);
            var state = runtime.Tracker.ServerState();
            var status = runtime.Supervisor.Status;

            var online = runtime.Tracker.LivePlayers();
            var pings = online.Select(p => p.Ping).Where(p => p > 0).ToArray();

            // Слоты берём из serverDZ.cfg (maxPlayers).
            var slots = TrackerService.ReadMaxPlayers(runtime.ServerSettings) ?? 0;

            return Results.Ok(new
            {
                connected = runtime.Tracker.IsConnected(runtime.Profile.Tracker.StaleSeconds),
                running = runtime.Supervisor.IsRunning,

                players = online.Count,
                slots,
                fps = state?.Fps ?? 0,
                fpsAvg = state?.FpsAvg ?? 0,
                pingAvg = pings.Length > 0
                    ? (int)Math.Round(pings.Average())
                    : state?.PingAvg ?? 0,

                cpuPercent = status.CpuPercent,
                workingSetBytes = status.WorkingSetBytes,
                privateBytes = status.PrivateBytes,
                threadCount = status.ThreadCount,
                uptimeSeconds = status.Uptime?.TotalSeconds,
                startedAt = status.StartedAt,

                vehicles = state?.Vehicles ?? 0,
                infected = state?.Infected ?? 0,
                worldObjects = state?.WorldObjects ?? 0,

                online24h = runtime.Tracker.OnlineHistory()
                    .Select(p => new { at = p.At, players = p.Players, alive = p.Alive })
            });
        });

        api.MapGet("/servers/{serverId}/tracker/map", (string serverId, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            var state = runtime.Tracker.ServerState();
            return Results.Ok(new
            {
                connected = runtime.Tracker.IsConnected(runtime.Profile.Tracker.StaleSeconds),
                server = state,
                vehicles = state?.VehicleList ?? [],
                worldObjects = state?.WorldObjectList ?? []
            });
        });

        // ------------------------------------------------ тайлы карты (xam.nu)

        // Метаданные карты: имя, размер мира, доступность тайлов.
        api.MapGet("/servers/{serverId}/tracker/map/info", async (string serverId,
            ServerRegistry registry, DayZMapService maps, CancellationToken ct) =>
        {
            var runtime = Require(registry, serverId);

            var info = await maps.ResolveAsync(runtime.ServerSettings, runtime.Profile.Map, ct);
            if (info is null)
            {
                return Results.Ok(new
                {
                    available = false,
                    map = runtime.Profile.Map,
                    label = ServerRegistry.MapLabel(runtime.Profile.Map)
                });
            }

            return Results.Ok(new
            {
                available = true,
                map = info.Key,
                tilev = info.Tilev,
                label = ServerRegistry.MapLabel(info.Key),
                size = info.Size,
                maxZoom = 8,
                layers = new[] { "topographic", "satellite" },
                attribution = "XAM · xam.nu"
            });
        });

        // Прокси тайла: браузер берёт картинку со своего origin (строгий CSP),
        // а панель скачивает её у xam.nu и кэширует.
        // В пути есть ключ карты и версия тайлов: при смене карты адреса
        // меняются, и браузер не отдаёт кэш от прежней карты.
        api.MapGet("/servers/{serverId}/tracker/map/tiles/{map}/{tilev}/{layer}/{z:int}/{x:int}/{y:int}",
            async (string serverId, string map, string tilev, string layer, int z, int x, int y,
                ServerRegistry registry, DayZMapService maps, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);

            var info = await maps.ResolveAsync(runtime.ServerSettings, runtime.Profile.Map, ctx.RequestAborted);
            if (info is null)
            {
                return Results.NotFound();
            }

            // Адрес не должен подменять карту: иначе можно было бы подсунуть
            // один набор тайлов под именем другого.
            if (!string.Equals(map, info.Key, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(tilev, info.Tilev, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var bytes = await maps.GetTileAsync(info, layer, z, x, y, ctx.RequestAborted);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            // Тайлы неизменяемы — пусть браузер кэширует их надолго.
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(bytes, "image/webp", enableRangeProcessing: false);
        })
        .DisableRateLimiting(); // При перетаскивании загружается много тайлов за раз.

        api.MapGet("/servers/{serverId}/tracker/database", (string serverId, string? search, bool? watch, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            var players = runtime.Tracker.Players();

            if (!string.IsNullOrWhiteSpace(search))
            {
                players = players.Where(p =>
                    p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.PlainId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.Aliases.Any(a => a.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                    p.Note.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (watch == true)
            {
                players = players.Where(p => p.Watch).ToArray();
            }

            var online = runtime.Tracker.LivePlayers().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Results.Ok(players.Select(p => new
            {
                p.Id,
                p.PlainId,
                p.Name,
                p.Aliases,
                p.Sessions,
                playtimeSeconds = Math.Round(p.PlaytimeSeconds),
                p.Kills,
                p.Deaths,
                kd = p.Deaths == 0 ? p.Kills : Math.Round((double)p.Kills / p.Deaths, 2),
                p.Watch,
                p.Note,
                p.FirstSeen,
                p.LastSeen,
                p.TotalViolationPoints,
                online = online.Contains(p.Id)
            }));
        });

        api.MapPost("/servers/{serverId}/tracker/database/{id}/note", (string id, NoteRequest request, string serverId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            Require(registry, serverId).Tracker.SetNote(id, request.Note ?? "", request.Watch ?? false);
            audit.Write(Actor(ctx), "tracker.note", id, null);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/servers/{serverId}/tracker/kills", (string serverId, int? limit, ServerRegistry registry) =>
            Results.Ok(Require(registry, serverId).Tracker.Kills(Math.Clamp(limit ?? 50, 1, 500))));

        api.MapGet("/servers/{serverId}/tracker/chat", (string serverId, int? limit, ServerRegistry registry) =>
            Results.Ok(Require(registry, serverId).Tracker.Chat(Math.Clamp(limit ?? 100, 1, 500))));

        // Отправка сообщения всем игрокам. Основной путь — через мод
        // (команда broadcast → ванильный RPC), он работает и без BattlEye.
        // Откат — RCON (say -1), если мод недоступен, но RCON подключён.
        api.MapPost("/servers/{serverId}/tracker/chat", async (ChatSendRequest request, string serverId,
            ServerRegistry registry, RconService rcon, AuditLog audit, HttpContext ctx) =>
        {
            var text = (request.Text ?? "").Trim();
            if (text.Length == 0)
            {
                return Results.BadRequest(new { error = "Сообщение не может быть пустым." });
            }

            if (text.Length > 512)
            {
                return Results.BadRequest(new { error = "Сообщение слишком длинное (максимум 512 символов)." });
            }

            var runtime = Require(registry, serverId);
            var modConnected = runtime.Tracker.IsConnected(runtime.Profile.Tracker.StaleSeconds);
            var session = rcon.Session(serverId);

            var delivered = false;
            string? via = null;

            // 1) Через мод — предпочтительно.
            if (modConnected && runtime.Profile.Tracker.Enabled)
            {
                runtime.Commands.Enqueue("broadcast", new Dictionary<string, string> { ["text"] = text });
                delivered = true;
                via = "mod";
            }
            // 2) Через RCON, если мод недоступен, но RCON живой.
            else if (session.IsConnected)
            {
                try
                {
                    var safe = text.Replace("\"", "");
                    await session.SendAsync("say -1 " + safe, ctx.RequestAborted);
                    delivered = true;
                    via = "rcon";
                }
                catch (TimeoutException ex)
                {
                    return Results.BadRequest(new { error = ex.Message, code = "rcon_timeout" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message, code = "rcon_offline" });
                }
            }

            if (!delivered)
            {
                return Results.BadRequest(new
                {
                    error = "Нет связи ни с модом, ни с RCON. Запустите сервер и дождитесь подключения.",
                    code = "offline"
                });
            }

            // Сообщение сразу попадает в лог чата панели, чтобы не ждать записи в ADM.
            runtime.Tracker.AddChat([new ChatEvent
            {
                At = DateTimeOffset.Now,
                PlayerName = "Администрация",
                Channel = "admin",
                Text = text
            }]);

            audit.Write(Actor(ctx), "chat.broadcast", text, null);
            return Results.Ok(new { ok = true, via });
        });

        api.MapGet("/servers/{serverId}/tracker/violations", (string serverId, string? type, int? limit, ServerRegistry registry) =>
        {
            var items = Require(registry, serverId).Tracker.Violations(Math.Clamp(limit ?? 200, 1, 1000));
            if (!string.IsNullOrWhiteSpace(type))
            {
                items = items.Where(v => string.Equals(v.Type, type, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            return Results.Ok(items);
        });

        api.MapGet("/servers/{serverId}/tracker/commands", (string serverId, string? status, int? limit, ServerRegistry registry) =>
            Results.Ok(Require(registry, serverId).Commands.List(status, Math.Clamp(limit ?? 100, 1, 500))));

        // -------------------------------------------------------- мод DayZPulse
        // Наш мод раздаётся с сайта: проверка версии, скачивание, обновление.

        api.MapGet("/servers/{serverId}/panel-mod", async (string serverId, ServerRegistry registry,
            PanelModService panelMod, SettingsStore settings, CancellationToken ct) =>
        {
            var runtime = Require(registry, serverId);
            var profile = runtime.Profile;
            var manifest = await panelMod.FetchManifestAsync(ct);

            var installedHere = PanelModService.IsInstalled(profile);
            var version = PanelModService.InstalledVersion(profile);

            return Results.Ok(new
            {
                baseUrl = panelMod.BaseUrl,
                installed = installedHere,
                installedVersion = version,
                globalVersion = settings.Current.Steam.PanelModVersion,
                updatedAt = settings.Current.Steam.PanelModUpdatedAt,
                remoteVersion = manifest?.Version,
                remoteUpdated = manifest?.Updated,
                remoteSize = manifest?.Size,
                upToDate = installedHere && manifest is not null && version is not null &&
                           string.Equals(version, manifest.Version, StringComparison.OrdinalIgnoreCase),
                available = manifest is not null,
                busy = panelMod.Busy,
                progress = panelMod.Percent,
                phase = panelMod.Phase,
                running = runtime.Supervisor.IsRunning
            });
        });

        // Установка/обновление мода в конкретном сервере.
        api.MapPost("/servers/{serverId}/panel-mod/install", async (string serverId, ServerRegistry registry,
            PanelModService panelMod, ModManager mods, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var result = await panelMod.InstallAsync(runtime.Profile, ctx.RequestAborted);

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error ?? "Не удалось установить мод." });
            }

            // Подключаем @DayZPulse в параметры запуска.
            mods.RebuildLaunchMods(serverId);
            registry.Sync();

            audit.Write(Actor(ctx), "panelmod.install", runtime.Name, "версия " + (result.Version ?? "?"));
            return Results.Ok(new
            {
                ok = true,
                version = result.Version,
                installed = PanelModService.IsInstalled(runtime.Profile),
                restartRequired = runtime.Supervisor.IsRunning
            });
        });

        // Смена адреса сайта с модом.
        api.MapPut("/panel-mod/url", (PanelModUrlRequest request, SettingsStore settings,
            AuditLog audit, HttpContext ctx) =>
        {
            var url = (request.BaseUrl ?? "").Trim().TrimEnd('/');

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Адрес должен начинаться с http:// или https://" });
            }

            settings.Update(s => s.Steam.PanelModBaseUrl = url);
            audit.Write(Actor(ctx), "panelmod.url", url, null);
            return Results.Ok(new { ok = true, baseUrl = url });
        });

        // -------------------------------------------------------------- моды

        // Поиск модов в Steam Workshop по названию.
        api.MapGet("/servers/{serverId}/mods/search", async (string serverId, string? q,
            ServerRegistry registry, HttpContext ctx) =>
        {
            Require(registry, serverId);

            var query = (q ?? "").Trim();
            if (query.Length < 2)
            {
                return Results.Ok(new { items = Array.Empty<object>() });
            }

            var items = await WorkshopSearch.SearchAsync(query, ctx.RequestAborted);
            return Results.Ok(new { items });
        });

        // Список модов сервера + настройки модификации и статус Steam.
        api.MapGet("/servers/{serverId}/mods", (string serverId, ServerRegistry registry,
            ModManager mods, SettingsStore settings) =>
        {
            Require(registry, serverId);
            var profile = settings.FindServer(serverId)
                ?? throw new KeyNotFoundException($"Сервер не найден: {serverId}");

            return Results.Ok(new
            {
                items = profile.ModList.Select(m => new
                {
                    m.Id,
                    m.WorkshopId,
                    m.Name,
                    source = m.Source.ToString().ToLowerInvariant(),
                    m.Folder,
                    m.LocalPath,
                    serverOnly = m.ServerOnly,
                    m.AutoUpdate,
                    m.Enabled,
                    installed = !string.IsNullOrWhiteSpace(m.Folder) &&
                        Directory.Exists(ModManager.ModFolder(profile.Server, m.Folder!)),
                    m.InstalledAt,
                    m.UpdatedAt
                }),
                settings = profile.ModSettings,
                launchMods = profile.Server.Mods,
                launchServerMods = profile.Server.ServerMods,
                busy = mods.Busy,
                progress = mods.Percent,
                phase = mods.Phase,
                lastError = mods.LastError,
                steam = new
                {
                    loggedIn = settings.Current.Steam.LoggedIn,
                    username = settings.Current.Steam.Username
                }
            });
        });

        // Добавить мод: по ссылке/ID из Workshop или локальной папке.
        api.MapPost("/servers/{serverId}/mods", (ModAddRequest request, string serverId,
            ServerRegistry registry, SettingsStore settings, ModManager mods, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var profile = runtime.Profile;

            var name = (request.Name ?? "").Trim();

            if (string.Equals(request.Source, "local", StringComparison.OrdinalIgnoreCase))
            {
                // Локальная папка.
                if (string.IsNullOrWhiteSpace(request.Path) || !Directory.Exists(request.Path))
                {
                    return Results.BadRequest(new { error = "Папка мода не найдена." });
                }

                var local = new ServerMod
                {
                    Id = Guid.NewGuid().ToString("n")[..12],
                    Name = name.Length > 0 ? name : Path.GetFileName(request.Path.TrimEnd('\\', '/')),
                    Source = ModSource.Local,
                    ServerOnly = request.ServerOnly ?? false,
                    AutoUpdate = false,
                    Enabled = true
                };

                var result = mods.InstallLocal(profile, local, request.Path!);
                if (!result.Success)
                {
                    return Results.BadRequest(new { error = result.Error ?? "Не удалось подключить мод." });
                }

                // Наш собственный мод @DayZPulse всегда серверный: он не должен
                // попадать в клиентский список -mod= (он не нужен игрокам).
                if (ModStatusService.IsOwnModFolder(local.Folder ?? local.Name))
                {
                    local.ServerOnly = true;
                    local.Name = ModStatusService.PanelMod;

                    // @DayZPulse управляется отдельной карточкой «Связь с модом»,
                    // поэтому в общем списке модов его не дублируем.
                    settings.UpdateServer(serverId, p => p.ModList.RemoveAll(m => m.Id == local.Id));
                    mods.RebuildLaunchMods(serverId);
                    registry.Sync();
                    audit.Write(Actor(ctx), "mod.add.local.panel", local.Name, null);
                    return Results.Ok(new { ok = true, id = local.Id, panelMod = true });
                }

                settings.UpdateServer(serverId, p => p.ModList.Add(local));
                mods.RebuildLaunchMods(serverId);
            registry.Sync();
                audit.Write(Actor(ctx), "mod.add.local", local.Name, null);
                return Results.Ok(new { ok = true, id = local.Id });
            }

            // Workshop: ссылка или ID.
            var workshopId = ModManager.ExtractWorkshopId(request.Workshop ?? request.Path);
            if (workshopId is null)
            {
                return Results.BadRequest(new { error = "Не удалось определить Workshop ID из ссылки." });
            }

            if (profile.ModList.Any(m => m.WorkshopId == workshopId))
            {
                return Results.BadRequest(new { error = "Этот мод уже в списке." });
            }

            var mod = new ServerMod
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                WorkshopId = workshopId,
                Name = name.Length > 0 ? name : "Мод " + workshopId,
                Source = ModSource.Workshop,
                ServerOnly = request.ServerOnly ?? false,
                AutoUpdate = true,
                Enabled = true
            };

            settings.UpdateServer(serverId, p => p.ModList.Add(mod));
            audit.Write(Actor(ctx), "mod.add.workshop", workshopId, null);
            return Results.Ok(new { ok = true, id = mod.Id });
        });

        // Изменить мод (вкл/выкл, автообновление, имя, папку).
        api.MapPut("/servers/{serverId}/mods/{id}", (string serverId, string id, ModUpdateRequest request,
            ServerRegistry registry, SettingsStore settings, ModManager mods, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);

            settings.UpdateServer(serverId, profile =>
            {
                var mod = profile.ModList.FirstOrDefault(m => m.Id == id);
                if (mod is null) return;

                if (request.Enabled is not null) mod.Enabled = request.Enabled.Value;
                if (request.AutoUpdate is not null) mod.AutoUpdate = request.AutoUpdate.Value;
                if (request.ServerOnly is not null) mod.ServerOnly = request.ServerOnly.Value;
                if (!string.IsNullOrWhiteSpace(request.Name)) mod.Name = request.Name!.Trim();
                if (!string.IsNullOrWhiteSpace(request.Folder)) mod.Folder = request.Folder!.Trim();
            });

            mods.RebuildLaunchMods(serverId);
            registry.Sync();
            audit.Write(Actor(ctx), "mod.update", id, null);
            return Results.Ok(new { ok = true });
        });

        // Удалить мод из списка (и папку с диска — по флагу).
        api.MapDelete("/servers/{serverId}/mods/{id}", async (string serverId, string id, bool? deleteFiles,
            ServerRegistry registry, SettingsStore settings, ModManager mods, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var mod = runtime.Profile.ModList.FirstOrDefault(m => m.Id == id);

            if (mod is null)
            {
                return Results.NotFound(new { error = "Мод не найден." });
            }

            if (deleteFiles == true)
            {
                mods.RemoveFolder(runtime.Profile, mod);
            }

            settings.UpdateServer(serverId, p => p.ModList.RemoveAll(m => m.Id == id));
            mods.RebuildLaunchMods(serverId);
            registry.Sync();
            audit.Write(Actor(ctx), "mod.delete", mod.Name, null);
            return Results.Ok(new { ok = true });
        });

        // Установить/обновить все моды из Workshop, отмеченные автообновлением.
        api.MapPost("/servers/{serverId}/mods/install", async (string serverId, ModInstallRequest? request,
            ServerRegistry registry, SettingsStore settings, ModManager mods, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var profile = runtime.Profile;

            // Список id: либо указанные, либо все Workshop-моды с автообновлением.
            var targets = profile.ModList
                .Where(m => m.Source == ModSource.Workshop)
                .Where(m => request?.Ids is { Count: > 0 } ? request.Ids.Contains(m.Id) : m.AutoUpdate)
                .ToArray();

            if (targets.Length == 0)
            {
                return Results.Ok(new { ok = true, installed = 0, note = "Нет модов для установки." });
            }

            if (mods.Busy)
            {
                return Results.BadRequest(new { error = "Другая загрузка мода уже идёт." });
            }

            var results = new List<object>();
            var installed = 0;

            foreach (var mod in targets)
            {
                var result = await mods.InstallFromWorkshopAsync(profile, mod, ctx.RequestAborted);

                if (result.Success)
                {
                    settings.UpdateServer(serverId, p =>
                    {
                        var record = p.ModList.FirstOrDefault(m => m.Id == mod.Id);
                        if (record is not null)
                        {
                            record.Folder = mod.Folder;
                            record.InstalledAt = mod.InstalledAt;
                            record.UpdatedAt = mod.UpdatedAt;
                        }
                    });
                    installed++;
                }

                results.Add(new { id = mod.Id, name = mod.Name, ok = result.Success, error = result.Error });
            }

            mods.RebuildLaunchMods(serverId);
            registry.Sync();
            audit.Write(Actor(ctx), "mod.install", $"{installed}/{targets.Length}", null);

            return Results.Ok(new { ok = true, installed, total = targets.Length, results });
        });

        // Общие настройки модов сервера.
        api.MapPut("/servers/{serverId}/mods/settings", (ModSettings request, string serverId,
            ServerRegistry registry, SettingsStore settings, HttpContext ctx) =>
        {
            Require(registry, serverId);

            settings.UpdateServer(serverId, p => p.ModSettings = request);
            return Results.Ok(new { ok = true });
        });

        // -------------------------------------------------- стартовые наборы

        // Стартовые наборы (общий/мужской/женский) и пресеты выдачи.
        api.MapGet("/servers/{serverId}/loadouts", (string serverId, ServerRegistry registry,
            SettingsStore settings, LoadoutService loadouts) =>
        {
            Require(registry, serverId);
            var profile = settings.FindServer(serverId)
                ?? throw new KeyNotFoundException($"Сервер не найден: {serverId}");

            var now = DateTimeOffset.Now;

            return Results.Ok(new
            {
                spawn = new
                {
                    common = profile.SpawnKits.Common,
                    male = profile.SpawnKits.Male,
                    female = profile.SpawnKits.Female
                },
                presets = profile.GrantPresets.Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Items,
                    p.Players,
                    p.ExpiresAt,
                    p.Enabled,
                    p.CreatedAt,
                    active = p.Enabled && (p.ExpiresAt is null || p.ExpiresAt > now)
                }),
                directory = LoadoutService.ConfigDirectory(profile)
            });
        });

        // Сохранить стартовые наборы.
        api.MapPut("/servers/{serverId}/loadouts/spawn", (SpawnKits request, string serverId,
            SettingsStore settings, ServerRegistry registry, LoadoutService loadouts,
            AuditLog audit, HttpContext ctx) =>
        {
            Require(registry, serverId);

            settings.UpdateServer(serverId, p =>
            {
                request.Common ??= new Loadout { Name = "Общий" };
                p.SpawnKits = request;
            });

            registry.Sync();
            var profile = settings.FindServer(serverId);
            if (profile is not null) loadouts.WriteSpawnGear(profile);

            audit.Write(Actor(ctx), "loadout.spawn", serverId, null);
            return Results.Ok(new { ok = true });
        });

        // Создать/обновить пресет выдачи.
        api.MapPost("/servers/{serverId}/loadouts/presets", (GrantPreset request, string serverId,
            SettingsStore settings, ServerRegistry registry, LoadoutService loadouts,
            AuditLog audit, HttpContext ctx) =>
        {
            Require(registry, serverId);

            var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("n")[..12] : request.Id;

            settings.UpdateServer(serverId, p =>
            {
                var existing = p.GrantPresets.FirstOrDefault(x => x.Id == id);
                if (existing is null)
                {
                    request.Id = id;
                    request.CreatedAt = DateTimeOffset.Now;
                    p.GrantPresets.Add(request);
                }
                else
                {
                    existing.Name = request.Name;
                    existing.Items = request.Items;
                    existing.Players = request.Players;
                    existing.ExpiresAt = request.ExpiresAt;
                    existing.Enabled = request.Enabled;
                }
            });

            registry.Sync();
            var profile = settings.FindServer(serverId);
            if (profile is not null) loadouts.SyncAll(profile);

            audit.Write(Actor(ctx), "loadout.preset.save", request.Name, null);
            return Results.Ok(new { ok = true, id });
        });

        // Удалить пресет.
        api.MapDelete("/servers/{serverId}/loadouts/presets/{id}", (string serverId, string id,
            SettingsStore settings, ServerRegistry registry, LoadoutService loadouts,
            AuditLog audit, HttpContext ctx) =>
        {
            Require(registry, serverId);

            settings.UpdateServer(serverId, p => p.GrantPresets.RemoveAll(x => x.Id == id));
            registry.Sync();
            var profile = settings.FindServer(serverId);
            if (profile is not null) loadouts.SyncAll(profile);

            audit.Write(Actor(ctx), "loadout.preset.delete", id, null);
            return Results.Ok(new { ok = true });
        });

        // ------------------------------------------------------------ логи

        // Список логов сервера (RPT, ADM, script_*, error_*).
        api.MapGet("/servers/{serverId}/logs", (string serverId, ServerRegistry registry,
            ServerLogService logs) =>
        {
            var runtime = Require(registry, serverId);
            var files = logs.List(runtime.Profile);

            return Results.Ok(new
            {
                directory = ServerLogService.ProfilesDirectory(runtime.Profile),
                files = files.Select(f => new
                {
                    f.Name,
                    f.Kind,
                    f.SizeBytes,
                    f.Modified
                }),
                settings = runtime.Profile.Backups.Logs,
                backupDirectory = runtime.Backups.LogsDirectory,
                backups = runtime.Backups.ListLogs()
            });
        });

        // Чтение лога: последние строки + фильтр.
        api.MapGet("/servers/{serverId}/logs/{file}", (string serverId, string file, int? tail,
            string? filter, ServerRegistry registry, ServerLogService logs) =>
        {
            var runtime = Require(registry, serverId);
            var lines = logs.Read(runtime.Profile, file, tail ?? 500, filter);

            return Results.Ok(new { file, count = lines.Count, lines });
        });

        // Удаление лога (одиночного или всех старше N дней).
        api.MapDelete("/servers/{serverId}/logs", (string serverId, string? file, int? olderThanDays,
            ServerRegistry registry, ServerLogService logs, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var removed = logs.Delete(runtime.Profile,
                string.IsNullOrWhiteSpace(file) ? null : file,
                olderThanDays ?? 0);

            audit.Write(Actor(ctx), "logs.delete", string.IsNullOrEmpty(file) ? $"старше {olderThanDays} дн." : file, null);
            return Results.Ok(new { ok = true, removed });
        });

        // Скачать лог или архив логов.
        api.MapGet("/servers/{serverId}/logs/{file}/download", (string serverId, string file,
            ServerRegistry registry, ServerLogService logs) =>
        {
            var runtime = Require(registry, serverId);
            var path = logs.Resolve(runtime.Profile, file);

            return path is not null && File.Exists(path)
                ? Results.File(path, "text/plain", file)
                : Results.NotFound();
        });

        // Создать резервную копию логов.
        api.MapPost("/servers/{serverId}/logs/backup", async (string serverId, ServerRegistry registry,
            AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var result = await runtime.Backups.CreateLogBackupAsync(ctx.RequestAborted);

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Message });
            }

            audit.Write(Actor(ctx), "logs.backup", result.FileName, null);
            return Results.Ok(result);
        });

        // Настройки бэкапов и хранения логов.
        api.MapPut("/servers/{serverId}/logs/settings", (LogBackupSettings request, string serverId,
            ServerRegistry registry, SettingsStore settings, AuditLog audit, HttpContext ctx) =>
        {
            Require(registry, serverId);

            settings.UpdateServer(serverId, p => p.Backups.Logs = request);
            registry.Sync();
            audit.Write(Actor(ctx), "logs.settings", null, null);
            return Results.Ok(new { ok = true });
        });

        // -------------------------------------------------- инвентарь игрока

        // Каталог предметов сервера (из db/types.xml): класс + категория.
        // Нужен палитре выдачи — чтобы предлагать только совместимое.
        api.MapGet("/servers/{serverId}/tracker/catalog", (string serverId,
            ServerRegistry registry, ItemCatalog catalog) =>
        {
            var runtime = Require(registry, serverId);
            var items = catalog.ForServer(runtime.ServerSettings, runtime.Profile.Server.ProfilesDirectory);

            return Results.Ok(new
            {
                count = items.Count,
                items = items.Select(i => new
                {
                    cls = i.ClassName,
                    group = i.Category,
                    label = i.Label,
                    icon = i.Icon
                })
            });
        });

        // Последний снимок инвентаря (мод мог ещё не прислать).
        api.MapGet("/servers/{serverId}/tracker/inventory/{playerId}", (string serverId, string playerId,
            ServerRegistry registry) =>
        {
            var inventory = Require(registry, serverId).Tracker.Inventory(playerId);
            if (inventory is null)
            {
                return Results.Ok(new { available = false });
            }

            return Results.Ok(new
            {
                available = true,
                playerId = inventory.PlayerId,
                playerName = inventory.PlayerName,
                at = inventory.At,
                items = JsonSerializer.Deserialize<JsonElement>(inventory.Json)
            });
        });

        // Запрос свежего снимка инвентаря: мод соберёт и пришлёт на /tracker/inventory.
        api.MapPost("/servers/{serverId}/tracker/inventory/{playerId}/refresh", (string serverId, string playerId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);

            if (!runtime.Commands.AllowedActions.Contains("inventory"))
            {
                return Results.BadRequest(new { error = "Команда инвентаря недоступна." });
            }

            runtime.Commands.Enqueue("inventory", new Dictionary<string, string> { ["id"] = playerId });
            audit.Write(Actor(ctx), "tracker.inventory.refresh", playerId, null);
            return Results.Ok(new { ok = true });
        });

        // ------------------------------------------------ действия по игроку
        // Кик и бан идут через RCON: работают даже без мода, если известен номер
        // игрока в списке BattlEye (совпадает с номером от команды players).

        api.MapPost("/servers/{serverId}/tracker/players/{playerId}/kick", async (string serverId, string playerId,
            PlayerActionRequest request, ServerRegistry registry, RconService rcon, AuditLog audit, HttpContext ctx) =>
        {
            return await RconPlayerAction(serverId, playerId, "kick", request, registry, rcon, audit, ctx);
        });

        api.MapPost("/servers/{serverId}/tracker/players/{playerId}/ban", async (string serverId, string playerId,
            PlayerActionRequest request, ServerRegistry registry, RconService rcon, AuditLog audit, HttpContext ctx) =>
        {
            return await RconPlayerAction(serverId, playerId, "ban", request, registry, rcon, audit, ctx);
        });

        // -------------------------------------------------- демонстрационный игрок

        api.MapPost("/servers/{serverId}/tracker/demo", (string serverId, DemoRequest request,
            SettingsStore settings, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);

            // Флаг сохраняем в настройках, чтобы демо-игрок переживал перезапуск панели.
            settings.UpdateServer(serverId, profile => profile.Tracker.DemoPlayer = request.Enabled);

            runtime.Tracker.SetDemoPlayer(request.Enabled, DateTimeOffset.Now);
            audit.Write(Actor(ctx), "tracker.demo", request.Enabled ? "on" : "off", null);
            return Results.Ok(new { ok = true, enabled = request.Enabled });
        });

        api.MapPost("/servers/{serverId}/tracker/commands", (JsonElement body, string serverId,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var parsed = TrackerProtocol.ParseCommandInput(body);

            if (parsed is null || parsed.Value.Commands.Count == 0)
            {
                return Results.BadRequest(new { error = "Не указана команда." });
            }

            var (commands, group) = parsed.Value;
            var created = new List<object>();

            foreach (var (action, args) in commands)
            {
                if (!runtime.Commands.AllowedActions.Contains(action))
                {
                    return Results.BadRequest(new { error = $"Неизвестное действие: {action}" });
                }

                var entry = runtime.Commands.Enqueue(action, args, group);
                created.Add(new { entry.Id, entry.Action, entry.Status, entry.Group });
            }

            audit.Write(Actor(ctx), "tracker.command", string.Join(",", commands.Select(c => c.Action)), null);
            return Results.Ok(new
            {
                ok = true,
                created,
                connected = runtime.Tracker.IsConnected(runtime.Profile.Tracker.StaleSeconds)
            });
        });

        api.MapDelete("/servers/{serverId}/tracker/commands/{id:long}", (long id, string serverId, ServerRegistry registry) =>
            Require(registry, serverId).Commands.Cancel(id)
                ? Results.Ok(new { ok = true })
                : Results.BadRequest(new { error = "Команда не найдена или уже выдана." }));

        api.MapPost("/servers/{serverId}/tracker/commands/clear", (string serverId, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            Require(registry, serverId).Commands.Clear();
            audit.Write(Actor(ctx), "tracker.commands.clear", serverId, null);
            return Results.Ok(new { ok = true });
        });
    }

    // --------------------------------------------------------------- хелперы

    private static ServerRuntime Require(ServerRegistry registry, string serverId) =>
        registry.Get(serverId) ?? throw new KeyNotFoundException($"Сервер не найден: {serverId}");

    /// <summary>
    /// Кик или бан через RCON. Номер игрока в BattlEye берём либо из запроса,
    /// либо находим по его GUID/имени в списке <c>players</c>.
    /// </summary>
    private static async Task<IResult> RconPlayerAction(string serverId, string playerId, string action,
        PlayerActionRequest request, ServerRegistry registry, RconService rcon, AuditLog audit, HttpContext ctx)
    {
        var runtime = Require(registry, serverId);
        var session = rcon.Session(serverId);

        if (!session.IsConnected)
        {
            return Results.BadRequest(new
            {
                error = session.LastError ?? "RCON не подключён. Запустите сервер и дождитесь подключения.",
                code = "rcon_offline"
            });
        }

        // Игрока ищем в списке RCON по GUID (BI id совпадает с Guid BattlEye)
        // или по имени — данные трекера и RCON связаны именно так.
        int? number = request.Number;

        if (number is null or <= 0)
        {
            try
            {
                var players = await session.PlayersAsync(ctx.RequestAborted);
                var live = runtime.Tracker.LivePlayer(playerId);
                var match = players.FirstOrDefault(p =>
                    (!string.IsNullOrEmpty(live?.Id) && string.Equals(p.Guid, live.Id, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(live?.PlainId) && string.Equals(p.Guid, live.PlainId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(live?.Name) && string.Equals(p.Name, live.Name, StringComparison.OrdinalIgnoreCase)));

                number = match?.Number;
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message, code = "rcon_offline" });
            }
        }

        if (number is null or <= 0)
        {
            return Results.BadRequest(new
            {
                error = "Игрок не найден в списке RCON — возможно, он уже вышел.",
                code = "player_not_found"
            });
        }

        // «kick <#> [причина]» и «ban <#> <минуты> [причина]».
        var reason = (request.Reason ?? "").Replace("\"", "").Trim();
        var command = action switch
        {
            "ban" => $"ban {number} {Math.Clamp(request.Minutes ?? 0, 0, 100000)} {reason}".Trim(),
            _ => $"kick {number} {reason}".Trim()
        };

        try
        {
            var response = await session.SendAsync(command, ctx.RequestAborted);
            audit.Write(Actor(ctx), $"player.{action}", $"{playerId} ({command})", null);
            return Results.Ok(new { ok = true, number, response });
        }
        catch (TimeoutException ex)
        {
            return Results.BadRequest(new { error = ex.Message, code = "rcon_timeout" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message, code = "rcon_offline" });
        }
    }

    private static object? ParseStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return long.TryParse(value, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : null;
    }

    private static string Mask(string token) =>
        string.IsNullOrEmpty(token) ? "" : token.Length <= 8 ? new string('•', token.Length) : token[..4] + "…" + token[^4..];

    private static string Actor(HttpContext ctx) =>
        ctx.Items.TryGetValue("actor", out var value) && value is string actor ? actor : "неизвестно";

    public sealed record CommandRequest(string Command);
    public sealed record ConfigSaveRequest(string Path, string? Content);

    public sealed record QuickWeatherRequest(WeatherSettings Weather);
    public sealed record RestoreRequest(string FileName);
    public sealed record NoteRequest(string? Note, bool? Watch);
    public sealed record ChatSendRequest(string? Text);
    public sealed record PlayerActionRequest(int? Number, string? Reason, int? Minutes);
    public sealed record DemoRequest(bool Enabled);

    public sealed record ModAddRequest(string? Source, string? Workshop, string? Path, string? Name, bool? ServerOnly);
    public sealed record ModUpdateRequest(bool? Enabled, bool? AutoUpdate, string? Name, string? Folder, bool? ServerOnly);
    public sealed record ModInstallRequest(List<string>? Ids);
    public sealed record PanelModUrlRequest(string? BaseUrl);

    public sealed record TrackerConfigRequest(
        bool? Enabled, string? ServerId, int? StaleSeconds, int? RetentionDays,
        bool? ReadAdminLogs, bool? AutoConfigureMod, string? Token);
}

/// <summary>Преобразование состояния сервера в ответ API.</summary>
public static class ServerDtos
{
    public static object Status(ServerRuntime runtime)
    {
        var status = runtime.Supervisor.Status;
        var tracker = runtime.Profile.Tracker;

        return new
        {
            id = runtime.Id,
            name = runtime.Name,
            color = runtime.Profile.Color,
            status.State,
            stateText = StatusText(status.State),
            status.ProcessId,
            status.StartedAt,
            status.StoppedAt,
            status.ExitCode,
            status.LastError,
            status.LastMessage,
            status.AutoRestartArmed,
            status.AutoRestartsInWindow,
            status.CpuPercent,
            status.WorkingSetBytes,
            status.PrivateBytes,
            status.ThreadCount,
            status.LastSampleAt,
            uptimeSeconds = status.Uptime?.TotalSeconds,
            running = runtime.Supervisor.IsRunning,
            consoleLines = runtime.Console.Count,
            launchCommand = runtime.Supervisor.LastLaunchCommandLine,
            workingDirectory = ServerPaths.WorkingDirectory(runtime.ServerSettings),
            executablePath = SafeExecutable(runtime.ServerSettings),
            port = runtime.ServerSettings.Port,
            trackerConnected = runtime.Tracker.IsConnected(tracker.StaleSeconds),
            players = runtime.Tracker.LivePlayers().Count,
            playersAlive = runtime.Tracker.LivePlayers().Count(p => p.Alive)
        };
    }

    public static string StatusText(ServerState state) => state switch
    {
        ServerState.Stopped => "Остановлен",
        ServerState.Starting => "Запускается",
        ServerState.Running => "Работает",
        ServerState.Stopping => "Останавливается",
        ServerState.Restarting => "Перезапускается",
        ServerState.Crashed => "Аварийно завершился",
        ServerState.Failed => "Ошибка запуска",
        _ => state.ToString()
    };

    private static string? SafeExecutable(Models.ServerSettings options)
    {
        try
        {
            return ServerPaths.Executable(options);
        }
        catch
        {
            return options.ExecutablePath;
        }
    }
}
