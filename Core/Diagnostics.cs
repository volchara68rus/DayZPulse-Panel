using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DayZPulse.Core;

public interface IEncodingResolver
{
    Encoding Resolve(string? name);
}

/// <summary>Разрешает кодировку вывода дочернего процесса с поддержкой русских вариантов.</summary>
public sealed class EncodingResolver : IEncodingResolver
{
    public Encoding Resolve(string? name)
    {
        var key = (name ?? "utf-8").Trim().ToLowerInvariant();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            return key switch
            {
                "utf-8" or "utf8" => new UTF8Encoding(false),
                "utf-16" or "utf16" or "unicode" => Encoding.Unicode,
                "windows-1251" or "cp1251" or "1251" => Encoding.GetEncoding(1251),
                "oem" or "866" or "cp866" => Encoding.GetEncoding(866),
                "system" or "default" => Encoding.Default,
                _ => Encoding.GetEncoding(key)
            };
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }

    public static IReadOnlyList<string> Supported { get; } =
        ["utf-8", "windows-1251", "oem", "system", "utf-16"];
}

/// <summary>Подсчёт загрузки CPU процесса между вызовами.</summary>
public static class CpuSampler
{
    private static readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> Previous = new();
    private static readonly object Gate = new();

    public static double Sample(Process process)
    {
        var cpu = process.TotalProcessorTime;
        var now = DateTimeOffset.UtcNow;

        TimeSpan previousCpu;
        DateTimeOffset previousAt;
        lock (Gate)
        {
            if (Previous.TryGetValue(process.Id, out var prev))
            {
                previousCpu = prev.Cpu;
                previousAt = prev.At;
            }
            else
            {
                previousCpu = cpu;
                previousAt = now;
            }

            Previous[process.Id] = (cpu, now);

            if (Previous.Count > 64)
            {
                var stale = Previous.Keys.Where(id => id != process.Id).Take(Previous.Count - 32).ToArray();
                foreach (var id in stale)
                {
                    Previous.Remove(id);
                }
            }
        }

        var elapsed = (now - previousAt).TotalMilliseconds;
        if (elapsed <= 0)
        {
            return 0;
        }

        var used = (cpu - previousCpu).TotalMilliseconds;
        var percent = used / (elapsed * Environment.ProcessorCount) * 100.0;
        return Math.Clamp(Math.Round(percent, 1), 0, 100 * Environment.ProcessorCount);
    }

    public static void Forget(int processId)
    {
        lock (Gate)
        {
            Previous.Remove(processId);
        }
    }
}

/// <summary>Сведения о системе хоста.</summary>
public static class SystemInfo
{
    public static object Snapshot()
    {
        var gcInfo = GC.GetGCMemoryInfo();

        return new
        {
            machineName = Environment.MachineName,
            osVersion = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            is64Bit = Environment.Is64BitOperatingSystem,
            totalMemoryBytes = gcInfo.TotalAvailableMemoryBytes,
            appMemoryBytes = GC.GetTotalMemory(false),
            appUptimeSeconds = Environment.TickCount64 / 1000.0,
            processId = Environment.ProcessId,
            userName = Environment.UserName,
            startTime = Process.GetCurrentProcess().StartTime
        };
    }
}
