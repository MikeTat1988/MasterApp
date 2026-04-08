namespace MasterApp.Models;

public sealed class TunnelStatusSnapshot
{
    public string Status { get; set; } = "Stopped";
    public bool IsRunning { get; set; }
    public int? ProcessId { get; set; }
    public int? LastExitCode { get; set; }
    public string LastMessage { get; set; } = "Tunnel not started.";
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? StoppedAtUtc { get; set; }
}
