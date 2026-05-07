using MasterApp.Packages;
using MasterApp.Storage;
using System.Text.Json;

namespace MasterApp.Hosting;

public static class CodexWorkspacePolicy
{
    public const string HintsFileName = "masterapp.ai.json";

    public static int CalculateDecisionStepBudget(
        string? provider,
        string? taskMode,
        string? workspaceKind,
        string? prompt,
        int codexBaseBudget,
        int ollamaBaseBudget,
        bool isMasterAppWorkspace,
        bool hasExplicitWorkspaceHints)
    {
        var budget = IsOllamaProvider(provider) ? ollamaBaseBudget : codexBaseBudget;

        switch ((taskMode ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "investigate":
                budget += 12;
                break;
            case "code":
                budget += 10;
                break;
            case "ask":
                budget += 4;
                break;
        }

        if (string.Equals(workspaceKind, "installed-app", StringComparison.OrdinalIgnoreCase))
        {
            budget += 8;
        }

        if (isMasterAppWorkspace)
        {
            budget += 10;
        }

        var wordCount = CountWords(prompt);
        if (wordCount >= 12)
        {
            budget += 4;
        }

        if (wordCount >= 24)
        {
            budget += 4;
        }

        if (!hasExplicitWorkspaceHints &&
            !string.Equals(taskMode, "action", StringComparison.OrdinalIgnoreCase))
        {
            budget += 2;
        }

        var maxBudget = IsOllamaProvider(provider) ? 200 : 100;
        return Math.Clamp(budget, 8, maxBudget);
    }

    public static WorkspacePromptContext CreatePromptContext(
        string workspacePath,
        string? workspaceKind,
        string? appId,
        string? version,
        AppManifest? manifest)
    {
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var isMasterAppWorkspace = File.Exists(Path.Combine(fullWorkspacePath, "src", "MasterApp", "MasterApp.csproj"));
        var explicitHints = TryLoadHints(fullWorkspacePath);
        var rootFiles = SafeEnumerateNames(fullWorkspacePath, includeDirectories: false);
        var rootDirectories = SafeEnumerateNames(fullWorkspacePath, includeDirectories: true);
        var lines = new List<string>();

        lines.Add($"Workspace path: {fullWorkspacePath}");
        lines.Add($"Workspace kind: {workspaceKind ?? "workspace"}");

        if (!string.IsNullOrWhiteSpace(appId))
        {
            lines.Add($"App id: {appId}");
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            lines.Add($"App version: {version}");
        }

        if (rootDirectories.Count > 0)
        {
            lines.Add($"Top-level directories: {string.Join(", ", rootDirectories)}");
        }

        if (rootFiles.Count > 0)
        {
            lines.Add($"Top-level files: {string.Join(", ", rootFiles)}");
        }

        if (explicitHints is not null)
        {
            lines.Add("Workspace AI hints:");
            if (!string.IsNullOrWhiteSpace(explicitHints.Summary))
            {
                lines.Add($"- Summary: {explicitHints.Summary.Trim()}");
            }

            foreach (var entry in NormalizeList(explicitHints.PreferredEntryPoints).Take(5))
            {
                lines.Add($"- Preferred entry point: {entry}");
            }

            foreach (var file in NormalizeList(explicitHints.HotspotFiles).Take(8))
            {
                lines.Add($"- Hot file: {file}");
            }

            foreach (var hint in NormalizeList(explicitHints.InvestigationHints).Take(6))
            {
                lines.Add($"- Investigation hint: {hint}");
            }

            foreach (var avoid in NormalizeList(explicitHints.AvoidPatterns).Take(4))
            {
                lines.Add($"- Avoid: {avoid}");
            }
        }

        if (isMasterAppWorkspace)
        {
            lines.Add("MasterApp workspace defaults:");
            lines.Add("- Primary browser UI is under src/MasterApp/wwwroot.");
            lines.Add("- API endpoints and hosting orchestration live under src/MasterApp/Hosting.");
            lines.Add("- For button, icon, tab, dashboard, or Codex UI issues, start with src/MasterApp/wwwroot/masterapp-ui.js and src/MasterApp/wwwroot/masterapp-ui.css.");
        }

        if (string.Equals(workspaceKind, "installed-app", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("Installed app defaults:");
            lines.Add("- Read app.manifest.json early; it defines package type, launch contract, build command, and publish command.");
            lines.Add("- If a fix changes the installed app itself, package a replacement zip before claiming success unless the user asked only for investigation.");
        }

        if (manifest is not null)
        {
            lines.Add($"Manifest appType: {manifest.AppType}");
            lines.Add($"Manifest launch kind: {manifest.Launch.Kind}");

            if (!string.IsNullOrWhiteSpace(manifest.Entry))
            {
                lines.Add($"Manifest entry: {manifest.Entry}");
            }

            if (!string.IsNullOrWhiteSpace(manifest.Launch.ExecutablePath))
            {
                lines.Add($"Manifest executable: {manifest.Launch.ExecutablePath}");
            }

            if (!string.IsNullOrWhiteSpace(manifest.Build?.InstallCommand))
            {
                lines.Add($"Manifest build command: {manifest.Build.InstallCommand}");
            }

            if (!string.IsNullOrWhiteSpace(manifest.Publish?.Command))
            {
                lines.Add($"Manifest publish command: {manifest.Publish.Command}");
            }
        }
        else
        {
            if (Directory.Exists(Path.Combine(fullWorkspacePath, "wwwroot")))
            {
                lines.Add("Detected wwwroot at workspace root; UI investigation should usually start there.");
            }

            if (File.Exists(Path.Combine(fullWorkspacePath, "package.json")))
            {
                lines.Add("Detected package.json at workspace root; inspect package scripts and frontend entry points before broad scans.");
            }

            if (Directory.EnumerateFiles(fullWorkspacePath, "*.csproj", SearchOption.TopDirectoryOnly).Any())
            {
                lines.Add("Detected a .csproj at workspace root; inspect the project file and Program.cs before broad scans.");
            }
        }

        return new WorkspacePromptContext
        {
            WorkspacePath = fullWorkspacePath,
            WorkspaceKind = workspaceKind ?? "workspace",
            AppId = appId,
            Version = version,
            IsMasterAppWorkspace = isMasterAppWorkspace,
            HasExplicitHints = explicitHints is not null,
            PromptLines = lines
        };
    }

    public static ApprovalWorkspaceAccess EvaluateApprovalWorkspaceAccess(
        string workingDirectory,
        IEnumerable<string> allowedWorkspacePaths)
    {
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        var allowed = allowedWorkspacePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var isAllowed = allowed.Contains(fullWorkingDirectory, StringComparer.OrdinalIgnoreCase);
        return new ApprovalWorkspaceAccess
        {
            WorkingDirectory = fullWorkingDirectory,
            IsAllowed = isAllowed,
            RequiresApproval = true,
            CanTrustWorkspace = !isAllowed,
            TrustWorkspacePath = isAllowed ? string.Empty : fullWorkingDirectory
        };
    }

    private static bool IsOllamaProvider(string? provider)
        => string.Equals((provider ?? string.Empty).Trim(), "ollama", StringComparison.OrdinalIgnoreCase);

    private static int CountWords(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static WorkspaceAiHints? TryLoadHints(string workspacePath)
    {
        try
        {
            var hintsPath = Path.Combine(workspacePath, HintsFileName);
            if (!File.Exists(hintsPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WorkspaceAiHints>(File.ReadAllText(hintsPath), JsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }

    private static List<string> SafeEnumerateNames(string workspacePath, bool includeDirectories)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(workspacePath)
                .Where(path =>
                {
                    var attributes = File.GetAttributes(path);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    return includeDirectories ? isDirectory : !isDirectory;
                })
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static IEnumerable<string> NormalizeList(IEnumerable<string>? values)
        => values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
           ?? Enumerable.Empty<string>();
}

public sealed class ApprovalWorkspaceAccess
{
    public string WorkingDirectory { get; init; } = string.Empty;
    public bool IsAllowed { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public bool CanTrustWorkspace { get; init; }
    public string TrustWorkspacePath { get; init; } = string.Empty;
}

public sealed class WorkspacePromptContext
{
    public string WorkspacePath { get; init; } = string.Empty;
    public string WorkspaceKind { get; init; } = "workspace";
    public string? AppId { get; init; }
    public string? Version { get; init; }
    public bool IsMasterAppWorkspace { get; init; }
    public bool HasExplicitHints { get; init; }
    public IReadOnlyList<string> PromptLines { get; init; } = Array.Empty<string>();
}

public sealed class WorkspaceAiHints
{
    public string? Summary { get; set; }
    public List<string> PreferredEntryPoints { get; set; } = new();
    public List<string> HotspotFiles { get; set; } = new();
    public List<string> InvestigationHints { get; set; } = new();
    public List<string> AvoidPatterns { get; set; } = new();
}
