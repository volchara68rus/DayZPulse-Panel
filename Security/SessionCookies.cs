namespace DayZPulse.Security;

/// <summary>Выдача и удаление cookie сессии с защитными атрибутами.</summary>
public static class SessionCookies
{
    public static void Set(HttpContext context, string token, int hours)
    {
        context.Response.Cookies.Append(Web.AuthenticationMiddleware.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromHours(Math.Max(1, hours)),
            Secure = context.Request.IsHttps,
            IsEssential = true
        });
    }

    public static void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(Web.AuthenticationMiddleware.CookieName, new CookieOptions
        {
            Path = "/",
            SameSite = SameSiteMode.Strict,
            HttpOnly = true
        });
}
