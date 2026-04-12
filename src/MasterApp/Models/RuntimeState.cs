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
    public List<CodexOperationRecord> CodexOperations { get; set; } = new();
    public CodexRuntimeState Codex { get; set; } = new();
    public RelaunchStatusRecord? LastRelaunch { get; set; }
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

public sealed class CodexRuntimeState
{
    public string CurrentProvider { get; set; } = "codex";
    public string CurrentModel { get; set; } = string.Empty;
    public string CurrentSessionId { get; set; } = string.Empty;
    public List<CodexChatSession> Sessions { get; set; } = new();
    public CodexChatRun? ActiveRun { get; set; }
    public CodexApprovalRequest? PendingApproval { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

public sealed class CodexChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public string Provider { get; set; } = "codex";
    public string Model { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<CodexChatMessage> Messages { get; set; } = new();
}

public sealed class CodexChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "assistant";
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = "completed";
    public string? RunId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CodexChatRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "idle";
    public string Provider { get; set; } = "codex";
    public string RequestedMode { get; set; } = "auto";
    public string TaskMode { get; set; } = "ask";
    public string TaskModeSource { get; set; } = "auto";
    public double TaskModeConfidence { get; set; }
    public string TaskModeReason { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? FailureMessage { get; set; }
    public List<string> ChangedFiles { get; set; } = new();
    public List<string> LogLines { get; set; } = new();
    public List<CodexApprovalRecord> ApprovalHistory { get; set; } = new();
    public CodexBuildResult? BuildResult { get; set; }
    public RelaunchStatusRecord? RestartStatus { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class CodexOperationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "idle";
    public string WorkspacePath { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string AssistantResponse { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string CliMode { get; set; } = "unknown";
    public bool UsedStructuredOutput { get; set; }
    public int? ExitCode { get; set; }
    public string? FailureMessage { get; set; }
    public List<string> ChangedFiles { get; set; } = new();
    public List<string> LogLines { get; set; } = new();
    public List<CodexFollowUpAction> FollowUps { get; set; } = new();
    public CodexBuildResult? BuildResult { get; set; }
    public RelaunchStatusRecord? RestartStatus { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class CodexFollowUpAction
{
    public string Kind { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class CodexApprovalRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string Kind { get; set; } = "command";
    public string Summary { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

public sealed class CodexApprovalRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "command";
    public string Summary { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Decision { get; set; } = "pending";
    public int? ExitCode { get; set; }
    public string OutputSummary { get; set; } = string.Empty;
    public List<string> LogLines { get; set; } = new();
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

public sealed class CodexBuildResult
{
    public string Status { get; set; } = "not-requested";
    public string Command { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int? ExitCode { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> LogLines { get; set; } = new();
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class RelaunchStatusRecord
{
    public string Status { get; set; } = "idle";
    public string Message { get; set; } = string.Empty;
    public string? BackupDirectory { get; set; }
    public string? Command { get; set; }
    public string? OperationId { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
