using MasterApp.Diagnostics;
using MasterApp.Models;
using MasterApp.Storage;

namespace MasterApp.Bootstrap;

public sealed class BootstrapContext
{
    public required AppPaths Paths { get; init; }
    public required AppSettings Settings { get; init; }
    public required AppSecrets Secrets { get; init; }
    public required RuntimeStateStore RuntimeStateStore { get; init; }
    public required FileLogManager Log { get; init; }
    public required IReadOnlyList<string> ValidationIssues { get; init; }
}
