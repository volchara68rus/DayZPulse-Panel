using System.Security.Cryptography;

namespace DayZPulse.Security;

/// <summary>PBKDF2-хеширование паролей и сравнение за постоянное время.</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static (string Hash, string Salt, int Iterations) Create(string password, int iterations = 210_000)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Пароль не может быть пустым.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, iterations);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64))
        {
            return false;
        }

        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(hashBase64);
            salt = Convert.FromBase64String(saltBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(password, salt, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int size = KeySize) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Math.Clamp(iterations, 10_000, 2_000_000), HashAlgorithmName.SHA256, size);
}
