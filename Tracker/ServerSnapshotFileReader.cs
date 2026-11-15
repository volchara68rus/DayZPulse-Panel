using System.Text.Json;
using DayZPulse.Core;
using DayZPulse.Models;

namespace DayZPulse.Tracker;

/// <summary>
/// Читает снимок позиций из файла, который пишет мод (файловый режим).
///
/// Зачем: <c>POST</c> из Enforce Script синхронный и блокирует игровой поток
/// на время запроса — при интервале 2 с это заметно бьёт по FPS сервера.
/// Мод пишет снимок в <c>$profile:DayZPulseCore/players.json</c> обычной
/// файловой операцией, а панель читает его раз в секунду. HTTP остаётся
/// только для команд (выдача предметов, сообщения).
/// </summary>
public sealed class ServerSnapshotFileReader
{
    private readonly ILogger _logger;

    /// <summary>Когда файл читали в последний раз и его размер/время изменения.</summary>
    private readonly Dictionary<string, (DateTime Time, long Size)> _lastRead =
        new(StringComparer.OrdinalIgnoreCase);

    public ServerSnapshotFileReader(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Путь к файлу снимка: <c>$profile:DayZPulseCore/players.json</c>.</summary>
    public static string SnapshotPath(ServerProfile profile)
    {
        var root = ModConfigWriter.ProfilesRoot(profile);
        return Path.Combine(root, "DayZPulseCore", "players.json");
    }

    /// <summary>
    /// Если файл появился или изменился — разбирает и применяет снимок.
    /// Молча ничего не делает, если файла нет (сервер работает по HTTP-режиму).
    /// </summary>
    public void TryRead(ServerRuntime runtime, TrackerService service)
    {
        var path = SnapshotPath(runtime.Profile);

        if (!File.Exists(path))
        {
            return;
        }

        var info = new FileInfo(path);

        // Читаем только при изменении файла, иначе каждую секунду парсили бы
        // один и тот же JSON.
        if (_lastRead.TryGetValue(runtime.Id, out var last) &&
            last.Time == info.LastWriteTimeUtc && last.Size == info.Length)
        {
            return;
        }

        string json;
        try
        {
            // Файл может быть занят на запись — открываем с общим доступом.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var server = TrackerProtocol.ParseServer(root);
            var players = TrackerProtocol.ParsePlayers(root);

            service.ApplyFileSnapshot(runtime, server, players);

            _lastRead[runtime.Id] = (info.LastWriteTimeUtc, info.Length);
        }
        catch (JsonException)
        {
            // Файл ещё дописывается — попробуем на следующем тике.
        }
    }
}
