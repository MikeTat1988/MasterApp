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
    public string CodexCommand { get; set; } = "codex";
    public List<string> WorkspacePaths { get; set; } = new();
    public string PreferredBuildCommand { get; set; } = string.Empty;
    public string PreferredRestartCommand { get; set; } = string.Empty;
    public bool PreferCodexJsonOutput { get; set; } = true;
    public int CodexHistoryLimit { get; set; } = 8;
    public int ConfigBackupRetentionCount { get; set; } = 10;

    public static AppSettings CreateDefault() => new();
}
