using System.Text.RegularExpressions;

namespace DayZPulse.Security;

/// <summary>Требования к паролю владельца панели.</summary>
public static partial class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 200;

    private static readonly string[] CommonPasswords =
    [
        "password", "passw0rd", "123456", "12345678", "123456789", "qwerty", "qwerty123",
        "admin", "admin123", "administrator", "letmein", "welcome", "root", "toor",
        "test1234", "iloveyou", "monkey", "dragon", "master", "shadow"
    ];

    /// <summary>Возвращает текст ошибки или null, если пароль допустим.</summary>
    public static string? Validate(string? password, string? username)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Пароль не указан.";
        }

        if (password.Length < MinimumLength)
        {
            return $"Пароль должен содержать минимум {MinimumLength} символов.";
        }

        if (password.Length > MaximumLength)
        {
            return $"Пароль слишком длинный (максимум {MaximumLength} символов).";
        }

        if (password.Any(char.IsControl))
        {
            return "Пароль не должен содержать управляющих символов.";
        }

        var lower = password.ToLowerInvariant();

        // Панель работает локально, поэтому достаточно двух групп символов.
        var classes = CountClasses(password);
        if (classes < 2)
        {
            return "Пароль должен содержать минимум две группы символов из: строчные, прописные, цифры, знаки.";
        }

        if (!string.IsNullOrWhiteSpace(username) &&
            username.Length >= 3 &&
            lower.Contains(username.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return "Пароль не должен содержать имя пользователя.";
        }

        // Запрещаем только полное совпадение с частым паролем или его явное преобладание.
        foreach (var common in CommonPasswords)
        {
            if (lower == common)
            {
                return "Пароль слишком простой: выберите другой.";
            }

            if (!lower.Contains(common, StringComparison.Ordinal))
            {
                continue;
            }

            var dominance = (double)common.Length / password.Length;
            if (dominance > 0.7)
            {
                return "Пароль слишком простой: содержит распространённое слово.";
            }
        }

        // Совсем однообразные пароли вроде «aaaaaaaa» отклоняем.
        if (password.Distinct().Count() < 3)
        {
            return "Пароль слишком однообразный: используйте больше разных символов.";
        }

        return null;
    }

    private static int CountClasses(string password)
    {
        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;
        return classes;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    public static partial Regex UsernamePattern { get; }
}
