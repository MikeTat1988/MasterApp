using MasterApp.Packages;

namespace MasterApp.Models;

public sealed class RuntimeState
{
    public Dictionary<string, InstalledAppState> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PackageInstallResult? LastPackageResult { get; set; }
    public PublishResult? LastPublishResult { get; set; }
    public DateTimeOffset? LastPackageScanAtUtc { get; set; }
    public string? LastPackageScanReason { get; set; }
    public TunnelProcessState TunnelProcess { get; set; } = new();
}

public sealed class TunnelProcessState
{
    public int? ProcessId { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
}

public sealed class InstalledAppState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ActiveVersion { get; set; } = string.Empty;
    public int? AssignedPort { get; set; }
    public List<string> Versions { get; set; } = new();
    public DateTimeOffset InstalledAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public AppManifest Manifest { get; set; } = new();
    public AppRunState RunState { get; set; } = new();
    public PublishArtifactInfo? LastPublishedArtifact { get; set; }
}

public sealed class AppRunState
{
    public string Status { get; set; } = "stopped";
    public bool IsRunning { get; set; }
    public int? ProcessId { get; set; }
    public int? Port { get; set; }
    public string? Url { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? StoppedAtUtc { get; set; }
}

public sealed class PackageInstallResult
{
    public bool Success { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    public string? AppId { get; set; }
    public string? Version { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? InstalledPath { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PublishResult
{
    public bool Success { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string Message { get; set; } = string.Empty;
    public PublishArtifactInfo? Artifact { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PublishArtifactInfo
{
    public string ArtifactKind { get; set; } = "unknown";
    public string OutputPath { get; set; } = string.Empty;
    public string? ZipPath { get; set; }
}
