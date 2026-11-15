namespace DayZPulse.Security;

/// <summary>
/// Выставляет защитные HTTP-заголовки: строгий CSP, запрет встраивания в iframe,
/// запрет MIME-сниффинга, ограничение referrer и опасных API браузера.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Скрипты — только свои файлы: ни inline, ни eval, ни внешних источников,
    /// поэтому внедрить исполняемый код через данные невозможно.
    /// Стили допускают inline: панель динамически задаёт ширины полос и позиции
    /// маркеров, а CSS не является вектором выполнения кода.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws://127.0.0.1 ws://localhost; " +
        "form-action 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'none'; " +
        "object-src 'none'; " +
        "media-src 'none'; " +
        "worker-src 'none'";

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["X-XSS-Protection"] = "0"; // Устаревший фильтр выключен: CSP надёжнее.
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Cross-Origin-Embedder-Policy"] = "require-corp";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";
        headers["Permissions-Policy"] =
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), " +
            "microphone=(), payment=(), usb=(), interest-cohort=()";
        headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        headers.Remove("Server");
        headers.Remove("X-Powered-By");

        await _next(context);
    }
}

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
