namespace MasterApp.Models;

public sealed class AppPaths
{
    public required string RootDirectory { get; init; }
    public required string StateDirectory { get; init; }
    public required string BackupsDirectory { get; init; }
    public required string LogsDirectory { get; init; }
    public required string TempDirectory { get; init; }
    public required string AppsDirectory { get; init; }
    public required string AppSpecsDirectory { get; init; }
    public required string SettingsFile { get; init; }
    public required string SecretsFile { get; init; }
    public required string RuntimeStateFile { get; init; }
    public required string RelaunchStateFile { get; init; }
}
