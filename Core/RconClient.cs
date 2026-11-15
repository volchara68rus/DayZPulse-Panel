using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DayZPulse.Core;

/// <summary>
/// Клиент BattlEye RCON Protocol v2 (UDP).
/// Спецификация: https://www.battleye.com/downloads/BERConProtocol.txt
/// </summary>
public sealed class RconClient : IAsyncDisposable
{
    private const byte PacketLogin = 0x00;
    private const byte PacketCommand = 0x01;
    private const byte PacketServerMessage = 0x02;
    private const byte HeaderMarker = 0xFF;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _endpoint;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<byte, PendingCommand> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // BattlEye принимает команды только с ожидаемым номером последовательности:
    // первый пакет после входа должен иметь seq 0, далее строго +1.
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private readonly ILogger _logger;
    private Task? _receiveTask;
    private Task? _keepAliveTask;
    private TaskCompletionSource<bool>? _loginTcs;
    private byte _sequence;
    private DateTime _lastSendUtc = DateTime.MinValue;
    private int _disposed;

    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public DateTimeOffset? LastMessageAt { get; private set; }

    /// <summary>Сообщение из консоли сервера (пакет 0x02).</summary>
    public event Action<string>? ServerMessage;

    public RconClient(string host, int port, ILogger logger)
    {
        _logger = logger;
        Host = host;
        Port = port;

        var address = Resolve(host);
        _endpoint = new IPEndPoint(address, port);
        _udp = new UdpClient(address.AddressFamily)
        {
            Client =
            {
                ReceiveBufferSize = 1 << 20,
                SendBufferSize = 1 << 20
            }
        };

        // UDP-сокет намеренно не «подключаем»: у подключённого UDP-сокета ICMP
        // «порт недоступен» (сервер ещё грузится или RCON выключен) приходит как
        // SocketException на Receive, и это выглядит как сетевая авария.
        // Без Connect мы просто не получаем ответ и ждём таймаут.
        var bindAddress = address.AddressFamily == AddressFamily.InterNetwork
            ? IPAddress.Any
            : IPAddress.IPv6Any;

        try { _udp.Client.Bind(new IPEndPoint(bindAddress, 0)); }
        catch (SocketException) { /* сокет забиндится при первой отправке */ }
    }

    public string Host { get; }
    public int Port { get; }

    private static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Вход в RCON. До 3 попыток, каждая с указанным таймаутом.</summary>
    public async Task<bool> LoginAsync(string password, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (IsConnected)
        {
            return true;
        }

        LastError = null;
        _receiveTask ??= Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _keepAliveTask ??= Task.Run(() => KeepAliveLoopAsync(_cts.Token), CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loginTcs = tcs;

            var passwordBytes = Encoding.UTF8.GetBytes(password ?? "");
            var payload = new byte[1 + passwordBytes.Length];
            payload[0] = PacketLogin;
            passwordBytes.CopyTo(payload, 1);

            try
            {
                await SendPayloadAsync(payload, ct);
            }
            catch (Exception ex)
            {
                LastError = "Не удалось отправить запрос входа: " + ex.Message;
                continue;
            }

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            if (finished == tcs.Task)
            {
                if (await tcs.Task)
                {
                    // Новая сессия — BattlEye снова ждёт команды начиная с seq 0.
                    _sequence = 0xFF;
                    IsConnected = true;
                    ConnectedAt = DateTimeOffset.UtcNow;
                    LastError = null;
                    return true;
                }

                LastError = "Неверный пароль RCON";
                return false;
            }
        }

        LastError ??= "RCON не отвечает: сервер ещё загружается или RCON отключён в BattlEye";
        return false;
    }

    /// <summary>Отправка команды с ожиданием ответа и одним повтором при потере UDP-пакета.</summary>
    public async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!IsConnected)
        {
            throw new InvalidOperationException(LastError ?? "Нет подключения к RCON");
        }

        // Команды уходят строго по одной: BattlEye ждёт последовательные номера пакетов.
        await _commandLock.WaitAsync(ct);
        try
        {
            return await SendCommandCoreAsync(command, timeout, ct);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<string> SendCommandCoreAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var commandBytes = Encoding.UTF8.GetBytes(command ?? "");
        var seq = NextSequence();
        var payload = new byte[2 + commandBytes.Length];
        payload[0] = PacketCommand;
        payload[1] = seq;
        commandBytes.CopyTo(payload, 2);

        var pendingCommand = new PendingCommand();
        _pending[seq] = pendingCommand;

        try
        {
            await SendPayloadAsync(payload, ct);
            var response = await WaitResponseAsync(pendingCommand, timeout, ct);

            if (response is null)
            {
                // UDP ненадёжен — повторяем запрос один раз.
                await SendPayloadAsync(payload, ct);
                response = await WaitResponseAsync(pendingCommand, timeout, ct);
            }

            if (response is null)
            {
                LastError = "RCON не ответил на команду";
                throw new TimeoutException($"RCON не ответил на команду за {timeout.TotalSeconds:0} с");
            }

            return response;
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    private async Task<string?> WaitResponseAsync(PendingCommand cmd, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var delay = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        var completed = await Task.WhenAny(cmd.Completion.Task, delay);
        if (completed == cmd.Completion.Task)
        {
            timeoutCts.Cancel();
            return await cmd.Completion.Task;
        }

        return null;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                // Сервер BattlEye поднимается позже самого DayZ: до этого порт
                // просто никто не слушает, и приходит ICMP. Не считаем это сбоем.
                try { await Task.Delay(300, ct); } catch { break; }
                continue;
            }
            catch { break; }

            // Пакеты принимаем только от RCON-сервера этого инстанса.
            if (!_endpoint.Equals(result.RemoteEndPoint))
            {
                continue;
            }

            var data = result.Buffer;
            if (data.Length < 7)
            {
                continue;
            }

            if (data[0] != 0x42 || data[1] != 0x45 || data[6] != HeaderMarker)
            {
                continue;
            }

            var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(2, 4));

            // CRC считается по байтам после поля CRC: 0xFF + payload.
            if (crc != RconCrc32.Compute(data.AsSpan(6)))
            {
                continue;
            }

            LastMessageAt = DateTimeOffset.UtcNow;
            HandlePayload(data.AsSpan(7));
        }
    }

    private void HandlePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        switch (payload[0])
        {
            case PacketLogin:
            {
                var ok = payload.Length >= 2 && payload[1] == 1;
                _loginTcs?.TrySetResult(ok);
                break;
            }

            case PacketCommand:
            {
                if (payload.Length < 2)
                {
                    return;
                }

                var seq = payload[1];
                if (!_pending.TryGetValue(seq, out var cmd))
                {
                    return;
                }

                // Команда без ответа (например say).
                if (payload.Length == 2)
                {
                    _pending.TryRemove(seq, out _);
                    cmd.Completion.TrySetResult("");
                    return;
                }

                var body = payload[2..];

                // Многочастный ответ: 0x00 | всего | индекс | данные.
                if (body.Length >= 3 && body[0] == 0x00)
                {
                    int total = body[1];
                    int index = body[2];
                    cmd.AddFragment(total, index, Encoding.UTF8.GetString(body[3..]));

                    if (cmd.IsComplete)
                    {
                        _pending.TryRemove(seq, out _);
                        cmd.Completion.TrySetResult(cmd.GetResult());
                    }

                    return;
                }

                _pending.TryRemove(seq, out _);
                cmd.Completion.TrySetResult(Encoding.UTF8.GetString(body));
                break;
            }

            case PacketServerMessage:
            {
                if (payload.Length < 2)
                {
                    return;
                }

                var seq = payload[1];
                var ack = new byte[] { PacketServerMessage, seq };
                _ = SendPayloadAsync(ack, _cts.Token);

                if (payload.Length > 2)
                {
                    var text = Encoding.UTF8.GetString(payload[2..]);
                    try { ServerMessage?.Invoke(text); } catch { /* подписчик не должен ронять приём */ }
                }

                break;
            }
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }

            if (!IsConnected)
            {
                continue;
            }

            if ((DateTime.UtcNow - _lastSendUtc).TotalSeconds < 25)
            {
                continue;
            }

            try
            {
                // Пустой 2-байтный command-пакет продлевает сессию (по спецификации).
                await _commandLock.WaitAsync(ct);
                try
                {
                    var payload = new byte[] { PacketCommand, NextSequence() };
                    await SendPayloadAsync(payload, ct);
                }
                finally
                {
                    _commandLock.Release();
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* keep-alive не критичен */ }
        }
    }

    private async Task SendPayloadAsync(byte[] payload, CancellationToken ct)
    {
        var packet = BuildPacket(payload);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _udp.SendAsync(packet, packet.Length, _endpoint);
            _lastSendUtc = DateTime.UtcNow;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static byte[] BuildPacket(ReadOnlySpan<byte> payload)
    {
        var packet = new byte[7 + payload.Length];
        packet[0] = 0x42; // 'B'
        packet[1] = 0x45; // 'E'

        // CRC считается по байтам после поля CRC: 0xFF + payload.
        var crcSource = new byte[1 + payload.Length];
        crcSource[0] = HeaderMarker;
        payload.CopyTo(crcSource.AsSpan(1));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), RconCrc32.Compute(crcSource));

        packet[6] = HeaderMarker;
        payload.CopyTo(packet.AsSpan(7));
        return packet;
    }

    private byte NextSequence()
    {
        for (var i = 0; i < 256; i++)
        {
            _sequence = (byte)((_sequence + 1) & 0xFF);
            if (!_pending.ContainsKey(_sequence))
            {
                return _sequence;
            }
        }

        throw new InvalidOperationException("Слишком много одновременных команд RCON");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RconClient));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IsConnected = false;
        try { _cts.Cancel(); } catch { /* уже отменено */ }
        try { _udp.Dispose(); } catch { /* уже закрыт */ }

        foreach (var cmd in _pending.Values)
        {
            cmd.Completion.TrySetException(new ObjectDisposedException(nameof(RconClient)));
        }

        _pending.Clear();
        _loginTcs?.TrySetResult(false);

        var tasks = new[] { _receiveTask, _keepAliveTask }
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();

        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* задачи могли уже завершиться */ }
        }

        _sendLock.Dispose();
        _commandLock.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Разбор ответа команды players.
    /// Формат строки: 0  1.2.3.4:2304  45  &lt;guid&gt;  Имя игрока
    /// </summary>
    public static List<RconPlayer> ParsePlayers(string response)
    {
        var players = new List<RconPlayer>();
        if (string.IsNullOrWhiteSpace(response))
        {
            return players;
        }

        var regex = new Regex(
            @"^\s*(?<num>\d+)\s+(?<ip>[0-9a-fA-F\.\:]+):(?<port>\d+)\s+(?<ping>\d+)\s+(?<guid>\S+)(?:\s+(?<name>.*?))?\s*$",
            RegexOptions.Compiled);

        foreach (var raw in response.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var m = regex.Match(line);
            if (!m.Success)
            {
                continue;
            }

            // BattlEye помечает неподтверждённый GUID суффиксом «(?)»:
            //   09b0dafff40a833c8a5645a840c4d114(?)
            // Суффикс не часть GUID — иначе он попадает в ключ записи.
            var guid = StripGuidSuffix(m.Groups["guid"].Value.Trim());

            players.Add(new RconPlayer
            {
                Number = ParseInt(m.Groups["num"].Value),
                Ip = m.Groups["ip"].Value.Trim(),
                Port = ParseInt(m.Groups["port"].Value),
                Ping = ParseInt(m.Groups["ping"].Value),
                Guid = guid,
                Name = m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : ""
            });
        }

        return players;
    }

    /// <summary>Убирает из GUID служебный суффикс BattlEye «(?)» (неподтверждённый GUID).</summary>
    public static string StripGuidSuffix(string guid)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return guid;
        }

        var g = guid.Trim();
        var cut = g.IndexOf('(');
        if (cut >= 0)
        {
            g = g[..cut];
        }

        return g.Trim();
    }

    private static int ParseInt(string value) => int.TryParse(value, out var result) ? result : 0;

    private sealed class PendingCommand
    {
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Total { get; private set; } = 1;
        public ConcurrentDictionary<int, string> Fragments { get; } = new();

        public bool IsComplete => Total > 0 && Fragments.Count >= Total;

        public void AddFragment(int total, int index, string text)
        {
            Total = Math.Max(1, total);
            Fragments[index] = text;
        }

        public string GetResult()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Total; i++)
            {
                sb.Append(Fragments.TryGetValue(i, out var part) ? part : "");
            }

            return sb.ToString();
        }
    }
}

public sealed class RconPlayer
{
    public int Number { get; set; }
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public int Ping { get; set; }
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>CRC32 (полином 0xEDB88320), как в BattlEye RCON.</summary>
public static class RconCrc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var j = 0; j < 8; j++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320 : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        return crc ^ 0xFFFFFFFF;
    }
}
