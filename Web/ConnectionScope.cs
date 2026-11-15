using System.Net;
using System.Net.Sockets;

namespace DayZPulse.Web;

/// <summary>Откуда пришёл запрос к панели.</summary>
public enum ConnectionScope
{
    /// <summary>С этой же машины (loopback).</summary>
    Local,

    /// <summary>Из локальной сети по приватному адресу.</summary>
    PrivateNetwork,

    /// <summary>Извне: публичный адрес или туннель.</summary>
    External
}

/// <summary>
/// Определяет, откуда пришёл запрос. Панель работает локально, поэтому
/// при внешнем доступе требуется защищённый туннель (HTTPS).
/// </summary>
public static class ConnectionScopeDetector
{
    public static ConnectionScope Classify(IPAddress? address)
    {
        if (address is null)
        {
            // Без адреса (например, запрос из самого процесса) считаем локальным.
            return ConnectionScope.Local;
        }

        if (IPAddress.IsLoopback(address))
        {
            return ConnectionScope.Local;
        }

        var normalized = Normalize(address);

        if (IsPrivate(normalized))
        {
            return ConnectionScope.PrivateNetwork;
        }

        return ConnectionScope.External;
    }

    /// <summary>Приводит IPv4-mapped IPv6 (::ffff:127.0.0.1) к обычному IPv4.</summary>
    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return true;
            }

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            // 169.254.0.0/16 — link-local (APIPA).
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            // 100.64.0.0/10 — CGNAT, часто встречается у туннелей.
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }

            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 — unique local addresses.
            if (address.IsIPv6UniqueLocal)
            {
                return true;
            }

            // fe80::/10 — link-local.
            if (address.IsIPv6LinkLocal)
            {
                return true;
            }

            return false;
        }

        return false;
    }
}
