using DayZPulse.Core;
using DayZPulse.Hosting;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Security;
using DayZPulse.Service;
using DayZPulse.Steam;
using DayZPulse.SystemIntegration;
using DayZPulse.Tracker;

namespace DayZPulse.Web;

/// <summary>Управление списком серверов, общие настройки, служба и система.</summary>
public static class ApiEndpoints
{
    /// <summary>Кэш размеров папок серверов (пересчёт не чаще раза в 5 минут).</summary>
    private static readonly Dictionary<string, (long Bytes, DateTimeOffset At)> FolderSizeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void MapDayZPulseApi(this WebApplication app)
    {
        app.MapAuthEndpoints();

        var api = app.MapGroup("/api").RequireRateLimiting("api");

        // ------------------------------------------------------- список серверов

        api.MapPost("/servers", (ServerCreateRequest request, SettingsStore settings, ServerRegistry registry,
            TrackerService tracker, AuditLog audit, HttpContext ctx) =>
        {
            var name = (request.Name ?? "").Trim();
            if (name.Length is < 1 or > 64)
            {
                return Results.BadRequest(new { error = "Название сервера: от 1 до 64 символов." });
            }

            var id = ServerProfile.Slugify(string.IsNullOrWhiteSpace(request.Id) ? name : request.Id!);
            if (settings.Current.Servers.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new { error = $"Сервер с идентификатором «{id}» уже есть." });
            }

            var port = request.Port ?? NextFreePort(settings.Current);

            // Папка обязательна: в ней лежит сервер, и оттуда берутся конфиги.
            var directory = (request.Directory ?? "").Trim();
            if (directory.Length < 3)
            {
                var fallbackRoot = (settings.Current.DefaultServerDirectory ?? "").Trim();

                if (fallbackRoot.Length < 3)
                {
                    return Results.BadRequest(new
                    {
                        error = "Укажите папку сервера DayZ.",
                        code = "bad_directory"
                    });
                }

                // Для второго и следующих серверов добавляем подкаталог с именем сервера.
                directory = settings.Current.Servers.Count == 0
                    ? fallbackRoot
                    : Path.Combine(fallbackRoot, id);
            }

            var profile = new ServerProfile
            {
                Id = id,
                Name = name,
                Color = NormalizeColor(request.Color),
                Map = NormalizeMap(request.Map),
                Server = new ServerSettings
                {
                    Port = port,
                    BaseDirectory = directory
                },
                Tracker = new TrackerSettings { ServerId = id, ApiToken = TrackerService.NewToken() }
            };

            settings.Update(s => s.Servers.Add(profile));
            registry.Sync();

            // Сразу готовим служебные каталоги сервера и SteamCMD.
            ServerPaths.EnsureDataDirectories(profile.Server);
            tracker.WriteModConfig(Require(registry, id));

            audit.Write(Actor(ctx), "server.create", $"{name} ({id})", null);
            return Results.Ok(new { ok = true, id, port });
        });

        api.MapDelete("/servers/{serverId}", async (string serverId, bool? deleteFiles, SettingsStore settings,
            ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            if (settings.Current.Servers.Count <= 1)
            {
                return Results.BadRequest(new { error = "Нельзя удалить единственный сервер." });
            }

            var runtime = Require(registry, serverId);
            if (runtime.Supervisor.IsRunning)
            {
                return Results.BadRequest(new { error = "Остановите сервер перед удалением." });
            }

            var directory = ServerPaths.BaseDirectory(runtime.ServerSettings);
            var removeFiles = deleteFiles == true;

            // Сначала убираем сервер из реестра: это закрывает файл журнала,
            // иначе Windows не даст удалить папку сервера.
            settings.Update(s =>
            {
                s.Servers.RemoveAll(p => string.Equals(p.Id, serverId, StringComparison.OrdinalIgnoreCase));
                if (string.Equals(s.DefaultServerId, serverId, StringComparison.OrdinalIgnoreCase))
                {
                    s.DefaultServerId = s.Servers.FirstOrDefault()?.Id;
                }
            });

            registry.Remove(serverId);
            registry.Sync();

            // Папку удаляем после снятия сервера с учёта, чтобы файлы были свободны.
            if (removeFiles && directory.Length >= 3 && Directory.Exists(directory))
            {
                try
                {
                    DeleteDirectoryWithRetry(directory, attempts: 6);
                }
                catch (Exception ex)
                {
                    audit.Write(Actor(ctx), "server.delete", $"{serverId} · папка не удалена: {ex.Message}", null);
                    return Results.Ok(new
                    {
                        ok = true,
                        filesRemoved = false,
                        directory,
                        warning = $"Сервер удалён из панели, но папку удалить не удалось: {ex.Message}"
                    });
                }
            }

            audit.Write(Actor(ctx), "server.delete", removeFiles ? $"{serverId} · файлы удалены" : $"{serverId} · только из панели", null);
            return Results.Ok(new { ok = true, filesRemoved = removeFiles, directory });
        });

        // ------------------------------------------------- настройки одного сервера

        // Параметры запуска сервера (карточка в «Конфигурации»).
        api.MapGet("/servers/{serverId}/launch", (string serverId, ServerRegistry registry) =>
        {
            var runtime = Require(registry, serverId);
            var server = runtime.ServerSettings;

            // Итоговую команду собираем на лету из текущих настроек,
            // а если сервер уже запускался — показываем фактическую.
            string preview;
            try
            {
                var working = ServerPaths.WorkingDirectory(server);
                var exe = SafeExecutable(server) ?? server.ExecutableName;
                preview = QuoteCommand(exe) + " " +
                          ServerSupervisor.BuildArguments(server, exe, working);
            }
            catch
            {
                preview = "";
            }

            return Results.Ok(new
            {
                arguments = server.Arguments ?? "",
                extraArguments = server.ExtraArguments ?? "",
                executableName = server.ExecutableName ?? "",
                configPath = SafeConfigPath(server),
                port = server.Port,
                mods = server.Mods ?? "",
                serverMods = server.ServerMods ?? "",
                launchCommand = runtime.Supervisor.LastLaunchCommandLine ?? preview.Trim(),
                preview = preview.Trim(),
                running = runtime.Supervisor.IsRunning
            });
        });

        api.MapPut("/servers/{serverId}/launch", (LaunchUpdateRequest request, string serverId,
            SettingsStore settings, ServerRegistry registry, AuditLog audit, HttpContext ctx) =>
        {
            var runtime = Require(registry, serverId);
            var running = runtime.Supervisor.IsRunning;

            settings.UpdateServer(serverId, profile =>
            {
                if (request.Arguments is not null) profile.Server.Arguments = request.Arguments.Trim();
                if (request.ExtraArguments is not null) profile.Server.ExtraArguments = request.ExtraArguments.Trim();
            });

            registry.Sync();
            audit.Write(Actor(ctx), "server.launch", running ? "отложено" : "записано", null);
            return Results.Ok(new { ok = true, pending = running });
        });

        api.MapGet("/servers/{serverId}/settings", (string serverId, ServerRegistry registry,
            SettingsStore settings) =>
        {
            var runtime = Require(registry, serverId);
            var profile = runtime.Profile;
            var server = profile.Server;

            return Results.Ok(new
            {
                profile = new { profile.Id, profile.Name, profile.Enabled, profile.Color },
                server,
                tracker = profile.Tracker,
                backups = profile.Backups,
                configFiles = profile.ConfigFiles,
                paths = new
                {
                    baseDirectory = ServerPaths.BaseDirectory(server),
                    workingDirectory = ServerPaths.WorkingDirectory(server),
                    executable = SafeExecutable(server),
                    profiles = ModConfigWriter.ProfilesRoot(profile),
                    steamCmd = SteamCmdService.ResolveDirectory(settings.Current.Steam.Directory),
                    // Данные сервера лежат внутри его папки.
                    serverData = ServerPaths.DataDirectory(server),
                    backups = runtime.Backups.Directory,
                    logs = ServerPaths.ServerLogsDirectory(server),
                    // Данные панели — рядом с панелью.
                    panelHome = AppPaths.BaseDirectory,
                    settingsFile = AppPaths.SettingsFile
                },
                encodings = Core.EncodingResolver.Supported,
                defaultBaseDirectory = ServerSettings.DefaultBaseDirectory
            });
        });

        api.MapPut("/servers/{serverId}/settings", (ServerSettingsUpdate request, string serverId, SettingsStore settings,
            ServerRegistry registry, TrackerService tracker, AuditLog audit, HttpContext ctx) =>
        {
            settings.UpdateServer(serverId, profile =>
            {
                if (request.Profile is not null)
                {
                    if (!string.IsNullOrWhiteSpace(request.Profile.Name)) profile.Name = request.Profile.Name!.Trim();
                    if (request.Profile.Enabled is not null) profile.Enabled = request.Profile.Enabled.Value;
                    if (request.Profile.Color is not null) profile.Color = NormalizeColor(request.Profile.Color);
                }

                if (request.Server is not null) profile.Server = request.Server;
                if (request.Tracker is not null) profile.Tracker = request.Tracker;
                if (request.Backups is not null) profile.Backups = request.Backups;
                if (request.ConfigFiles is not null) profile.ConfigFiles = request.ConfigFiles;
            });

            registry.Sync();
            tracker.WriteModConfig(Require(registry, serverId));

            audit.Write(Actor(ctx), "server.settings", serverId, null);
            return Results.Ok(new { ok = true });
        });

        // ---------------------------------------------------------- общие настройки

        api.MapGet("/settings", (SettingsStore settings, ServerRegistry registry) =>
        {
            var snapshot = settings.Snapshot();
            var servers = registry.All.Select(rt => new
            {
                id = rt.Id,
                name = rt.Name,
                color = rt.Profile.Color,
                enabled = rt.Profile.Enabled,
                port = rt.ServerSettings.Port,
                directory = ServerPaths.WorkingDirectory(rt.ServerSettings),
                trackerToken = Mask(rt.Profile.Tracker.ApiToken)
            }).ToArray();

            return Results.Ok(new
            {
                web = snapshot.Web,
                auth = new { snapshot.Auth.Enabled, snapshot.Auth.Username, snapshot.Auth.SessionHours, snapshot.Auth.OwnerName, snapshot.Auth.OwnerCreatedAt },
                logging = snapshot.Logging,
                defaultServerId = snapshot.DefaultServerId,
                servers,
                paths = new
                {
                    home = AppPaths.BaseDirectory,
                    data = AppPaths.DataRoot,
                    settingsFile = AppPaths.SettingsFile,
                    logs = AppPaths.LogsDirectory,
                    executable = Environment.ProcessPath
                }
            });
        });

        api.MapPut("/settings", (GlobalSettingsUpdate request, SettingsStore settings, AuditLog audit, HttpContext ctx) =>
        {
            settings.Update(current =>
            {
                if (request.Web is not null) current.Web = request.Web;
                if (request.Logging is not null) current.Logging = request.Logging;
                if (request.Auth is not null)
                {
                    current.Auth.Enabled = request.Auth.Enabled;
                    current.Auth.SessionHours = request.Auth.SessionHours;
                    if (!string.IsNullOrWhiteSpace(request.Auth.Username))
                    {
                        current.Auth.Username = request.Auth.Username.Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(request.DefaultServerId))
                {
                    current.DefaultServerId = request.DefaultServerId;
                }
            });

            audit.Write(Actor(ctx), "settings.update", null, null);

            return Results.Ok(new
            {
                ok = true,
                restartRequired = request.Web is not null,
                settings = settings.Snapshot().Web
            });
        });

        api.MapGet("/settings/export", (SettingsStore settings) =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Json.Serialize(settings.Snapshot()));
            return Results.File(bytes, "application/json", $"dayzpulse-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        });

        api.MapPost("/settings/import", async (HttpRequest request, SettingsStore settings, ServerRegistry registry,
            AuditLog audit, HttpContext ctx) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидалась загрузка файла." });
            }

            var form = await request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.BadRequest(new { error = "Файл не выбран." });
            }

            var temp = Path.Combine(AppPaths.TempDirectory, $"import-{Guid.NewGuid():n}.json");
            Directory.CreateDirectory(AppPaths.TempDirectory);

            await using (var stream = File.Create(temp))
            {
                await file.CopyToAsync(stream, ctx.RequestAborted);
            }

            try
            {
                settings.ImportFrom(temp);
            }
            finally
            {
                File.Delete(temp);
            }

            registry.Sync();
            audit.Write(Actor(ctx), "settings.import", file.FileName, null);

            return Results.Ok(new { ok = true, restartRequired = true });
        });

        // ---------------------------------------------------------------- служба

        api.MapGet("/service/status", (WindowsServiceManager manager) => Results.Ok(manager.GetStatus()));

        api.MapPost("/service/install", async (ServiceActionRequest? request, WindowsServiceManager manager,
            AuditLog audit, HttpContext ctx) =>
        {
            var message = await manager.InstallAsync(request?.Arguments, ctx.RequestAborted);
            audit.Write(Actor(ctx), "service.install", message, null);
            return Results.Ok(new { message, status = manager.GetStatus() });
        });

        api.MapPost("/service/uninstall", async (WindowsServiceManager manager, AuditLog audit, HttpContext ctx) =>
        {
            var message = await manager.UninstallAsync(ctx.RequestAborted);
            audit.Write(Actor(ctx), "service.uninstall", message, null);
            return Results.Ok(new { message, status = manager.GetStatus() });
        });

        api.MapPost("/service/start", async (WindowsServiceManager manager, AuditLog audit, HttpContext ctx) =>
        {
            var message = await manager.StartAsync(ctx.RequestAborted);
            audit.Write(Actor(ctx), "service.start", message, null);
            return Results.Ok(new { message, status = manager.GetStatus() });
        });

        api.MapPost("/service/stop", async (WindowsServiceManager manager, AuditLog audit, HttpContext ctx) =>
        {
            var message = await manager.StopAsync(ctx.RequestAborted);
            audit.Write(Actor(ctx), "service.stop", message, null);
            return Results.Ok(new { message, status = manager.GetStatus() });
        });

        // --------------------------------------------------------------- система

        api.MapGet("/system/info", (ServerRegistry registry, ConsoleHub hub) => Results.Ok(new
        {
            host = SystemInfo.Snapshot(),
            servers = registry.Summary(),
            consoleClients = hub.ClientCount,
            version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(),
            paths = new
            {
                home = AppPaths.BaseDirectory,
                data = AppPaths.DataRoot,
                settings = AppPaths.SettingsFile,
                logs = AppPaths.LogsDirectory
            }
        }));

        api.MapGet("/system/disk", (string? path) =>
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(path) ? AppPaths.BaseDirectory : path!;

                var volumeTotal = 0L;
                var volumeFree = 0L;

                // Размер тома — для контекста (сколько всего на диске).
                var root = Path.GetPathRoot(Path.GetFullPath(target));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        volumeTotal = drive.TotalSize;
                        volumeFree = drive.TotalFreeSpace;
                    }
                }

                // Размер папки сервера: пересчитываем не чаще раза в 5 минут,
                // иначе дашборд каждые 5 секунд перебирал бы все файлы сервера.
                var folderBytes = FolderSizeCached(target);

                return Results.Ok(new
                {
                    folderBytes,
                    totalBytes = volumeTotal,
                    freeBytes = volumeFree
                });
            }
            catch
            {
                return Results.Ok(new { folderBytes = 0L, totalBytes = 0L, freeBytes = 0L });
            }
        });

        api.MapGet("/system/autostart", () =>
        {
            try
            {
                return Results.Ok(new { enabled = Tray.AutostartManager.IsEnabled() });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { enabled = false, error = ex.Message });
            }
        });

        api.MapPost("/system/autostart", (AutostartRequest request, AuditLog audit, HttpContext ctx) =>
        {
            try
            {
                Tray.AutostartManager.Set(request.Enabled);
                audit.Write(Actor(ctx), "system.autostart", request.Enabled ? "включён" : "отключён", null);
                return Results.Ok(new { enabled = Tray.AutostartManager.IsEnabled() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/audit", (AuditLog audit, int? lines) => Results.Ok(new { lines = audit.ReadTail(lines ?? 200) }));

        // Выбор папки системным диалогом. Нужен и при первом запуске, и в настройках.
        api.MapPost("/system/pick-folder", (PickFolderRequest? request) =>
        {
            var title = string.IsNullOrWhiteSpace(request?.Title)
                ? "Выберите папку"
                : request!.Title!.Trim();

            var result = FolderPicker.Pick(request?.Path, title);

            if (result.Success)
            {
                return Results.Ok(new { ok = true, path = result.Path });
            }

            if (result.WasCancelled)
            {
                return Results.Ok(new { ok = false, cancelled = true });
            }

            return Results.Ok(new { ok = false, error = result.Error });
        });

        // Просмотр папок для выбора прямо в браузере: работает и в режиме службы.
        api.MapGet("/system/browse", (string? path) => Results.Ok(DirectoryBrowser.Browse(path)));

        // Создание папки из окна выбора.
        api.MapPost("/system/mkdir", (MakeDirRequest? request) =>
        {
            var result = DirectoryBrowser.CreateDirectory(request?.Parent, request?.Name);

            return result.Ok
                ? Results.Ok(new { ok = true, path = result.Path })
                : Results.BadRequest(new { error = result.Error });
        });

        api.MapPost("/system/restart", (Hosting.AppLifecycle lifecycle, AuditLog audit, HttpContext ctx) =>
        {
            audit.Write(Actor(ctx), "system.restart", null, null);
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                lifecycle.RequestRestart("запрос из веб-интерфейса");
            });
            return Results.Ok(new { ok = true, message = "Панель перезапускается." });
        });

        api.MapPost("/system/shutdown", (Hosting.AppLifecycle lifecycle, AuditLog audit, HttpContext ctx) =>
        {
            audit.Write(Actor(ctx), "system.shutdown", null, null);
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                lifecycle.RequestShutdown("запрос из веб-интерфейса");
            });
            return Results.Ok(new { ok = true, message = "Панель завершает работу." });
        });
    }

    // ------------------------------------------------------------------ авторизация

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/setup/status", (SettingsStore settings) =>
        {
            var auth = settings.Current.Auth;
            // Панель могла быть запущена с другим портом: показываем фактический.
            var effective = settings.EffectiveWeb;

            return Results.Ok(new
            {
                setupRequired = string.IsNullOrWhiteSpace(auth.PasswordHash) || !auth.OwnerCreated,
                productName = "DayZPulse",
                defaultServerDirectory = settings.Current.DefaultServerDirectory ?? "",
                webPort = effective.Port,
                bindAddress = effective.BindAddress,
                // Панель уже слушает этот порт, поэтому он не может быть «занят».
                portAvailable = true,
                suggestedPort = effective.Port,
                serverCount = settings.Current.Servers.Count
            });
        });

        // Проверка порта до сохранения: панель подскажет свободный.
        app.MapGet("/api/setup/port", (int port, string? address, SettingsStore settings) =>
        {
            var bind = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim();

            if (port is < 1 or > 65535)
            {
                return Results.BadRequest(new { error = "Порт должен быть в диапазоне 1–65535." });
            }

            // Порт, на котором панель уже слушает, считается доступным.
            var effective = settings.EffectiveWeb;
            var isCurrent = port == effective.Port;
            var free = isCurrent || PortProbe.IsAvailable(port, bind);

            return Results.Ok(new
            {
                port,
                available = free,
                current = isCurrent,
                suggested = free ? (int?)null : PortProbe.FindFree(port, bind)
            });
        });

        app.MapPost("/api/setup", (OwnerSetupRequest request, SettingsStore settings, SessionTokens tokens,
            ServerRegistry registry, AppLifecycle lifecycle, AuditLog audit, HttpContext ctx) =>
        {
            if (!string.IsNullOrWhiteSpace(settings.Current.Auth.PasswordHash) && settings.Current.Auth.OwnerCreated)
            {
                return Results.Conflict(new { error = "Владелец уже создан.", code = "already_configured" });
            }

            var username = (request.Username ?? "").Trim();
            var password = request.Password ?? "";

            if (username.Length is < 3 or > 32 || !PasswordPolicy.UsernamePattern.IsMatch(username))
            {
                return Results.BadRequest(new { error = "Имя владельца: 3–32 символа, латиница, цифры, «_», «-» и «.»." });
            }

            var problem = PasswordPolicy.Validate(password, username);
            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            if (!string.Equals(password, request.PasswordConfirm, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Пароли не совпадают." });
            }

            var (hash, salt, iterations) = PasswordHasher.Create(password);
            var directory = (request.ServerDirectory ?? "").Trim();

            // Папку сервера пользователь выбирает сам: значение по умолчанию не подставляем.
            // Папка должна существовать; сервер DayZ при необходимости уже скачан в неё.
            if (directory.Length < 3 || !Directory.Exists(directory))
            {
                return Results.BadRequest(new
                {
                    error = "Укажите существующую папку для сервера DayZ.",
                    code = "bad_directory"
                });
            }

            // Порт выбирает пользователь; проверяем его перед сохранением.
            // Текущий порт панели занят ею самой, поэтому его не проверяем.
            var port = request.WebPort is > 0 and <= 65535 ? request.WebPort!.Value : settings.Current.Web.Port;
            var effectivePort = settings.EffectiveWeb.Port;

            if (port != effectivePort && !PortProbe.IsAvailable(port, settings.Current.Web.BindAddress))
            {
                var free = PortProbe.FindFree(port, settings.Current.Web.BindAddress);
                return Results.BadRequest(new
                {
                    error = free > 0
                        ? $"Порт {port} занят другой программой. Свободен, например, {free}."
                        : $"Порт {port} занят. Выберите другой.",
                    code = "port_busy"
                });
            }

            var previousPort = settings.Current.Web.Port;

            settings.Update(s =>
            {
                s.Auth.Enabled = true;
                s.Auth.Username = username;
                s.Auth.PasswordHash = hash;
                s.Auth.PasswordSalt = salt;
                s.Auth.PasswordIterations = iterations;
                s.Auth.OwnerCreated = true;
                s.Auth.OwnerName = username;
                s.Auth.OwnerCreatedAt = DateTimeOffset.Now;

                // Папка и порт запоминаются для первого сервера и веб-интерфейса.
                s.DefaultServerDirectory = directory;
                s.Web.Port = port;
            });

            registry.Sync();

            audit.Write(username, "auth.owner.created", $"{directory} · порт {port}", null);

            var token = tokens.Issue(username, TimeSpan.FromHours(settings.Current.Auth.SessionHours));
            SessionCookies.Set(ctx, token, settings.Current.Auth.SessionHours);

            // Веб-сервер слушает порт при запуске, поэтому смена порта
            // требует перезапуска приложения.
            if (port != previousPort)
            {
                audit.Write(username, "system.restart", $"порт {previousPort} → {port}", null);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(900);
                    lifecycle.RequestRestart($"смена порта на {port}");
                });
            }

            return Results.Ok(new
            {
                ok = true,
                user = username,
                serverDirectory = directory,
                webPort = port,
                restartRequired = port != previousPort
            });
        });

        app.MapPost("/api/auth/login", (LoginRequest request, SettingsStore settings, SessionTokens tokens,
            AuditLog audit, HttpContext ctx) =>
        {
            var auth = settings.Current.Auth;

            if (string.IsNullOrWhiteSpace(auth.PasswordHash))
            {
                return Results.Json(new { error = "Панель ещё не настроена.", code = "setup_required" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!auth.Enabled)
            {
                SessionCookies.Set(ctx, tokens.Issue(auth.Username, TimeSpan.FromHours(auth.SessionHours)), auth.SessionHours);
                return Results.Ok(new { ok = true, user = auth.Username });
            }

            var username = (request.Username ?? "").Trim();
            var password = request.Password ?? "";

            // Сначала проверяем владельца, затем — администраторов.
            var ownerName = auth.OwnerName ?? auth.Username;
            var isOwner = string.Equals(username, ownerName, StringComparison.OrdinalIgnoreCase);
            var ownerOk = isOwner && PasswordHasher.Verify(password, auth.PasswordHash, auth.PasswordSalt, auth.PasswordIterations);

            var admin = auth.Admins.FirstOrDefault(a =>
                a.Enabled && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
            var adminOk = admin is not null && PasswordHasher.Verify(password, admin.PasswordHash, admin.PasswordSalt, admin.PasswordIterations);

            if (!ownerOk && !adminOk)
            {
                audit.Write(username.Length > 0 ? username : "неизвестно", "auth.login.failed", null, Remote(ctx));
                Thread.Sleep(Random.Shared.Next(120, 260));
                return Results.Json(new { error = "Неверное имя или пароль." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var loginName = adminOk ? admin!.Username : ownerName;
            SessionCookies.Set(ctx, tokens.Issue(loginName, TimeSpan.FromHours(auth.SessionHours)), auth.SessionHours);

            if (adminOk)
            {
                settings.Update(s =>
                {
                    var record = s.Auth.Admins.FirstOrDefault(a =>
                        string.Equals(a.Username, loginName, StringComparison.OrdinalIgnoreCase));
                    if (record is not null) record.LastLoginAt = DateTimeOffset.Now;
                });
            }

            audit.Write(loginName, "auth.login.success", adminOk ? "admin" : "owner", Remote(ctx));
            return Results.Ok(new { ok = true, user = loginName });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            SessionCookies.Clear(ctx);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/auth/status", (SettingsStore settings, SessionTokens tokens, HttpContext ctx) =>
        {
            var auth = settings.Current.Auth;
            var user = tokens.Validate(ctx.Request.Cookies[AuthenticationMiddleware.CookieName]);
            var configured = !string.IsNullOrWhiteSpace(auth.PasswordHash) && auth.OwnerCreated;
            var identity = SessionTokens.Resolve(settings.Current, user);

            // Определяем, откуда пришёл запрос: с этой машины, из локальной сети
            // или извне. При внешнем доступе панель советует защищённый туннель.
            var remote = ctx.Connection.RemoteIpAddress;
            var access = ConnectionScopeDetector.Classify(remote);

            return Results.Ok(new
            {
                authEnabled = auth.Enabled,
                setupRequired = !configured,
                authenticated = user is not null,
                user,
                owner = auth.OwnerName,
                isOwner = identity.IsOwner,
                // Права текущего пользователя: разделы и серверы.
                canUseAllSections = identity.CanUseAllSections,
                sections = identity.CanUseAllSections ? null : identity.Sections,
                canUseAllServers = identity.Servers is null,
                servers = identity.Servers,
                productName = "DayZPulse",
                serverCount = settings.Current.Servers.Count,
                defaultServerDirectory = settings.Current.DefaultServerDirectory ?? "",
                access = access.ToString().ToLowerInvariant(),
                localAccess = access is not ConnectionScope.External
            });
        });

        app.MapPost("/api/auth/password", (PasswordChangeRequest request, SettingsStore settings,
            AuditLog audit, HttpContext ctx) =>
        {
            var auth = settings.Current.Auth;
            if (!PasswordHasher.Verify(request.CurrentPassword ?? "", auth.PasswordHash, auth.PasswordSalt, auth.PasswordIterations))
            {
                return Results.BadRequest(new { error = "Текущий пароль неверен." });
            }

            var problem = PasswordPolicy.Validate(request.NewPassword ?? "", auth.Username);
            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            var (hash, salt, iterations) = PasswordHasher.Create(request.NewPassword!);
            settings.Update(s =>
            {
                s.Auth.PasswordHash = hash;
                s.Auth.PasswordSalt = salt;
                s.Auth.PasswordIterations = iterations;
                s.Auth.Enabled = true;
                s.Auth.OwnerCreated = true;
            });

            audit.Write(Actor(ctx), "auth.password.changed", null, null);
            return Results.Ok(new { ok = true });
        });

        // ------------------------------------------------------- администраторы

        // Список администраторов (владелец видит и правит). Пароли не отдаём.
        app.MapGet("/api/admins", (SettingsStore settings, SessionTokens tokens, HttpContext ctx) =>
        {
            if (!IsOwnerRequest(settings, tokens, ctx, out _))
            {
                return Results.Forbid();
            }

            var auth = settings.Current.Auth;
            var online = tokens.ActiveUsers();

            var admins = auth.Admins.Select(a => new
            {
                a.Id,
                a.Username,
                a.Enabled,
                allSections = a.AllSections,
                sections = a.Sections,
                allServers = a.AllServers,
                servers = a.Servers,
                createdAt = a.CreatedAt,
                lastLoginAt = a.LastLoginAt,
                online = online.Contains(a.Username)
            });

            return Results.Ok(new
            {
                owner = new
                {
                    username = auth.OwnerName ?? auth.Username,
                    online = online.Contains(auth.OwnerName ?? auth.Username)
                },
                admins
            });
        });

        // Создание администратора.
        app.MapPost("/api/admins", (AdminCreateRequest request, SettingsStore settings, SessionTokens tokens,
            AuditLog audit, HttpContext ctx) =>
        {
            if (!IsOwnerRequest(settings, tokens, ctx, out _))
            {
                return Results.Forbid();
            }

            var username = (request.Username ?? "").Trim();
            if (username.Length is < 3 or > 32)
            {
                return Results.BadRequest(new { error = "Имя: от 3 до 32 символов." });
            }

            var auth = settings.Current.Auth;
            var ownerName = auth.OwnerName ?? auth.Username;
            if (string.Equals(username, ownerName, StringComparison.OrdinalIgnoreCase) ||
                auth.Admins.Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new { error = "Такое имя уже занято." });
            }

            var problem = PasswordPolicy.Validate(request.Password ?? "", username);
            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            var (hash, salt, iterations) = PasswordHasher.Create(request.Password!);
            var account = new AdminAccount
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                Username = username,
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordIterations = iterations,
                Enabled = true,
                AllSections = request.AllSections,
                Sections = NormalizeSections(request.Sections),
                AllServers = request.AllServers,
                Servers = request.Servers ?? [],
                CreatedAt = DateTimeOffset.Now
            };

            settings.Update(s => s.Auth.Admins.Add(account));
            audit.Write(Actor(ctx), "admin.create", username, null);

            return Results.Ok(new { ok = true, id = account.Id });
        });

        // Изменение администратора (права; пароль — если задан).
        app.MapPut("/api/admins/{id}", (string id, AdminUpdateRequest request, SettingsStore settings,
            SessionTokens tokens, AuditLog audit, HttpContext ctx) =>
        {
            if (!IsOwnerRequest(settings, tokens, ctx, out _))
            {
                return Results.Forbid();
            }

            var auth = settings.Current.Auth;
            var account = auth.Admins.FirstOrDefault(a => a.Id == id);
            if (account is null)
            {
                return Results.NotFound(new { error = "Администратор не найден." });
            }

            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                var problem = PasswordPolicy.Validate(request.Password!, account.Username);
                if (problem is not null)
                {
                    return Results.BadRequest(new { error = problem });
                }

                var (hash, salt, iterations) = PasswordHasher.Create(request.Password!);
                account.PasswordHash = hash;
                account.PasswordSalt = salt;
                account.PasswordIterations = iterations;
            }

            settings.Update(s =>
            {
                var record = s.Auth.Admins.First(a => a.Id == id);
                if (request.Enabled is not null) record.Enabled = request.Enabled.Value;
                if (request.AllSections is not null) record.AllSections = request.AllSections.Value;
                if (request.Sections is not null) record.Sections = NormalizeSections(request.Sections);
                if (request.AllServers is not null) record.AllServers = request.AllServers.Value;
                if (request.Servers is not null) record.Servers = request.Servers;
            });

            audit.Write(Actor(ctx), "admin.update", account.Username, null);
            return Results.Ok(new { ok = true });
        });

        // Удаление администратора.
        app.MapDelete("/api/admins/{id}", (string id, SettingsStore settings, SessionTokens tokens,
            AuditLog audit, HttpContext ctx) =>
        {
            if (!IsOwnerRequest(settings, tokens, ctx, out _))
            {
                return Results.Forbid();
            }

            var name = settings.Current.Auth.Admins.FirstOrDefault(a => a.Id == id)?.Username ?? id;
            settings.Update(s => s.Auth.Admins.RemoveAll(a => a.Id == id));
            audit.Write(Actor(ctx), "admin.delete", name, null);
            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>Отбирает только допустимые ключи разделов.</summary>
    private static List<string> NormalizeSections(List<string>? sections) =>
        (sections ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Проверяет, что запрос идёт от владельца панели.</summary>
    private static bool IsOwnerRequest(SettingsStore settings, SessionTokens tokens, HttpContext ctx, out string user)
    {
        user = tokens.Validate(ctx.Request.Cookies[AuthenticationMiddleware.CookieName]) ?? "";
        var auth = settings.Current.Auth;
        var ownerName = auth.OwnerName ?? auth.Username;

        // Если авторизация выключена, считаем запрос владельческим.
        if (!auth.Enabled)
        {
            return true;
        }

        return user.Length > 0 && string.Equals(user, ownerName, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- хелперы

    private static ServerRuntime Require(ServerRegistry registry, string serverId) =>
        registry.Get(serverId) ?? throw new KeyNotFoundException($"Сервер не найден: {serverId}");

    /// <summary>Первый свободный блок из пяти портов, не пересекающийся с другими серверами.</summary>
    private static int NextFreePort(AppSettings settings)
    {
        var used = settings.Servers.Select(p => p.Server.Port).ToHashSet();
        for (var port = 2302; port <= 65000; port += 10)
        {
            if (used.Contains(port))
            {
                continue;
            }

            // Блок 2300-2400 зарезервирован за первым сервером.
            if (used.Count > 0 && port is >= 2302 and < 2402)
            {
                continue;
            }

            return port;
        }

        return 2302;
    }

    private static string NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return "#3fb950";
        }

        var value = color.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value.ToLowerInvariant() : "#3fb950";
    }

    /// <summary>Известные карты DayZ; произвольное значение сохраняется как есть.</summary>
    private static string NormalizeMap(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return "chernarusplus";
        }

        var value = map.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9_.-]{1,40}$") ? value : "chernarusplus";
    }

    private static string Mask(string token) =>
        string.IsNullOrEmpty(token) ? "не задан" : token.Length <= 8 ? new string('•', token.Length) : token[..4] + "…" + token[^4..];

    /// <summary>Заключает путь в кавычки, если в нём есть пробелы.</summary>
    private static string QuoteCommand(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;

    /// <summary>Путь к конфигу сервера; не бросает исключений для интерфейса.</summary>
    private static string SafeConfigPath(ServerSettings options)
    {
        try { return ServerPaths.ConfigFile(options); }
        catch { return options.ConfigFile; }
    }

    private static string? SafeExecutable(ServerSettings options)
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

    /// <summary>
    /// Удаляет папку, повторяя попытки: файл мог освободиться не сразу после
    /// закрытия журнала сервера, и Windows держит его ещё мгновение.
    /// </summary>
    private static void DeleteDirectoryWithRetry(string directory, int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch when (attempt < attempts)
            {
                Thread.Sleep(250 * attempt);
            }
        }
    }

    /// <summary>
    /// Суммарный размер всех файлов в папке (рекурсивно).
    /// Недоступные файлы пропускаются: папка сервера может содержать
    /// заблокированные или удалённые файлы.
    /// </summary>
    private static long FolderSize(string directory)
    {
        long total = 0;

        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Файл занят или исчез — пропускаем.
                    }
                }
            }
            catch
            {
                // Нет доступа к каталогу.
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    pending.Push(sub);
                }
            }
            catch
            {
                // Нет доступа к подкаталогам.
            }
        }

        return total;
    }

    /// <summary>Размер папки с кэшем на 5 минут: перебор файлов небыстрый.</summary>
    private static long FolderSizeCached(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        lock (FolderSizeCache)
        {
            if (FolderSizeCache.TryGetValue(directory, out var cached) &&
                DateTimeOffset.Now - cached.At < TimeSpan.FromMinutes(5))
            {
                return cached.Bytes;
            }
        }

        var bytes = FolderSize(directory);

        lock (FolderSizeCache)
        {
            FolderSizeCache[directory] = (bytes, DateTimeOffset.Now);
        }

        return bytes;
    }

    private static string Actor(HttpContext ctx) =>
        ctx.Items.TryGetValue("actor", out var value) && value is string actor ? actor : "неизвестно";

    private static string? Remote(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    public sealed record ServerCreateRequest(string? Name, string? Id, int? Port, string? Color, string? Directory, string? Map);
    public sealed record ServerSettingsUpdate(ProfilePatch? Profile, ServerSettings? Server, TrackerSettings? Tracker,
        BackupSettings? Backups, List<ConfigFileEntry>? ConfigFiles);
    public sealed record ProfilePatch(string? Name, bool? Enabled, string? Color);
    public sealed record GlobalSettingsUpdate(WebSettings? Web, LoggingSettings? Logging, AuthPatch? Auth, string? DefaultServerId);
    public sealed record AuthPatch(bool Enabled, string? Username, int SessionHours);
    public sealed record ServiceActionRequest(string? Arguments);
    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);

    public sealed record AdminCreateRequest(
        string? Username, string? Password, bool AllSections, List<string>? Sections,
        bool AllServers, List<string>? Servers);

    public sealed record LaunchUpdateRequest(string? Arguments, string? ExtraArguments);

    public sealed record AdminUpdateRequest(
        string? Password, bool? Enabled, bool? AllSections, List<string>? Sections,
        bool? AllServers, List<string>? Servers);
    public sealed record AutostartRequest(bool Enabled);
    public sealed record PickFolderRequest(string? Path, string? Title);
    public sealed record MakeDirRequest(string? Parent, string? Name);
    public sealed record OwnerSetupRequest(string? Username, string? Password, string? PasswordConfirm,
        string? ServerDirectory, int? WebPort);
}
