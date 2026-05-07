using MasterApp.Models;
using MasterApp.Packages;
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
    private const int MaxPromptOutputCharacters = 8_000;
    private static readonly Version Gpt55MinimumCliVersion = new(0, 128, 0);

    private static readonly Regex ModelRegex = new(@"(?m)^\s*model\s*=\s*""(?<model>[^""]+)""\s*$", RegexOptions.Compiled);
    private static readonly Regex VersionNumberRegex = new(@"(?<version>\d+\.\d+\.\d+)", RegexOptions.Compiled);

    private readonly string _codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private readonly string _codexConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
    private readonly string _codexModelsCacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
    private readonly string _codexSessionIndexFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "session_index.jsonl");
    private readonly string _codexSessionsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    private int GetMaxDecisionSteps(CodexChatRun run)
    {
        var workspace = FindWorkspaceChoice(run.WorkspacePath);
        var manifest = workspace is not null &&
                       string.Equals(workspace.Kind, "installed-app", StringComparison.OrdinalIgnoreCase)
            ? TryLoadWorkspaceManifest(run.WorkspacePath)
            : null;
        var promptContext = CodexWorkspacePolicy.CreatePromptContext(
            run.WorkspacePath,
            workspace?.Kind,
            workspace?.AppId,
            workspace?.Version,
            manifest);

        return CodexWorkspacePolicy.CalculateDecisionStepBudget(
            run.Provider,
            run.TaskMode,
            workspace?.Kind,
            run.Prompt,
            _context.Settings.CodexMaxDecisionSteps,
            _context.Settings.OllamaMaxDecisionSteps,
            promptContext.IsMasterAppWorkspace,
            promptContext.HasExplicitHints);
    }

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
        var resolution = CodexExecutableResolver.Resolve(configuredCommand);
        return new CodexCliResolutionState
        {
            Status = resolution.Status,
            ResolvedExecutablePath = resolution.ResolvedExecutablePath,
            AttemptedPaths = resolution.AttemptedPaths.ToList(),
            LastError = resolution.LastError,
            ResolvedAtUtc = resolution.ResolvedAtUtc
        };
    }

    private CodexCliProbeState ProbeCli(string executablePath)
    {
        try
        {
            var help = RunProcessCapture(executablePath, new[] { "exec", "--help" }, Directory.GetCurrentDirectory(), 15_000, Encoding.UTF8);
            var version = RunProcessCapture(executablePath, new[] { "--version" }, Directory.GetCurrentDirectory(), 10_000, Encoding.UTF8);
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
        var workspace = FindWorkspaceChoice(workspacePath);
        if (workspace is not null &&
            string.Equals(workspace.Kind, "installed-app", StringComparison.OrdinalIgnoreCase))
        {
            var manifest = TryLoadWorkspaceManifest(workspacePath);
            var appCommand = manifest?.Publish?.Command?.Trim();
            if (string.IsNullOrWhiteSpace(appCommand))
            {
                appCommand = manifest?.Build?.InstallCommand?.Trim();
            }

            if (!string.IsNullOrWhiteSpace(appCommand))
            {
                return $"cmd.exe /c \"{appCommand}\"";
            }
        }

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

    private CodexWorkspaceChoice? FindWorkspaceChoice(string workspacePath)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        return GetWorkspaceChoiceRecords()
            .FirstOrDefault(item => string.Equals(Path.GetFullPath(item.Path), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private WorkspacePromptContext BuildWorkspacePromptContext(string workspacePath)
    {
        var workspace = FindWorkspaceChoice(workspacePath);
        var manifest = workspace is not null &&
                       string.Equals(workspace.Kind, "installed-app", StringComparison.OrdinalIgnoreCase)
            ? TryLoadWorkspaceManifest(workspacePath)
            : null;

        return CodexWorkspacePolicy.CreatePromptContext(
            workspacePath,
            workspace?.Kind,
            workspace?.AppId,
            workspace?.Version,
            manifest);
    }

    private static AppManifest? TryLoadWorkspaceManifest(string workspacePath)
    {
        try
        {
            var manifestPath = Path.Combine(workspacePath, "app.manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppManifest>(File.ReadAllText(manifestPath), JsonOptions.Default);
        }
        catch
        {
            return null;
        }
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
                .Where(model => IsCodexModelSupportedByResolvedCli(model.Slug))
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
        return string.IsNullOrWhiteSpace(current) || !IsCodexModelSupportedByResolvedCli(current)
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

    private string ResolveSupportedCodexModel(string? requestedModel)
    {
        var requested = requestedModel?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requested) && IsCodexModelSupportedByResolvedCli(requested))
        {
            return requested;
        }

        var available = GetAvailableCodexModels();
        var fallback = available.FirstOrDefault(model => string.Equals(model.Slug, "gpt-5.4", StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(model => string.Equals(model.Slug, "gpt-5.4-mini", StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(model => string.Equals(model.Slug, "gpt-5.3-codex", StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault();

        return fallback?.Slug ?? requested;
    }

    private bool IsCodexModelSupportedByResolvedCli(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (!string.Equals(model.Trim(), "gpt-5.5", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var version = TryParseVersionText(_probeState.Version);
        return version is not null && version >= Gpt55MinimumCliVersion;
    }

    private static Version? TryParseVersionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionNumberRegex.Match(text);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private object BuildUsageSummary(CodexRuntimeState runtime, string currentProvider, string currentModel)
    {
        var provider = NormalizeProvider(currentProvider, currentModel);
        var evaluatedAtUtc = DateTimeOffset.UtcNow;
        var isUnlimited = IsOllamaProvider(provider);
        var fiveHourWindowStart = evaluatedAtUtc.AddHours(-5);
        var weeklyWindowStart = evaluatedAtUtc.AddDays(-7);

        var requestEvents = runtime.Sessions
            .Where(session => !IsOllamaProvider(session.Provider))
            .SelectMany(session => session.Messages
                .Where(message =>
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    IsRecentTimestampPlausible(message.CreatedAtUtc))
                .Select(message => message.CreatedAtUtc))
            .ToArray();

        var usedFiveHours = requestEvents.Count(timestamp => timestamp >= fiveHourWindowStart);
        var usedWeek = requestEvents.Count(timestamp => timestamp >= weeklyWindowStart);
        var fiveHourLimit = Math.Max(1, _context.Settings.CodexUsageRequestsPer5Hours);
        var weeklyLimit = Math.Max(fiveHourLimit, _context.Settings.CodexUsageRequestsPerWeek);

        if (isUnlimited)
        {
            return new
            {
                provider,
                model = currentModel,
                isUnlimited = true,
                summary = "Unlimited (local Ollama/Llama)",
                items = new object[]
                {
                    new
                    {
                        label = "5h",
                        used = (int?)null,
                        limit = (int?)null,
                        display = "Unlimited (local)",
                        detail = "Local Ollama/Llama runs are not quota-limited by MasterApp."
                    },
                    new
                    {
                        label = "Weekly",
                        used = (int?)null,
                        limit = (int?)null,
                        display = "Unlimited (local)",
                        detail = "Usage is informational only for local models."
                    }
                }
            };
        }

        return new
        {
            provider,
            model = currentModel,
            isUnlimited = false,
            evaluatedAtUtc,
            summary = $"{usedFiveHours} / {fiveHourLimit} requests in 5h",
            items = new object[]
            {
                new
                {
                    label = "5h",
                    used = usedFiveHours,
                    limit = fiveHourLimit,
                    windowStartUtc = fiveHourWindowStart,
                    display = $"{usedFiveHours} / {fiveHourLimit} requests",
                    detail = "Rolling 5-hour request count."
                },
                new
                {
                    label = "Weekly",
                    used = usedWeek,
                    limit = weeklyLimit,
                    windowStartUtc = weeklyWindowStart,
                    display = $"{usedWeek} / {weeklyLimit} requests",
                    detail = "Rolling 7-day request count."
                }
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
