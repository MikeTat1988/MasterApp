using MasterApp.LifeJournal;

namespace MasterApp.Models;

public sealed class AppSettings
{
    public string CloudflaredPath { get; set; } = @"C:\Program Files (x86)\cloudflared\cloudflared.exe";
    public string IncomingFolder { get; set; } = @"C:\MasterApp\Incoming";
    public string ProcessedFolder { get; set; } = @"C:\MasterApp\Processed";
    public string FailedFolder { get; set; } = @"C:\MasterApp\Failed";
    public string PublishedFolder { get; set; } = @"C:\MasterApp\Published";
    public bool AutoStartTunnel { get; set; } = false;
    public string LogLevel { get; set; } = "Info";
    public int PackageScanIntervalSeconds { get; set; } = 5;
    public string RuntimeMode { get; set; } = "standard";
    public int PreferredLocalPort { get; set; } = 19057;
    public int LocalPortFallbackCount { get; set; } = 20;
    public bool WifiOnly { get; set; } = true;
    public int SessionQrTtlSeconds { get; set; } = 60;
    public LifeJournalSettings LifeJournal { get; set; } = new();

    public static AppSettings CreateDefault() => new();
}
