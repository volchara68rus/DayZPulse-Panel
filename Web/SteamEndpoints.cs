using DayZPulse.Core;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Security;
using DayZPulse.Steam;

namespace DayZPulse.Web;

/// <summary>Эндпоинты SteamCMD: установка, вход в Steam и опрос состояния.</summary>
public static class SteamEndpoints
{
    public static void MapSteamEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/steam").RequireRateLimiting("api");

        // ------------------------------------------- первый запуск: автоустановка

        app.MapGet("/api/setup/steam", (SettingsStore settings, SteamCmdService steam,
            SteamCmdInstaller installer, string? directory) =>
        {
            // SteamCMD всегда живёт рядом с панелью — папка сервера его не задаёт.
            var target = SteamCmdService.ResolveDirectory(settings.Current.Steam.Directory);
            var present = steam.IsInstalled(target);
            var warm = steam.IsWarm(target);
            var status = installer.Status;

            // Папка сервера: проверяем, есть ли в ней уже установленный сервер DayZ.
            var serverRoot = string.IsNullOrWhiteSpace(directory)
                ? (settings.Current.DefaultServerDirectory ?? "").Trim()
                : directory!.Trim();

            var serverExists = steam.IsServerInstalled(serverRoot);
            var directoryExists = serverRoot.Length >= 3 && Directory.Exists(serverRoot);

            // SteamCMD готов, когда скачан и прогрет.
            var steamReady = present && warm;

            return Results.Ok(new
            {
                // Владельца можно создать, когда SteamCMD готов и папка существует.
                // Сервер скачается позже, из панели, после входа в Steam.
                required = !steamReady,
                installed = steamReady,
                steamReady,
                present,
                warm,
                running = status.Running,
                directory = target,
                path = steam.ExecutablePath(target),
                autoInstall = settings.Current.Steam.AutoInstall,
                canInstall = true,
                progress = status.ProgressPercent,
                phase = status.Phase,
                stage = status.Stage,
                error = status.Error,

                // Состояние выбранной папки сервера.
                serverDirectory = serverRoot,
                directoryExists,
                serverExists
            });
        });

        // Запуск установки SteamCMD. Возвращает ответ сразу, а не ждёт загрузку:
        // иначе браузер не может показывать прогресс.
        app.MapPost("/api/setup/steam/install", (InstallRequest? request, SettingsStore settings,
            SteamCmdInstaller installer, AuditLog audit, HttpContext context) =>
        {
            var directory = (request?.ServerDirectory ?? "").Trim();
            if (directory.Length >= 3)
            {
                settings.Update(s => s.DefaultServerDirectory = directory, notify: false);
            }

            if (!installer.StartInstall())
            {
                return Results.Ok(new { ok = true, message = "Загрузка уже идёт." });
            }

            audit.Write("setup", "steam.install", directory, null);
            return Results.Ok(new { ok = true, message = "Загрузка SteamCMD началась." });
        });

        // --------------------------------------------------- состояние и настройки

        api.MapGet("/status", (SettingsStore settings, SteamCmdService steam) =>
        {
            var options = settings.Current.Steam;
            var directory = SteamCmdService.ResolveDirectory(options.Directory);
            var status = steam.Status;

            var loggedIn = options.LoggedIn;

            // Флаг мог не сохраниться (старый сбой разбора), но сессия SteamCMD
            // могла остаться. Проверку запускаем в фоне, чтобы не блокировать
            // интерфейс: результат подхватит следующий запрос.
            if (!loggedIn && options.Username.Length > 0 &&
                status.State == SteamLoginState.Idle &&
                steam.IsInstalled(directory) && steam.IsWarm(directory))
            {
                steam.CheckCachedSessionInBackground(directory, options.Username, ok =>
                {
                    if (!ok)
                    {
                        return;
                    }

                    settings.Update(s =>
                    {
                        s.Steam.LoggedIn = true;
                        s.Steam.LoggedInAt ??= DateTimeOffset.Now;
                        s.Steam.LastError = null;
                    });
                });
            }

            return Results.Ok(new
            {
                installed = steam.IsInstalled(directory),
                warm = steam.IsWarm(directory),
                directory,
                path = steam.ExecutablePath(directory),
                username = options.Username,
                loggedIn,
                loggedInAt = settings.Current.Steam.LoggedInAt,
                lastError = settings.Current.Steam.LastError,
                autoInstall = options.AutoInstall,
                state = status.State.ToString(),
                stateError = status.Error,
                output = Tail(status.Output, 40)
            });
        });

        api.MapPost("/settings", (SteamSettingsUpdate request, SettingsStore settings, AuditLog audit, HttpContext ctx) =>
        {
            settings.Update(s =>
            {
                if (request.Directory is not null)
                {
                    s.Steam.Directory = request.Directory.Trim();
                }

                if (request.AutoInstall is not null)
                {
                    s.Steam.AutoInstall = request.AutoInstall.Value;
                }
            });

            audit.Write(Actor(ctx), "steam.settings", null, null);
            return Results.Ok(new { ok = true });
        });

        // ------------------------------------------------------------------- вход

        api.MapPost("/login", (SteamLoginRequest request, SettingsStore settings, SteamCmdService steam,
            AuditLog audit, HttpContext ctx) =>
        {
            var options = settings.Current.Steam;
            var directory = SteamCmdService.ResolveDirectory(options.Directory);

            var username = (request.Username ?? options.Username ?? "").Trim();
            var password = request.Password ?? "";

            if (username.Length < 1)
            {
                return Results.BadRequest(new { error = "Укажите логин Steam." });
            }

            if (!steam.IsInstalled(directory))
            {
                return Results.BadRequest(new { error = "SteamCMD ещё не установлен.", code = "not_installed" });
            }

            settings.Update(s => s.Steam.Username = username);

            // Повтор прерывает текущую попытку и начинает её заново.
            if (!steam.StartLogin(directory, username, password, request.Restart == true))
            {
                return Results.Conflict(new { error = "Вход уже выполняется." });
            }

            audit.Write(Actor(ctx), "steam.login", username, null);
            return Results.Ok(new { ok = true, message = "Вход выполняется." });
        });

        // Код Steam Guard передаётся отдельным запросом, пока идёт вход.
        api.MapPost("/guard", (SteamGuardRequest request, SteamCmdService steam, HttpContext ctx) =>
        {
            var code = (request.Code ?? "").Trim();
            if (code.Length == 0)
            {
                return Results.BadRequest(new { error = "Укажите код Steam Guard." });
            }

            return steam.ProvideGuardCode(code)
                ? Results.Ok(new { ok = true })
                : Results.Conflict(new { error = "Код сейчас не ожидается. Дождитесь запроса." });
        });

        api.MapPost("/logout", async (SettingsStore settings, SteamCmdService steam,
            AuditLog audit, HttpContext ctx) =>
        {
            var options = settings.Current.Steam;
            var directory = SteamCmdService.ResolveDirectory(options.Directory);

            var result = await steam.LogoutAsync(directory, ctx.RequestAborted);

            // Сбрасываем сохранённое состояние входа.
            settings.Update(s =>
            {
                s.Steam.LoggedIn = false;
                s.Steam.LoggedInAt = null;
                s.Steam.LastError = null;
            });

            audit.Write(Actor(ctx), "steam.logout", options.Username, null);

            return result.Success
                ? Results.Ok(new
                {
                    ok = true,
                    message = "Выход выполнен. При следующем входе потребуется код Steam Guard.",
                    output = result.Output
                })
                : Results.BadRequest(new { error = result.Error ?? "Не удалось выйти из Steam.", output = result.Output });
        });

        // Панель опрашивает этот метод после запуска входа: он же фиксирует успех.
        api.MapGet("/login/status", (SettingsStore settings, SteamCmdService steam) =>
        {
            var status = steam.Status;
            var options = settings.Current.Steam;

            if (status.State == SteamLoginState.Done && !options.LoggedIn)
            {
                settings.Update(s =>
                {
                    s.Steam.LoggedIn = true;
                    s.Steam.LoggedInAt = status.FinishedAt ?? DateTimeOffset.Now;
                    s.Steam.LastError = null;
                });
            }
            else if (status.State == SteamLoginState.Failed && status.Error is { Length: > 0 })
            {
                settings.Update(s => s.Steam.LastError = status.Error);
            }

            return Results.Ok(new
            {
                state = status.State.ToString(),
                error = status.Error,
                startedAt = status.StartedAt,
                finishedAt = status.FinishedAt,
                output = Tail(status.Output, 40),
                loggedIn = settings.Current.Steam.LoggedIn,
                username = settings.Current.Steam.Username
            });
        });

        // ------------------------------------ загрузка сервера DayZ из панели
        // Состояние сервера DayZ в папке конкретного сервера панели.
        api.MapGet("/server/status", (SettingsStore settings, SteamCmdService steam,
            SteamCmdInstaller installer, string? serverId) =>
        {
            var profile = settings.FindServer(serverId) ?? settings.Current.Servers.FirstOrDefault();
            var directory = profile is null ? "" : ServerPaths.BaseDirectory(profile.Server);

            var status = installer.Status;

            return Results.Ok(new
            {
                serverId = profile?.Id,
                directory,
                directoryExists = directory.Length >= 3 && Directory.Exists(directory),
                installed = steam.IsServerInstalled(directory),
                steamReady = steam.IsInstalled(AppPaths.SteamCmdDirectory) && steam.IsWarm(AppPaths.SteamCmdDirectory),
                running = status.Running,
                progress = status.ProgressPercent,
                phase = status.Phase,
                stage = status.Stage,
                error = status.Error,
                loggedIn = settings.Current.Steam.LoggedIn,
                username = settings.Current.Steam.Username
            });
        });

        // Загрузка сервера DayZ. Возвращает ответ сразу: прогресс идёт по live-каналу.
        api.MapPost("/server/install", (ServerInstallRequest? request, SettingsStore settings,
            SteamCmdInstaller installer, SteamCmdService steam, AuditLog audit, HttpContext ctx) =>
        {
            var profile = settings.FindServer(request?.ServerId) ?? settings.Current.Servers.FirstOrDefault();
            if (profile is null)
            {
                return Results.BadRequest(new { error = "Сервер не выбран." });
            }

            var directory = ServerPaths.BaseDirectory(profile.Server);

            if (!steam.IsInstalled(AppPaths.SteamCmdDirectory) || !steam.IsWarm(AppPaths.SteamCmdDirectory))
            {
                return Results.BadRequest(new { error = "SteamCMD ещё не установлен.", code = "not_installed" });
            }

            if (!settings.Current.Steam.LoggedIn)
            {
                return Results.BadRequest(new
                {
                    error = "Сначала войдите в Steam: сервер DayZ доступен только аккаунту, владеющему игрой.",
                    code = "not_logged_in"
                });
            }

            if (!installer.StartServerInstall(directory))
            {
                return Results.Conflict(new { error = "Загрузка сервера уже идёт." });
            }

            audit.Write(Actor(ctx), "steam.server.install", $"{profile.Id} · {directory}", null);
            return Results.Ok(new { ok = true, message = "Загрузка сервера DayZ началась." });
        });
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var all = text.Split('\n');
        return all.Length <= lines ? text : string.Join('\n', all[^lines..]);
    }

    private static string Actor(HttpContext ctx) =>
        ctx.Items.TryGetValue("actor", out var value) && value is string actor ? actor : "неизвестно";

    public sealed record SteamLoginRequest(string? Username, string? Password, bool? ExpectsGuard, bool? Restart);
    public sealed record SteamGuardRequest(string? Code);
    public sealed record SteamSettingsUpdate(string? Directory, bool? AutoInstall);
    public sealed record InstallRequest(string? ServerDirectory);
    public sealed record ServerInstallRequest(string? ServerId);
}
