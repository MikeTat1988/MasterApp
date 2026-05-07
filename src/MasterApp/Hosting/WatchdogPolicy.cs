using MasterApp.Models;

namespace MasterApp.Hosting;

public static class WatchdogPolicy
{
    public const int MaxConsecutiveLaunchFailures = 3;
    public static readonly TimeSpan RetryWindow = TimeSpan.FromHours(2);
    public static readonly TimeSpan LaunchProbeWindow = TimeSpan.FromSeconds(90);

    public static bool WasExplicitQuit(ShutdownIntentRecord? intent)
    {
        return string.Equals(intent?.Reason, "quit", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanAttemptRestart(WatchdogStateRecord? state, DateTimeOffset nowUtc)
    {
        if (state is null)
        {
            return true;
        }

        if (state.ConsecutiveLaunchFailures < MaxConsecutiveLaunchFailures)
        {
            return true;
        }

        if (state.LastFailureAtUtc is null)
        {
            return false;
        }

        return nowUtc - state.LastFailureAtUtc.Value >= RetryWindow;
    }

    public static WatchdogStateRecord RegisterLaunchFailure(WatchdogStateRecord? state, DateTimeOffset nowUtc)
    {
        var next = Clone(state);
        if (next.LastFailureAtUtc is null || nowUtc - next.LastFailureAtUtc.Value >= RetryWindow)
        {
            next.ConsecutiveLaunchFailures = 0;
            next.FirstFailureAtUtc = nowUtc;
        }

        next.ConsecutiveLaunchFailures++;
        next.LastLaunchAttemptAtUtc = nowUtc;
        next.LastFailureAtUtc = nowUtc;
        next.FirstFailureAtUtc ??= nowUtc;
        return next;
    }

    public static WatchdogStateRecord RegisterLaunchSuccess(WatchdogStateRecord? state, DateTimeOffset nowUtc)
    {
        var next = Clone(state);
        next.ConsecutiveLaunchFailures = 0;
        next.FirstFailureAtUtc = null;
        next.LastFailureAtUtc = null;
        next.LastLaunchAttemptAtUtc = nowUtc;
        next.LastSuccessfulLaunchAtUtc = nowUtc;
        return next;
    }

    private static WatchdogStateRecord Clone(WatchdogStateRecord? state)
    {
        if (state is null)
        {
            return new WatchdogStateRecord();
        }

        return new WatchdogStateRecord
        {
            ConsecutiveLaunchFailures = state.ConsecutiveLaunchFailures,
            FirstFailureAtUtc = state.FirstFailureAtUtc,
            LastFailureAtUtc = state.LastFailureAtUtc,
            LastLaunchAttemptAtUtc = state.LastLaunchAttemptAtUtc,
            LastSuccessfulLaunchAtUtc = state.LastSuccessfulLaunchAtUtc
        };
    }
}
