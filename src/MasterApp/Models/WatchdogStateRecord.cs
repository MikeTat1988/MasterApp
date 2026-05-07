namespace MasterApp.Models;

public sealed class ShutdownIntentRecord
{
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset SetAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WatchdogStateRecord
{
    public int ConsecutiveLaunchFailures { get; set; }
    public DateTimeOffset? FirstFailureAtUtc { get; set; }
    public DateTimeOffset? LastFailureAtUtc { get; set; }
    public DateTimeOffset? LastLaunchAttemptAtUtc { get; set; }
    public DateTimeOffset? LastSuccessfulLaunchAtUtc { get; set; }
}
