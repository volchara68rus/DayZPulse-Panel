namespace DayZPulse.Core;

/// <summary>
/// Строка консоли сервера.
/// <paramref name="OwnerOnly"/> — строку видит только владелец панели:
/// туда попадают технические данные (пути, команда запуска, SteamID),
/// которые администраторам с ограниченными правами знать не нужно.
/// </summary>
public sealed record ConsoleLine(long Sequence, DateTimeOffset Timestamp, string Stream, string Text, bool OwnerOnly = false);

/// <summary>Кольцевой буфер последних строк консоли сервера с монотонными номерами.</summary>
public sealed class ConsoleBuffer
{
    private readonly object _gate = new();
    private readonly Queue<ConsoleLine> _lines;
    private readonly int _capacity;
    private long _sequence;

    public ConsoleBuffer(int capacity)
    {
        _capacity = Math.Clamp(capacity, 100, 200_000);
        _lines = new Queue<ConsoleLine>(Math.Min(_capacity, 4096));
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public ConsoleLine Add(string stream, string text, bool ownerOnly = false)
    {
        var line = new ConsoleLine(++_sequence, DateTimeOffset.Now, stream, text, ownerOnly);
        lock (_gate)
        {
            while (_lines.Count >= _capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
        }

        return line;
    }

    public IReadOnlyList<ConsoleLine> Snapshot(int maxLines, bool includeOwnerOnly = true)
    {
        lock (_gate)
        {
            var source = includeOwnerOnly
                ? _lines
                : _lines.Where(l => !l.OwnerOnly);

            return maxLines <= 0 ? source.ToArray() : source.TakeLast(maxLines).ToArray();
        }
    }

    public IReadOnlyList<ConsoleLine> Since(long sequence, int maxLines, bool includeOwnerOnly = true)
    {
        lock (_gate)
        {
            var result = _lines
                .Where(l => l.Sequence > sequence)
                .Where(l => includeOwnerOnly || !l.OwnerOnly)
                .ToArray();

            return result.Length <= maxLines ? result : result[^maxLines..];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }
}
