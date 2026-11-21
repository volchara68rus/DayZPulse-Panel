using DayZPulse.Core;
using DayZPulse.Hosting;
using DayZPulse.Infrastructure;
using DayZPulse.Logging;
using DayZPulse.Models;
using DayZPulse.Security;
using DayZPulse.Service;
using DayZPulse.Steam;
using DayZPulse.Tracker;
using DayZPulse.Web;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.EventLog;
namespace DayZPulse;

public static class AppBootstrap
{
    /// <summary>Собирает и настраивает веб-приложение.</summary>
    public static WebApplication Build(string[] args, bool isWindowsService, Action<string>? consoleSink,
        RuntimeOverrides? overrides = null)
    {
        overrides ??= new RuntimeOverrides();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(AppBootstrap).Assembly.GetName().Name
        });

        // Настройки загружаем как можно раньше: часть регистраций от них зависит.
        var preLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole(o => o.SingleLine = true));
        var settings = new SettingsStore(preLoggerFactory.CreateLogger<SettingsStore>());
        AppPaths.Initialize(null);
        Bootstrap.FirstRunSetup.EnsureServerDirectories(settings);

        builder.Services.AddSingleton(overrides);
        settings.SetRuntimeOverrides(overrides);

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<SessionTokens>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddSingleton<EncodingResolver>();
        builder.Services.AddSingleton<IEncodingResolver>(sp => sp.GetRequiredService<EncodingResolver>());
        builder.Services.AddSingleton<WindowsServiceManager>();
        builder.Services.AddSingleton<ConsoleHub>();
        builder.Services.AddSingleton<LiveHub>();
        builder.Services.AddSingleton<LivePublisher>();
        builder.Services.AddSingleton<ConsoleBridge>();
        builder.Services.AddSingleton<WebHostLifecycleService>();
        builder.Services.AddSingleton<AppLifecycle>();

        // Остановка не должна ждать закрытия WebSocket-соединений: страница
        // держит канал событий открытым, и хост по умолчанию висит 30 секунд.
        // При перезапуске (например, после смены порта) это выглядело как
        // «зависшая» страница. Гасим быстро, соединения закрываем сами.
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(3);
        });
        builder.Services.AddSingleton<SteamCmdService>();
        builder.Services.AddSingleton<SteamCmdInstaller>();

        // ------------------------------------------- серверы и трекер (мультисервер)
        builder.Services.AddSingleton<ServerRegistry>();
        builder.Services.AddSingleton<TrackerService>();
        builder.Services.AddSingleton<RconService>();
        builder.Services.AddSingleton<DayZMapService>();
        builder.Services.AddSingleton<ItemCatalog>();
        builder.Services.AddSingleton<ModManager>();
        builder.Services.AddSingleton<PanelModService>();
        builder.Services.AddSingleton<ServerLogService>();
        builder.Services.AddSingleton<LoadoutService>();
        builder.Services.AddSingleton<EngineMetricsService>();
        builder.Services.AddSingleton<DayZPulse.Web.WebUiUpdateService>();
        builder.Services.AddHostedService<DayZPulse.Web.WebUiCheckService>();
        builder.Services.AddSingleton<DayZPulse.Web.PanelUpdateService>();
        builder.Services.AddHostedService<DayZPulse.Web.PanelUpdateCheckService>();

        var memoryLogProvider = new InMemoryLogProvider(ParseLevel(settings.Current.Logging.MinimumLevel));
        builder.Services.AddSingleton(memoryLogProvider);

        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerRegistry>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsoleBridge>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LivePublisher>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WebHostLifecycleService>());
        builder.Services.AddHostedService<WebSocketShutdownService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TrackerService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RconService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SteamCmdInstaller>());

        builder.Services.AddWindowsService(options => options.ServiceName = WindowsServiceManager.ServiceName);

        // ------------------------------------------------------------- логи
        var loggingSettings = settings.Current.Logging;
        var minimum = ParseLevel(loggingSettings.MinimumLevel);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(minimum);
        builder.Logging.AddProvider(new FileLoggerProvider(AppPaths.LogsDirectory, loggingSettings.LogRetentionDays, minimum));
        builder.Logging.AddProvider(memoryLogProvider);

        if (isWindowsService)
        {
            builder.Logging.AddEventLog(new EventLogSettings { SourceName = WindowsServiceManager.ServiceName });
        }

        if (consoleSink is not null)
        {
            builder.Logging.AddProvider(new ConsoleLogProvider(consoleSink, minimum));
        }

        // ----------------------------------------------------------- веб-сервер
        var web = new WebSettings
        {
            Port = overrides.WebPort is > 0 ? overrides.WebPort.Value : settings.Current.Web.Port,
            BindAddress = settings.Current.Web.BindAddress,
            AllowRemote = settings.Current.Web.AllowRemote,
            OpenBrowserOnStart = overrides.OpenBrowserOnStart ?? settings.Current.Web.OpenBrowserOnStart
        };
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;

            var address = web.AllowRemote ? "0.0.0.0" : web.BindAddress;
            if (!System.Net.IPAddress.TryParse(address, out var ip))
            {
                ip = System.Net.IPAddress.Loopback;
            }

            options.Listen(ip, web.Port, listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);

            // Ограничение размера тела запроса (импорт настроек, загрузки).
            options.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
        });

        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("api", context =>
                System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "local",
                    _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 100,
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
                    }));

            // Жёсткий лимит на попытки входа и создание владельца:
            // применяется к /api/auth/login, /api/setup и /api/steam/login.
            options.AddPolicy("auth", context =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "local",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0,
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
                    }));
        });

        var app = builder.Build();

        // Если панель обновили через установщик, старая обновлённая веб-часть
        // из каталога данных перекрыла бы новую встроенную вёрстку: отбрасываем
        // её до того, как настроим отдачу статики (удалять каталог после
        // создания провайдера файлов нельзя).
        DayZPulse.Web.WebUiUpdateService.CleanupStaleOverride(settings, preLoggerFactory.CreateLogger("WebUi"));

        // Веб-часть может быть обновлена отдельно от панели: тогда её копия
        // лежит в каталоге данных (ProgramData), куда пишут без прав
        // администратора. Каталог создаём заранее (пусть даже пустой), чтобы
        // составной провайдер видел и файлы, появившиеся после обновления —
        // иначе их подхватил бы только перезапуск. Обновлённые файлы имеют
        // приоритет, отсутствующие берутся из встроенной копии.
        Directory.CreateDirectory(AppPaths.WebUiOverride);

        var webRootProviders = new List<Microsoft.Extensions.FileProviders.IFileProvider>
        {
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppPaths.WebUiOverride)
        };

        // Встроенная копия может отсутствовать (например, при запуске с
        // --data-dir в отдельный каталог): добавляем её, только если она есть.
        if (Directory.Exists(AppPaths.EmbeddedWebRoot))
        {
            webRootProviders.Add(new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppPaths.EmbeddedWebRoot));
        }

        app.Environment.WebRootFileProvider = new Microsoft.Extensions.FileProviders.CompositeFileProvider(
            [.. webRootProviders]);

        app.UseSecurityHeaders();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseRateLimiter();

        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (ServerStartException ex)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = "server_start_failed" });
            }
            catch (UnauthorizedAccessException ex)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = "forbidden" });
            }
            catch (FileNotFoundException ex)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = "not_found" });
            }
            catch (KeyNotFoundException ex)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = "not_found" });
            }
            catch (ArgumentException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = CleanMessage(ex.Message), code = "bad_request" });
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = "invalid_operation" });
            }
            catch (InvalidDataException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = "invalid_data" });
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DayZPulse.Request");
                logger.LogError(ex, "Необработанная ошибка при обработке {Path}", context.Request.Path);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "Внутренняя ошибка: " + ex.Message, code = "internal" });
            }
        });

        app.UseMiddleware<AuthenticationMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var headers = ctx.Context.Response.Headers;
                headers.CacheControl = "no-cache";

                // Все страницы и скрипты панели — UTF-8. Без явной кодировки
                // браузер с не-русской локалью (например, китайской) показывал
                // русский текст иероглифами: он угадывал кодировку по системе.
                var contentType = headers.ContentType.ToString();
                if (!contentType.Contains("charset", StringComparison.OrdinalIgnoreCase) &&
                    (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                     contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                     contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                     contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)))
                {
                    headers.ContentType = contentType + "; charset=utf-8";
                }
            }
        });

        app.MapDayZPulseApi();
        app.MapSteamEndpoints();
        app.MapTrackerEndpoints();
        app.Map("/ws/console", ConsoleHub.Endpoint);
        app.Map("/ws/events", LiveHub.Endpoint);

        // Браузеры запрашивают /favicon.ico автоматически — отдаём ту же иконку.
        app.MapGet("/favicon.ico", () =>
        {
            // Ищем в обновлённой веб-части, затем во встроенной.
            foreach (var root in new[] { AppPaths.WebUiOverride, AppPaths.EmbeddedWebRoot })
            {
                var path = Path.Combine(root, "assets", "favicon.svg");
                if (File.Exists(path))
                {
                    return Results.File(path, "image/svg+xml");
                }
            }

            return Results.NotFound();
        });

        return app;
    }

    /// <summary>Убирает технический хвост вроде "(Parameter 'settings')" из сообщений об ошибках.</summary>
    private static string CleanMessage(string message)
    {
        var index = message.IndexOf("(Parameter", StringComparison.Ordinal);
        var cleaned = index > 0 ? message[..index].Trim() : message;
        return cleaned.Length > 0 ? cleaned : "Некорректные данные запроса.";
    }

    public static LogLevel ParseLevel(string? value) => value?.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        "none" => LogLevel.None,
        _ => LogLevel.Information
    };
}
