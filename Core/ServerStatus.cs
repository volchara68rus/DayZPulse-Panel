namespace DayZPulse.Core;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Restarting,
    Crashed,
    Failed
}

public sealed record ServerStatus
{
    public ServerState State { get; init; } = ServerState.Stopped;
    public int? ProcessId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? LastError { get; init; }
    public string? LastMessage { get; init; }
    public bool AutoRestartArmed { get; init; }
    public int AutoRestartsInWindow { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public int ThreadCount { get; init; }
    public DateTimeOffset? LastSampleAt { get; init; }

    public TimeSpan? Uptime => StartedAt is null
        ? null
        : (State is ServerState.Running or ServerState.Stopping ? DateTimeOffset.Now - StartedAt.Value : StoppedAt - StartedAt);
}
