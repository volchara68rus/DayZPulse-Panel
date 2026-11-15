using System.Security.Cryptography;
using System.Text;

namespace DayZPulse.Security;

/// <summary>
/// Защита от CSRF по схеме «двойная отправка cookie»: случайный токен кладётся
/// в cookie, доступную только скриптам этого сайта, и должен быть повторён
/// в заголовке X-CSRF-Token при каждом изменяющем запросе.
/// </summary>
public sealed class CsrfService
{
    public const string CookieName = "dayzpulse_csrf";
    public const string HeaderName = "X-CSRF-Token";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public static bool IsSafeMethod(string method) => SafeMethods.Contains(method);

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Сравнение за постоянное время, чтобы токен нельзя было подобрать по времени ответа.</summary>
    public static bool Validate(string? cookie, string? header)
    {
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(cookie);
        var b = Encoding.UTF8.GetBytes(header);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
