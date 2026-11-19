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
    ///
    /// connect-src разрешает localhost с любым портом: при смене порта панель
    /// перезапускается, и страница со старого адреса опрашивает новый порт
    /// (<c>waitForPortThenOpen</c>). Это по-прежнему только локальная машина.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' http://127.0.0.1:* http://localhost:* ws://127.0.0.1:* ws://localhost:*; " +
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

        // Смена порта: страница со старого адреса опрашивает новый порт (это
        // другой origin), дожидаясь подъёма панели. Разрешаем ровно это —
        // безопасный статус и только для localhost, — иначе браузер блокирует
        // запрос по CORS. Учётные данные не запрашиваются, cookie не уходит.
        AllowLocalStatusProbe(context, headers);

        await _next(context);
    }

    /// <summary>Разрешает кросс-портовый опрос статуса страницам, открытым на localhost.</summary>
    private static void AllowLocalStatusProbe(HttpContext context, IHeaderDictionary headers)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!IsLocalOrigin(origin))
        {
            return;
        }

        headers["Access-Control-Allow-Origin"] = origin;
        headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";

        // Страница опрашивает статус с credentials (cookie и так общая для
        // хоста — cookie не зависят от порта), поэтому без этого заголовка
        // браузер отклонил бы ответ.
        headers["Access-Control-Allow-Credentials"] = "true";

        // Ответ зависит от Origin — кэшировать нельзя по одному адресу.
        headers["Vary"] = "Origin";
    }

    /// <summary>Origin указывает на этот же компьютер (127.0.0.1, localhost, ::1).</summary>
    private static bool IsLocalOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin == "null")
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host switch
        {
            "127.0.0.1" or "localhost" or "::1" or "[::1]" => true,
            _ => false
        };
    }
}

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
