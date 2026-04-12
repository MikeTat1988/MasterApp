using MasterApp.Models;
using MasterApp.Storage;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
    private const int MaxLiveLogLines = 240;
    private const int MaxPersistedLogLines = 120;
    private const int MaxApprovalLogLines = 80;
    private const int MaxDecisionSteps = 6;
    private const int MaxPromptOutputCharacters = 8_000;

    private static readonly Regex ModelRegex = new(@"(?m)^\s*model\s*=\s*""(?<model>[^""]+)""\s*$", RegexOptions.Compiled);

    private readonly string _codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private readonly string _codexConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
    private readonly string _codexModelsCacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
    private readonly string _codexSessionIndexFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "session_index.jsonl");
    private readonly string _codexSessionsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    private void RefreshCliState()
    {
        var resolution = ResolveCliExecutable(_context.Settings.CodexCommand);
        CodexCliProbeState probe;

        if (string.Equals(resolution.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(resolution.ResolvedExecutablePath))
        {
            probe = ProbeCli(resolution.ResolvedExecutablePath!);
        }
        else
        {
            probe = new CodexCliProbeState
            {
                Status = "failed",
                IsReady = false,
                StructuredOutput = false,
                ProbedAtUtc = DateTimeOffset.UtcNow,
                LastError = resolution.LastError
            };
        }

        lock (_gate)
        {
            _resolutionState = resolution;
            _probeState = probe;
            _resolvedExecutablePath = resolution.ResolvedExecutablePath;
        }
    }

    private CodexCliResolutionState ResolveCliExecutable(string? configuredCommand)
    {
        var attempted = new List<string>();
        var resolvedPath = string.Empty;
        var requested = configuredCommand?.Trim();

        if (!string.IsNullOrWhiteSpace(requested))
        {
            attempted.Add(requested);
            if (TryResolveConfiguredPath(requested, out var configuredPath))
            {
                resolvedPath = configuredPath!;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            var sandboxPath = Path.Combine(_codexHome, ".sandbox-bin", "codex.exe");
            attempted.Add(sandboxPath);
            if (File.Exists(sandboxPath))
            {
                resolvedPath = sandboxPath;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            foreach (var match in FindCommandOnPath(string.IsNullOrWhiteSpace(requested) ? "codex" : requested!))
            {
                attempted.Add(match);
                if (File.Exists(match))
                {
                    resolvedPath = match;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return new CodexCliResolutionState
            {
                Status = "missing",
                ResolvedExecutablePath = null,
                AttemptedPaths = attempted.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                LastError = BuildResolutionError(attempted),
                ResolvedAtUtc = DateTimeOffset.UtcNow
            };
        }

        return new CodexCliResolutionState
        {
            Status = "ready",
            ResolvedExecutablePath = resolvedPath,
            AttemptedPaths = attempted.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            LastError = null,
            ResolvedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private CodexCliProbeState ProbeCli(string executablePath)
    {
        try
        {
            var help = RunProcessCapture(executablePath, new[] { "exec", "--help" }, Directory.GetCurrentDirectory(), 15_000);
            var version = RunProcessCapture(executablePath, new[] { "--version" }, Directory.GetCurrentDirectory(), 10_000);
            var combined = $"{help.StandardOutput}\n{help.StandardError}";
            var versionText = TrimForLog($"{version.StandardOutput} {version.StandardError}".Trim(), 120);

            if (help.ExitCode == 0 && combined.Contains("--json", StringComparison.OrdinalIgnoreCase))
            {
                return new CodexCliProbeState
                {
                    Status = "ready",
                    IsReady = true,
                    Mode = "exec-json",
                    StructuredOutput = true,
                    Version = versionText,
                    ProbedAtUtc = DateTimeOffset.UtcNow
                };
            }

            return new CodexCliProbeState
            {
                Status = "failed",
                IsReady = false,
                StructuredOutput = false,
                Version = versionText,
                ProbedAtUtc = DateTimeOffset.UtcNow,
                LastError = TrimForLog(combined, 300)
            };
        }
        catch (Exception ex)
        {
            return new CodexCliProbeState
            {
                Status = "failed",
                IsReady = false,
                StructuredOutput = false,
                ProbedAtUtc = DateTimeOffset.UtcNow,
                LastError = ex.Message
            };
        }
    }

    private string ResolveWorkspace(string? requestedPath)
    {
        var allowed = GetAllowedWorkspacePaths();

        if (allowed.Length == 0)
        {
            throw new InvalidOperationException("No workspace paths are configured in settings.json.");
        }

        var candidate = string.IsNullOrWhiteSpace(requestedPath)
            ? allowed[0]
            : Path.GetFullPath(requestedPath);

        if (!allowed.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Workspace is not allowed: {candidate}");
        }

        return candidate;
    }

    private string[] GetAllowedWorkspacePaths()
    {
        return GetWorkspaceChoiceRecords()
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => Path.GetFullPath(item.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<object> GetWorkspaceChoices()
    {
        return GetWorkspaceChoiceRecords()
            .Select(item => (object)new
            {
                path = item.Path,
                label = item.Label,
                kind = item.Kind,
                appId = item.AppId,
                version = item.Version
            })
            .ToArray();
    }

    private IReadOnlyList<CodexWorkspaceChoice> GetWorkspaceChoiceRecords()
    {
        var choices = new List<CodexWorkspaceChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _context.Settings.WorkspacePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var full = Path.GetFullPath(path);
            if (!seen.Add(full))
            {
                continue;
            }

            choices.Add(new CodexWorkspaceChoice
            {
                Path = full,
                Label = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Kind = "workspace"
            });
        }

        foreach (var app in _context.RuntimeStateStore.GetApps())
        {
            if (string.IsNullOrWhiteSpace(app.Id) || string.IsNullOrWhiteSpace(app.ActiveVersion))
            {
                continue;
            }

            var installRoot = Path.Combine(_context.Paths.AppsDirectory, app.Id, app.ActiveVersion);
            if (!Directory.Exists(installRoot))
            {
                continue;
            }

            var full = Path.GetFullPath(installRoot);
            if (!seen.Add(full))
            {
                continue;
            }

            choices.Add(new CodexWorkspaceChoice
            {
                Path = full,
                Label = string.IsNullOrWhiteSpace(app.Name) ? app.Id : app.Name,
                Kind = "installed-app",
                AppId = app.Id,
                Version = app.ActiveVersion
            });
        }

        return choices
            .OrderBy(item => string.Equals(item.Kind, "workspace", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string GetBuildCommand(string workspacePath, string runId)
    {
        var configured = _context.Settings.PreferredBuildCommand?.Trim();
        var command = string.IsNullOrWhiteSpace(configured)
            ? "dotnet build .\\src\\MasterApp\\MasterApp.csproj -c Debug"
            : configured;

        if ((command.Contains(" dotnet build ", StringComparison.OrdinalIgnoreCase) ||
             command.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase)) &&
            !command.Contains(" -o ", StringComparison.OrdinalIgnoreCase) &&
            !command.Contains(" --output ", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = Path.Combine(_context.Paths.TempDirectory, "codex-build", runId);
            Directory.CreateDirectory(outputPath);
            command += $" -o \"{outputPath}\"";
        }

        return command;
    }

    private string GetRestartCommand(string workspacePath)
    {
        return _runtime.GetRestartCommand(workspacePath);
    }

    private string? TryReadCurrentCodexModel()
    {
        try
        {
            if (!File.Exists(_codexConfigFile))
            {
                return null;
            }

            var content = File.ReadAllText(_codexConfigFile);
            var match = ModelRegex.Match(content);
            return match.Success ? match.Groups["model"].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteCurrentCodexModel(string model)
    {
        Directory.CreateDirectory(_codexHome);

        string content;
        if (File.Exists(_codexConfigFile))
        {
            content = File.ReadAllText(_codexConfigFile);
            if (ModelRegex.IsMatch(content))
            {
                content = ModelRegex.Replace(content, $"model = \"{model}\"", 1);
            }
            else
            {
                content = content.TrimEnd() + Environment.NewLine + $"model = \"{model}\"" + Environment.NewLine;
            }
        }
        else
        {
            content = $"model = \"{model}\"{Environment.NewLine}";
        }

        File.WriteAllText(_codexConfigFile, content, Encoding.UTF8);
    }

    private IReadOnlyList<CodexModelInfo> GetAvailableModels()
    {
        var combined = GetAvailableCodexModels()
            .Concat(GetAvailableOllamaModels())
            .GroupBy(model => $"{model.Provider}:{model.Slug}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return combined.Length > 0 ? combined : FallbackModelList();
    }

    private IReadOnlyList<CodexModelInfo> GetAvailableCodexModels()
    {
        try
        {
            if (!File.Exists(_codexModelsCacheFile))
            {
                return FallbackModelList();
            }

            var json = File.ReadAllText(_codexModelsCacheFile);
            var cache = JsonSerializer.Deserialize<CodexModelsCache>(json, JsonOptions.Default);
            var models = cache?.Models?
                .Where(model => !string.IsNullOrWhiteSpace(model.Slug))
                .Select(model => new CodexModelInfo
                {
                    Provider = CodexProvider,
                    Slug = model.Slug ?? string.Empty,
                    DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Slug ?? string.Empty : model.DisplayName!,
                    SupportedReasoningLevels = model.SupportedReasoningLevels?
                        .Where(level => !string.IsNullOrWhiteSpace(level.Effort))
                        .Select(level => level.Effort!)
                        .ToList() ?? new List<string>()
                })
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return models is { Length: > 0 } ? models : FallbackModelList();
        }
        catch
        {
            return FallbackModelList();
        }
    }

    private IReadOnlyList<CodexModelInfo> FallbackModelList()
    {
        var current = TryReadCurrentCodexModel();
        return string.IsNullOrWhiteSpace(current)
            ? Array.Empty<CodexModelInfo>()
            : new[]
            {
                new CodexModelInfo
                {
                    Provider = CodexProvider,
                    Slug = current,
                    DisplayName = current,
                    SupportedReasoningLevels = new List<string>()
                }
            };
    }

    private sealed class CodexWorkspaceChoice
    {
        public string Path { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Kind { get; init; } = "workspace";
        public string? AppId { get; init; }
        public string? Version { get; init; }
    }
}
