using System.Net;
using System.Threading.RateLimiting;
using DayZPulse.Infrastructure;
using DayZPulse.Models;
using DayZPulse.Security;

namespace DayZPulse.Web;

/// <summary>
/// Проверяет сессию, повторяет проверку Origin для изменяющих запросов
/// и отклоняет запросы без CSRF-токена.
/// </summary>
public sealed class AuthenticationMiddleware
{
    public const string CookieName = "dayzpulse_session";

    private static readonly string[] AnonymousPaths =
    [
        "/login.html",
        "/setup.html",
        "/api/auth/login",
        "/api/auth/status",
        "/api/auth/logout",
        "/api/setup",
        "/api/setup/status",
        "/api/setup/port",
        "/api/setup/steam",
        "/api/setup/steam/install",
        "/api/system/pick-folder",
        "/api/system/browse",
        "/api/system/mkdir",
        "/favicon.ico"
    ];

    private readonly RequestDelegate _next;
    private readonly SessionTokens _tokens;
    private readonly SettingsStore _settings;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    public AuthenticationMiddleware(RequestDelegate next, SessionTokens tokens, SettingsStore settings,
        ILogger<AuthenticationMiddleware> logger)
    {
        _next = next;
        _tokens = tokens;
        _settings = settings;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // CSRF-токен выдаём всегда: страница входа тоже должна уметь его отправить.
        EnsureCsrfCookie(context);

        if (IsStaticAsset(path))
        {
            await _next(context);
            return;
        }

        var auth = _settings.Current.Auth;

        // Мод авторизуется собственным токеном — сессия панели ему не нужна.
        if (path.StartsWith("/tracker/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Канал событий нужен и странице создания владельца: там сессии ещё нет.
        if (path.Equals("/ws/events", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (IsAnonymous(path))
        {
            // Анонимный доступ без авторизации, но защита от CSRF обязательна.
            if (CsrfService.IsSafeMethod(context.Request.Method))
            {
                await _next(context);
            }
            else
            {
                await EnforceCsrfAsync(context, () => _next(context));
            }

            return;
        }

        // Первый запуск: пока владелец не создан, пускаем только на страницу создания владельца.
        if (string.IsNullOrWhiteSpace(auth.PasswordHash))
        {
            if (path.Equals("/api/setup/status", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/setup", StringComparison.OrdinalIgnoreCase))
            {
                await EnforceCsrfAsync(context, () => _next(context));
                return;
            }

            if (WantsHtml(context, path))
            {
                context.Response.Redirect("/setup.html");
                return;
            }

            await WriteJsonErrorAsync(context, StatusCodes.Status409Conflict, "setup_required",
                "Требуется создать владельца панели.");
            return;
        }

        if (!auth.Enabled)
        {
            context.Items["actor"] = "без авторизации";
            if (CsrfService.IsSafeMethod(context.Request.Method))
            {
                await _next(context);
            }
            else
            {
                await EnforceCsrfAsync(context, () => _next(context));
            }

            return;
        }

        var user = ResolveUser(context);
        if (user is null)
        {
            if (WantsHtml(context, path))
            {
                context.Response.Redirect("/login.html");
                return;
            }

            await WriteJsonErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized",
                "Требуется авторизация.");
            return;
        }

        context.Items["actor"] = user;

        if (CsrfService.IsSafeMethod(context.Request.Method))
        {
            await _next(context);
            return;
        }

        await EnforceCsrfAsync(context, () => _next(context));
    }

    private async Task EnforceCsrfAsync(HttpContext context, Func<Task> next)
    {
        if (!IsSameOrigin(context))
        {
            _logger.LogWarning("Отклонён запрос с чужим Origin: {Origin} -> {Path}",
                context.Request.Headers.Origin.ToString(), context.Request.Path);
            await WriteJsonErrorAsync(context, StatusCodes.Status403Forbidden, "bad_origin",
                "Запрос пришёл с недопустимого источника.");
            return;
        }

        var cookie = context.Request.Cookies[CsrfService.CookieName];
        var header = context.Request.Headers[CsrfService.HeaderName].ToString();

        if (!CsrfService.Validate(cookie, header))
        {
            _logger.LogWarning("Отклонён запрос без корректного CSRF-токена: {Path}", context.Request.Path);
            await WriteJsonErrorAsync(context, StatusCodes.Status403Forbidden, "csrf",
                "Отсутствует или неверен CSRF-токен. Обновите страницу.");
            return;
        }

        await next();
    }

    /// <summary>Проверяем, что запрос пришёл с этого же узла (защита от межсайтовых отправок).</summary>
    private static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return IsLocalHost(uri.Host) || string.Equals(uri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase);
        }

        var referer = context.Request.Headers.Referer.ToString();
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            return IsLocalHost(refererUri.Host) ||
                   string.Equals(refererUri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase);
        }

        // Запросы без Origin и Referer (например, локальные утилиты) допускаем только на loopback.
        var remote = context.Connection.RemoteIpAddress;
        return remote is null || IPAddress.IsLoopback(remote);
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static void EnsureCsrfCookie(HttpContext context)
    {
        var existing = context.Request.Cookies[CsrfService.CookieName];
        if (!string.IsNullOrEmpty(existing) && existing.Length >= 32 && existing.Length <= 128)
        {
            context.Items["csrf"] = existing;
            return;
        }

        var token = CsrfService.NewToken();
        context.Items["csrf"] = token;
        context.Response.Cookies.Append(CsrfService.CookieName, token, new CookieOptions
        {
            HttpOnly = false, // Должна читаться скриптом для отправки в заголовке.
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Secure = context.Request.IsHttps,
            IsEssential = true
        });
    }

    private string? ResolveUser(HttpContext context)
    {
        var token = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authorization["Bearer ".Length..].Trim();
            }
        }

        return _tokens.Validate(token);
    }

    private static bool IsStaticAsset(string path) =>
        path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase);

    private static bool WantsHtml(HttpContext context, string path)
    {
        if (path.Equals("/", StringComparison.Ordinal))
        {
            return true;
        }

        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accept = context.Request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) &&
               !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
               !path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnonymous(string path) =>
        AnonymousPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    private static async Task WriteJsonErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error = message, code });
    }
}
