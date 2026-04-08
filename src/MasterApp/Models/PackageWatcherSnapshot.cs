namespace MasterApp.Models;

public sealed class PackageWatcherSnapshot
{
    public bool IsRunning { get; set; }
    public DateTimeOffset? LastScanAtUtc { get; set; }
    public string LastScanReason { get; set; } = "none";
    public int IntervalSeconds { get; set; }
}
