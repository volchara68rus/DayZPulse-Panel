using System.Security.Cryptography;
using System.Text;
using DayZPulse.Models;

namespace DayZPulse.Security;

/// <summary>Кто сейчас работает в панели и что ему разрешено.</summary>
public sealed class PanelIdentity
{
    public string Name { get; init; } = "";
    public bool IsOwner { get; init; }

    /// <summary>Разрешённые разделы (для владельца — все).</summary>
    public IReadOnlySet<string> Sections { get; init; } = new HashSet<string>();

    /// <summary>Разрешённые серверы; null — все серверы панели.</summary>
    public IReadOnlySet<string>? Servers { get; init; }

    public bool CanUseAllSections => IsOwner || Sections.Contains("*");

    public bool CanUseSection(string section) =>
        IsOwner || Sections.Contains("*") || Sections.Contains(section);

    public bool CanUseServer(string serverId) =>
        Servers is null || Servers.Contains(serverId);
}

/// <summary>
/// Выдача и проверка подписанных токенов сессии (HMAC-SHA256, без сторонних
/// зависимостей). Дополнительно ведёт учёт активных сессий, чтобы панель
/// показывала, кто из администраторов сейчас в сети.
/// </summary>
public sealed class SessionTokens
{
    private readonly byte[] _key;
    private readonly object _gate = new();

    // Имя пользователя → время последней активности в панели.
    private readonly Dictionary<string, DateTimeOffset> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сессия считается активной, если запрос был не позже этого срока.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);

    public SessionTokens()
    {
        // Ключ подписи сессий храним в каталоге данных панели, а не только в
        // памяти: иначе при перезапуске (в том числе при смене порта) все сессии
        // сбрасывались и пользователя выбрасывало на страницу входа.
        _key = LoadOrCreateKey();
    }

    /// <summary>Файл с ключом подписи сессий в каталоге данных панели.</summary>
    private static string KeyFile => Path.Combine(Infrastructure.AppPaths.DataRoot, "session.key");

    private static byte[] LoadOrCreateKey()
    {
        try
        {
            var path = KeyFile;
            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length >= 32)
                {
                    return existing;
                }
            }

            var created = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Convert.ToBase64String(created));
            return created;
        }
        catch
        {
            // Не удалось прочитать/записать — используем ключ на время процесса.
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    /// <summary>Имена пользователей с активной сессией прямо сейчас.</summary>
    public IReadOnlySet<string> ActiveUsers()
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow - ActiveWindow;
            return _active.Where(kv => kv.Value >= cutoff).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Touch(string user)
    {
        lock (_gate)
        {
            _active[user] = DateTimeOffset.UtcNow;
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        return Convert.FromBase64String(s);
    }

    public string Issue(string username, TimeSpan lifetime)
    {
        var payload = $"{username}\n{DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds()}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(_key, payloadBytes);
        Touch(username);
        return $"{Base64Url(payloadBytes)}.{Base64Url(signature)}";
    }

    /// <summary>Возвращает имя пользователя, если токен валиден и не истёк.</summary>
    public string? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = FromBase64Url(parts[0]);
            signature = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(_key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            return null;
        }

        var payload = Encoding.UTF8.GetString(payloadBytes);
        var separator = payload.LastIndexOf('\n');
        if (separator <= 0)
        {
            return null;
        }

        if (!long.TryParse(payload[(separator + 1)..], out var expiry))
        {
            return null;
        }

        var name = payload[..separator];
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
        {
            return null;
        }

        // Любой успешный запрос обновляет отметку активности.
        Touch(name);
        return name;
    }

    /// <summary>Разбирает сведения о владельце и админах и определяет права по имени.</summary>
    public static PanelIdentity Resolve(AppSettings settings, string? user)
    {
        var auth = settings.Auth;

        // Авторизация выключена — панель открыта, доступ как у владельца.
        if (!auth.Enabled)
        {
            return new PanelIdentity { Name = user ?? auth.OwnerName ?? auth.Username, IsOwner = true, Sections = new HashSet<string>(), Servers = null };
        }

        var name = user ?? "";

        if (name.Length > 0 &&
            string.Equals(name, auth.OwnerName ?? auth.Username, StringComparison.OrdinalIgnoreCase))
        {
            return new PanelIdentity { Name = name, IsOwner = true, Sections = new HashSet<string>(), Servers = null };
        }

        var admin = auth.Admins.FirstOrDefault(a =>
            string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase));

        if (admin is null)
        {
            // Аккаунт удалён, а сессия ещё жива — доступа нет.
            return new PanelIdentity { Name = name, IsOwner = false, Sections = new HashSet<string>(), Servers = new HashSet<string>() };
        }

        var sections = admin.AllSections
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" }
            : new HashSet<string>(admin.Sections, StringComparer.OrdinalIgnoreCase);

        IReadOnlySet<string>? servers = admin.AllServers
            ? null
            : new HashSet<string>(admin.Servers, StringComparer.OrdinalIgnoreCase);

        return new PanelIdentity { Name = name, IsOwner = false, Sections = sections, Servers = servers };
    }
}
