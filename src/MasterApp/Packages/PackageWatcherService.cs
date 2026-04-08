using MasterApp.Diagnostics;
using MasterApp.Models;

namespace MasterApp.Packages;

public sealed class PackageWatcherService : IDisposable
{
    private readonly PackageManager _packageManager;
    private readonly FileLogManager _log;
    private readonly int _intervalSeconds;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private System.Threading.Timer? _timer;
    private DateTimeOffset? _lastScanAtUtc;
    private string _lastScanReason = "none";

    public PackageWatcherService(PackageManager packageManager, FileLogManager log, int intervalSeconds)
    {
        _packageManager = packageManager;
        _log = log;
        _intervalSeconds = Math.Max(2, intervalSeconds);
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _timer = new System.Threading.Timer(_ => _ = RunScanAsync("timer"), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(_intervalSeconds));
        IsRunning = true;
        _log.Info("PackageWatcher", $"Package watcher started. Interval={_intervalSeconds}s");
        _ = RunScanAsync("startup");
    }

    public async Task<OperationResult> ScanNowAsync(string reason)
    {
        return await RunScanAsync(reason);
    }

    public PackageWatcherSnapshot Snapshot()
    {
        return new PackageWatcherSnapshot
        {
            IsRunning = IsRunning,
            IntervalSeconds = _intervalSeconds,
            LastScanAtUtc = _lastScanAtUtc,
            LastScanReason = _lastScanReason
        };
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _timer?.Dispose();
        _timer = null;
        IsRunning = false;
        _log.Info("PackageWatcher", "Package watcher stopped.");
    }

    private async Task<OperationResult> RunScanAsync(string reason)
    {
        if (!await _scanLock.WaitAsync(0))
        {
            return OperationResult.Failure("Scan skipped because another scan is already running.");
        }

        try
        {
            _lastScanReason = reason;
            _lastScanAtUtc = DateTimeOffset.UtcNow;
            return _packageManager.ScanIncoming(reason);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public void Dispose()
    {
        Stop();
        _scanLock.Dispose();
    }
}
