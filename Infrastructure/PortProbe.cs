using System.Net;
using System.Net.Sockets;

namespace DayZPulse.Infrastructure;

/// <summary>Проверка доступности TCP-порта и подбор свободных портов.</summary>
public static class PortProbe
{
    /// <summary>Порт свободен, если его удаётся занять.</summary>
    public static bool IsAvailable(int port, string address = "127.0.0.1")
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        return IsAvailable(port, IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Loopback);
    }

    public static bool IsAvailable(int port, IPAddress address)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* уже остановлен */ }
        }
    }

    /// <summary>Весь блок портов сервера свободен.</summary>
    public static bool IsBlockAvailable(int firstPort, int count, string address = "127.0.0.1")
    {
        for (var offset = 0; offset < count; offset++)
        {
            if (!IsAvailable(firstPort + offset, address))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Ищет ближайший свободный порт, начиная с указанного.</summary>
    public static int FindFree(int start, string address = "127.0.0.1", int attempts = 50)
    {
        var port = Math.Clamp(start, 1, 65535);

        for (var i = 0; i < attempts && port <= 65535; i++, port++)
        {
            if (IsAvailable(port, address))
            {
                return port;
            }
        }

        return 0;
    }

    /// <summary>Ищет свободный блок портов для сервера DayZ (игра, запросы, RCON…).</summary>
    public static int FindFreeBlock(int start, int count = 5, string address = "127.0.0.1", int attempts = 60)
    {
        var port = Math.Clamp(start, 1, 65535);

        for (var i = 0; i < attempts && port + count - 1 <= 65535; i++, port += 10)
        {
            if (IsBlockAvailable(port, count, address))
            {
                return port;
            }
        }

        return 0;
    }
}
