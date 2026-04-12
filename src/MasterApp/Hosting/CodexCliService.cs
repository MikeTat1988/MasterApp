using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Storage;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MasterApp.Hosting;

public sealed partial class CodexCliService
{
    private const int MaxLiveLogLines = 400;
    private const int MaxPersistedLogLines = 200;
    private static readonly Regex ResultJsonPattern = new(@"MASTERAPP_RESULT_JSON:\s*(\{.+\})", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly CodexCliModeDefinition[] ModeCandidates =
    {
        new("exec-json", true, new[] { "exec", "--json" }),
        new("exec-jsonl", true, new[] { "exec", "--jsonl" }),
        new("exec-text", false, new[] { "exec" })
    };

    private readonly BootstrapContext _context;
    private readonly MasterAppRuntime _runtime;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, Channel<CodexServerEvent>> _subscribers = new();

    private CodexOperationRecord? _activeOperation;
    private CodexCliProbeState _probeState = new();

    public CodexCliService(BootstrapContext context, MasterAppRuntime runtime)
    {
        _context = context;
        _runtime = runtime;
        ApplyPendingRelaunchMarker();
    }

    public object GetDashboardResponse()
    {
        CodexOperationRecord? active;
        CodexCliProbeState probe;

        lock (_gate)
        {
            active = _activeOperation is null ? null : Clone(_activeOperation);
            probe = _probeState with { };
        }

        var recent = _context.RuntimeStateStore.GetCodexOperations()
            .Where(item => active is null || !string.Equals(item.Id, active.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.StartedAtUtc)
            .ToArray();

        return new
        {
            codexCommand = _context.Settings.CodexCommand,
            configuredWorkspaces = GetWorkspaceChoices(),
            preferredBuildCommand = _context.Settings.PreferredBuildCommand,
            preferredRestartCommand = _context.Settings.PreferredRestartCommand,
            preferCodexJsonOutput = _context.Settings.PreferCodexJsonOutput,
            logsPath = _context.Log.GetPath(Diagnostics.LogKind.Codex),
            cliProbe = probe,
            activeOperation = active,
            recentOperations = recent,
            lastRelaunch = _context.RuntimeStateStore.GetLastRelaunch()
        };
    }

    public CodexEventSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<CodexServerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        channel.Writer.TryWrite(new CodexServerEvent("codex.snapshot", GetDashboardResponse()));
        return new CodexEventSubscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    public Task<CodexOperationRecord> StartOperationAsync(CodexChatRequest request, CancellationToken cancellationToken = default)
    {
        var workspacePath = ResolveWorkspace(request.WorkspacePath);
        CodexOperationRecord operation;

        lock (_gate)
        {
            if (_activeOperation is not null && !IsTerminal(_activeOperation.Status))
            {
                throw new InvalidOperationException("A Codex operation is already running.");
            }

            operation = new CodexOperationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Status = "queued",
                WorkspacePath = workspacePath,
                Prompt = request.Prompt.Trim(),
                StartedAtUtc = DateTimeOffset.UtcNow
            };

            _activeOperation = operation;
        }

        Persist(operation);
        Publish(new CodexServerEvent("codex.operation.started", Clone(operation)));
        _ = Task.Run(() => ExecuteOperationAsync(operation, cancellationToken), CancellationToken.None);
        return Task.FromResult(Clone(operation));
    }

    private async Task ExecuteOperationAsync(CodexOperationRecord operation, CancellationToken cancellationToken)
    {
        WorkspaceSnapshot beforeSnapshot;
        try
        {
            beforeSnapshot = CaptureWorkspaceSnapshot(operation.WorkspacePath);
        }
        catch (Exception ex)
        {
            beforeSnapshot = new WorkspaceSnapshot(operation.WorkspacePath);
            AppendLog(operation, "system", $"Workspace snapshot warning: {ex.Message}");
        }

        try
        {
            UpdateStatus(operation, "probing", "Detecting local Codex CLI mode.");
            var mode = await EnsureCliModeAsync(operation.WorkspacePath, cancellationToken);
            operation.CliMode = mode.Name;
            operation.UsedStructuredOutput = mode.StructuredOutput;

            UpdateStatus(operation, "running", $"Running {mode.Name} in {operation.WorkspacePath}");
            var exitCode = await RunCodexAsync(operation, mode, BuildPrompt(operation), cancellationToken);
            operation.ExitCode = exitCode;

            TryApplyResultJson(operation);
            operation.ChangedFiles = GetChangedFiles(beforeSnapshot, CaptureWorkspaceSnapshot(operation.WorkspacePath));

            if (exitCode != 0)
            {
                operation.Status = "failed";
                operation.FailureMessage ??= $"Codex exited with code {exitCode}.";
            }

            var wantsRestart = ShouldRestartAfterCompletion(operation.Prompt);
            var wantsBuild = wantsRestart || ShouldBuildAfterCompletion(operation.Prompt);

            if (operation.Status != "failed" && wantsBuild)
            {
                operation.BuildResult = await RunBuildAsync(operation, cancellationToken);
                if (!operation.BuildResult.Success)
                {
                    operation.Status = "failed";
                    operation.FailureMessage = operation.BuildResult.Summary;
                }
            }

            if (operation.Status != "failed" && wantsRestart)
            {
                operation.Status = "restarting";
                Persist(operation);
                Publish(new CodexServerEvent("codex.operation.updated", Clone(operation)));

                var relaunch = await _runtime.ScheduleSelfRelaunchAsync(
                    operation.Id,
                    operation.WorkspacePath,
                    $"Codex requested restart after: {TrimForLog(operation.Prompt, 120)}");

                operation.RestartStatus = relaunch;
                operation.Status = relaunch.Status is "scheduled" or "launched" ? "restart-scheduled" : "failed";
                if (operation.Status == "failed")
                {
                    operation.FailureMessage = relaunch.Message;
                }
            }

            if (operation.Status != "failed" && operation.Status != "restart-scheduled")
            {
                operation.Status = "completed";
            }
        }
        catch (Exception ex)
        {
            operation.Status = "failed";
            operation.FailureMessage = ex.Message;
            AppendLog(operation, "error", ex.ToString());
            _context.Log.Codex("CodexCliService", $"Operation {operation.Id} failed.", ex);
        }
        finally
        {
            operation.CompletedAtUtc = DateTimeOffset.UtcNow;
            TrimForPersistence(operation);
            Persist(operation);
            Publish(new CodexServerEvent("codex.operation.completed", Clone(operation)));

            lock (_gate)
            {
                if (_activeOperation is not null && string.Equals(_activeOperation.Id, operation.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _activeOperation = null;
                }
            }
        }
    }

    private async Task<CodexCliModeDefinition> EnsureCliModeAsync(string workspacePath, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_probeState.IsReady)
            {
                var cached = ModeCandidates.FirstOrDefault(mode => string.Equals(mode.Name, _probeState.Mode, StringComparison.OrdinalIgnoreCase));
                if (cached is not null)
                {
                    return cached;
                }
            }
        }

        var failures = new List<string>();
        var candidates = _context.Settings.PreferCodexJsonOutput
            ? ModeCandidates
            : ModeCandidates.OrderBy(mode => mode.StructuredOutput ? 1 : 0).ToArray();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await ProbeModeAsync(candidate, workspacePath, cancellationToken);
            if (probe.Success)
            {
                lock (_gate)
                {
                    _probeState = new CodexCliProbeState
                    {
                        Status = "ready",
                        IsReady = true,
                        Mode = candidate.Name,
                        StructuredOutput = candidate.StructuredOutput,
                        ProbedAtUtc = DateTimeOffset.UtcNow,
                        LastError = null
                    };
                }

                Publish(new CodexServerEvent("codex.probe.updated", _probeState));
                return candidate;
            }

            failures.Add($"{candidate.Name}: {probe.Error}");
        }

        lock (_gate)
        {
            _probeState = new CodexCliProbeState
            {
                Status = "failed",
                IsReady = false,
                Mode = null,
                StructuredOutput = false,
                ProbedAtUtc = DateTimeOffset.UtcNow,
                LastError = string.Join(" | ", failures)
            };
        }

        Publish(new CodexServerEvent("codex.probe.updated", _probeState));
        throw new InvalidOperationException(_probeState.LastError ?? "Unable to detect a usable Codex CLI mode.");
    }

    private async Task<(bool Success, string? Error)> ProbeModeAsync(CodexCliModeDefinition mode, string workspacePath, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            var exitCode = await RunProcessAsync(
                _context.Settings.CodexCommand,
                mode.GetArguments("Reply with READY only. Do not run tools. Do not read or edit files."),
                workspacePath,
                line =>
                {
                    stdout.AppendLine(line);
                    return Task.CompletedTask;
                },
                line =>
                {
                    stderr.AppendLine(line);
                    return Task.CompletedTask;
                },
                cancellationToken,
                45_000);

            var output = stdout.ToString() + Environment.NewLine + stderr;
            if (exitCode == 0 && output.Contains("READY", StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }

            if (LooksLikeModeFailure(output))
            {
                return (false, TrimForLog(output, 400));
            }

            return exitCode == 0 ? (true, null) : (false, TrimForLog(output, 400));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<int> RunCodexAsync(CodexOperationRecord operation, CodexCliModeDefinition mode, string prompt, CancellationToken cancellationToken)
    {
        return await RunProcessAsync(
            _context.Settings.CodexCommand,
            mode.GetArguments(prompt),
            operation.WorkspacePath,
            line =>
            {
                HandleProcessLine(operation, "stdout", line, mode.StructuredOutput);
                return Task.CompletedTask;
            },
            line =>
            {
                HandleProcessLine(operation, "stderr", line, false);
                return Task.CompletedTask;
            },
            cancellationToken,
            30 * 60 * 1000);
    }

    private void HandleProcessLine(CodexOperationRecord operation, string stream, string line, bool structuredOutput)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AppendLog(operation, stream, line);

        if (structuredOutput && TryExtractAssistantText(line, out var delta))
        {
            AppendAssistantDelta(operation, delta);
        }
        else if (string.Equals(stream, "stdout", StringComparison.OrdinalIgnoreCase))
        {
            AppendAssistantDelta(operation, line);
        }

        Publish(new CodexServerEvent("codex.operation.updated", Clone(operation)));
    }

    private async Task<CodexBuildResult> RunBuildAsync(CodexOperationRecord operation, CancellationToken cancellationToken)
    {
        var command = GetBuildCommand(operation.WorkspacePath, operation.Id);
        var buildLogs = new List<string>();
        var build = new CodexBuildResult
        {
            Status = "running",
            Command = command,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Summary = "Build started."
        };

        operation.Status = "building";
        operation.BuildResult = build;
        Persist(operation);
        Publish(new CodexServerEvent("codex.operation.updated", Clone(operation)));

        try
        {
            var exitCode = await RunShellCommandAsync(
                command,
                operation.WorkspacePath,
                line =>
                {
                    buildLogs.Add(line);
                    build.LogLines = buildLogs.TakeLast(MaxLiveLogLines).ToList();
                    AppendLog(operation, "build", line);
                    Publish(new CodexServerEvent("codex.operation.updated", Clone(operation)));
                    return Task.CompletedTask;
                },
                cancellationToken,
                15 * 60 * 1000);

            build.ExitCode = exitCode;
            build.Success = exitCode == 0;
            build.Status = build.Success ? "completed" : "failed";
            build.Summary = build.Success ? "Build completed successfully." : $"Build failed with exit code {exitCode}.";
        }
        catch (Exception ex)
        {
            build.Status = "failed";
            build.Success = false;
            build.Summary = ex.Message;
            buildLogs.Add(ex.Message);
            AppendLog(operation, "build", ex.Message);
        }
        finally
        {
            build.CompletedAtUtc = DateTimeOffset.UtcNow;
            build.LogLines = buildLogs.TakeLast(MaxPersistedLogLines).ToList();
        }

        return build;
    }

    private string BuildPrompt(CodexOperationRecord operation)
    {
        var history = _context.RuntimeStateStore.GetCodexOperations()
            .Where(item => string.Equals(item.WorkspacePath, operation.WorkspacePath, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.Equals(item.Id, operation.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.StartedAtUtc)
            .Take(4)
            .Reverse()
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("You are operating inside the MasterApp Codex Chat panel.");
        builder.AppendLine($"Current workspace: {operation.WorkspacePath}");
        builder.AppendLine("Configured workspace roots:");
        foreach (var workspace in _context.Settings.WorkspacePaths)
        {
            builder.AppendLine($"- {workspace}");
        }

        builder.AppendLine();
        builder.AppendLine("Constraints:");
        builder.AppendLine("- Use the local Codex account/session already configured on this machine.");
        builder.AppendLine("- Do not ask for API keys.");
        builder.AppendLine("- You may read and edit files in the current workspace.");
        builder.AppendLine("- If the user asks to rebuild or restart MasterApp, do not kill or relaunch the app yourself.");
        builder.AppendLine("- Instead, make the requested code changes and mention any follow-up actions in the final result JSON.");
        builder.AppendLine("- End with a single line that begins with MASTERAPP_RESULT_JSON: followed by compact JSON.");
        builder.AppendLine("- The JSON should contain keys: summary (string), followUps (array of { kind, description }), notes (array of strings).");

        if (history.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Recent conversation context:");
            foreach (var item in history)
            {
                builder.AppendLine($"User: {TrimForLog(item.Prompt, 500)}");
                builder.AppendLine($"Assistant: {TrimForLog(item.Summary ?? item.AssistantResponse, 700)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("User request:");
        builder.AppendLine(operation.Prompt);
        return builder.ToString();
    }

    private void TryApplyResultJson(CodexOperationRecord operation)
    {
        var match = ResultJsonPattern.Match(operation.AssistantResponse);
        if (!match.Success)
        {
            operation.Summary = string.IsNullOrWhiteSpace(operation.Summary)
                ? TrimForLog(operation.AssistantResponse, 800)
                : operation.Summary;
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CodexResultEnvelope>(match.Groups[1].Value, JsonOptions.Default);
            if (payload is not null)
            {
                operation.Summary = payload.Summary ?? operation.Summary;
                operation.FollowUps = payload.FollowUps?
                    .Where(item => item is not null)
                    .Select(item => new CodexFollowUpAction
                    {
                        Kind = item!.Kind ?? string.Empty,
                        Description = item.Description ?? string.Empty
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Kind) || !string.IsNullOrWhiteSpace(item.Description))
                    .ToList() ?? operation.FollowUps;
            }
        }
        catch (JsonException ex)
        {
            AppendLog(operation, "system", $"Result JSON parse warning: {ex.Message}");
        }

        operation.AssistantResponse = ResultJsonPattern.Replace(operation.AssistantResponse, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(operation.Summary))
        {
            operation.Summary = TrimForLog(operation.AssistantResponse, 800);
        }
    }

    private void AppendAssistantDelta(CodexOperationRecord operation, string delta)
    {
        if (string.IsNullOrWhiteSpace(delta))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operation.AssistantResponse))
        {
            operation.AssistantResponse += Environment.NewLine;
        }

        operation.AssistantResponse += delta.TrimEnd();
    }

    private void AppendLog(CodexOperationRecord operation, string kind, string line)
    {
        var formatted = $"[{DateTimeOffset.Now:HH:mm:ss}] [{kind}] {line}";
        operation.LogLines.Add(formatted);
        if (operation.LogLines.Count > MaxLiveLogLines)
        {
            operation.LogLines.RemoveRange(0, operation.LogLines.Count - MaxLiveLogLines);
        }

        _context.Log.Codex("CodexCliService", formatted);
    }

    private void UpdateStatus(CodexOperationRecord operation, string status, string logMessage)
    {
        operation.Status = status;
        AppendLog(operation, "system", logMessage);
        Persist(operation);
        Publish(new CodexServerEvent("codex.operation.updated", Clone(operation)));
    }

    private string ResolveWorkspace(string? requestedPath)
    {
        var allowed = GetWorkspaceChoiceRecords()
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => Path.GetFullPath(item.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private string GetBuildCommand(string workspacePath, string operationId)
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
            var outputPath = Path.Combine(_context.Paths.TempDirectory, "codex-build", operationId);
            Directory.CreateDirectory(outputPath);
            command += $" -o \"{outputPath}\"";
        }

        return command;
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

    private sealed class CodexWorkspaceChoice
    {
        public string Path { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Kind { get; init; } = "workspace";
        public string? AppId { get; init; }
        public string? Version { get; init; }
    }

    private void Persist(CodexOperationRecord operation)
    {
        _context.RuntimeStateStore.UpsertCodexOperation(operation, _context.Settings.CodexHistoryLimit);
    }

    private void Publish(CodexServerEvent ev)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(ev);
        }
    }

    private void ApplyPendingRelaunchMarker()
    {
        var path = _context.Paths.RelaunchStateFile;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var marker = JsonSerializer.Deserialize<RelaunchStatusRecord>(json, JsonOptions.Default);
            if (marker is null)
            {
                return;
            }

            _context.RuntimeStateStore.SetLastRelaunch(marker);

            if (!string.IsNullOrWhiteSpace(marker.OperationId))
            {
                var operation = _context.RuntimeStateStore.GetCodexOperations()
                    .FirstOrDefault(item => string.Equals(item.Id, marker.OperationId, StringComparison.OrdinalIgnoreCase));
                if (operation is not null)
                {
                    operation.RestartStatus = marker;
                    if (string.Equals(marker.Status, "launched", StringComparison.OrdinalIgnoreCase))
                    {
                        operation.Status = "completed";
                    }

                    _context.RuntimeStateStore.UpsertCodexOperation(operation, _context.Settings.CodexHistoryLimit);
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log.Codex("CodexCliService", $"Failed to apply relaunch marker: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // ignore marker cleanup failures
            }
        }
    }

    private static WorkspaceSnapshot CaptureWorkspaceSnapshot(string workspacePath)
    {
        var snapshot = new WorkspaceSnapshot(workspacePath);
        var stack = new Stack<string>();
        stack.Push(workspacePath);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var directory in Directory.GetDirectories(current))
            {
                var name = Path.GetFileName(directory);
                if (ShouldSkipDirectory(name))
                {
                    continue;
                }

                stack.Push(directory);
            }

            foreach (var file in Directory.GetFiles(current))
            {
                var relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
                var info = new FileInfo(file);
                snapshot.Files[relative] = new WorkspaceFileFingerprint(info.Length, info.LastWriteTimeUtc);
            }
        }

        return snapshot;
    }

    private static List<string> GetChangedFiles(WorkspaceSnapshot before, WorkspaceSnapshot after)
    {
        var changes = new List<string>();

        foreach (var (path, fingerprint) in after.Files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.Files.TryGetValue(path, out var prior))
            {
                changes.Add($"A {path}");
                continue;
            }

            if (prior.Length != fingerprint.Length || prior.LastWriteTimeUtc != fingerprint.LastWriteTimeUtc)
            {
                changes.Add($"M {path}");
            }
        }

        foreach (var path in before.Files.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!after.Files.ContainsKey(path))
            {
                changes.Add($"D {path}");
            }
        }

        return changes.Take(200).ToList();
    }

    private static bool ShouldSkipDirectory(string name)
    {
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldBuildAfterCompletion(string prompt)
    {
        return Regex.IsMatch(prompt, @"\b(build|rebuild|compile|publish)\b", RegexOptions.IgnoreCase);
    }

    private static bool ShouldRestartAfterCompletion(string prompt)
    {
        return Regex.IsMatch(prompt, @"\b(restart|relaunch)\b", RegexOptions.IgnoreCase);
    }

    private static bool TryExtractAssistantText(string line, out string delta)
    {
        delta = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(line);
            if (TryFindText(document.RootElement, out delta))
            {
                delta = delta.Trim();
                return !string.IsNullOrWhiteSpace(delta);
            }
        }
        catch
        {
            // treat the line as non-JSON output
        }

        return false;
    }

    private static bool TryFindText(JsonElement element, out string value)
    {
        value = string.Empty;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            case JsonValueKind.Object:
                foreach (var propertyName in new[] { "delta", "text", "content", "message", "output_text" })
                {
                    if (element.TryGetProperty(propertyName, out var property) && TryFindText(property, out value))
                    {
                        return true;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindText(property.Value, out value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindText(item, out value))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool LooksLikeModeFailure(string output)
    {
        return output.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminal(string status)
    {
        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "restart-scheduled", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimForLog(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + " ...";
    }

    private static void TrimForPersistence(CodexOperationRecord operation)
    {
        operation.LogLines = operation.LogLines.TakeLast(MaxPersistedLogLines).ToList();
        operation.AssistantResponse = TrimForLog(operation.AssistantResponse, 20_000);
        operation.Summary = TrimForLog(operation.Summary, 2_000);
    }

    private static CodexOperationRecord Clone(CodexOperationRecord operation)
    {
        var json = JsonSerializer.Serialize(operation, JsonOptions.Default);
        return JsonSerializer.Deserialize<CodexOperationRecord>(json, JsonOptions.Default) ?? new CodexOperationRecord();
    }

    private static Task<int> RunShellCommandAsync(
        string command,
        string workingDirectory,
        Func<string, Task> onLine,
        CancellationToken cancellationToken,
        int timeoutMilliseconds)
    {
        return RunProcessAsync(
            "cmd.exe",
            new[] { "/c", command },
            workingDirectory,
            onLine,
            onLine,
            cancellationToken,
            timeoutMilliseconds);
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, Task> onStdoutLine,
        Func<string, Task> onStderrLine,
        CancellationToken cancellationToken,
        int timeoutMilliseconds)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                _ = onStdoutLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                _ = onStderrLine(args.Data);
            }
        };
        process.Exited += (_, _) => exitTcs.TrySetResult(process.ExitCode);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);
        using var registration = timeoutCts.Token.Register(() => exitTcs.TrySetCanceled(timeoutCts.Token));

        try
        {
            return await exitTcs.Task;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore shutdown failures after timeout
            }

            throw new TimeoutException($"Process timed out after {timeoutMilliseconds}ms: {fileName}");
        }
    }

    private sealed record CodexCliModeDefinition(string Name, bool StructuredOutput, string[] BaseArguments)
    {
        public IReadOnlyList<string> GetArguments(string prompt)
        {
            return new List<string>(BaseArguments) { prompt };
        }
    }

    private sealed record CodexResultEnvelope
    {
        public string? Summary { get; init; }
        public List<CodexResultFollowUp?>? FollowUps { get; init; }
    }

    private sealed record CodexResultFollowUp
    {
        public string? Kind { get; init; }
        public string? Description { get; init; }
    }

    private sealed class WorkspaceSnapshot
    {
        public WorkspaceSnapshot(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }
        public Dictionary<string, WorkspaceFileFingerprint> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record WorkspaceFileFingerprint(long Length, DateTime LastWriteTimeUtc);

    public sealed record CodexServerEvent(string Type, object Payload);

    public sealed class CodexChatRequest
    {
        public string Prompt { get; set; } = string.Empty;
        public string? WorkspacePath { get; set; }
    }

    public sealed record CodexCliProbeState
    {
        public string Status { get; init; } = "idle";
        public bool IsReady { get; init; }
        public string? Mode { get; init; }
        public bool StructuredOutput { get; init; }
        public DateTimeOffset? ProbedAtUtc { get; init; }
        public string? LastError { get; init; }
    }

    public sealed class CodexEventSubscription : IDisposable
    {
        private readonly Action _onDispose;

        public CodexEventSubscription(ChannelReader<CodexServerEvent> reader, Action onDispose)
        {
            Reader = reader;
            _onDispose = onDispose;
        }

        public ChannelReader<CodexServerEvent> Reader { get; }

        public void Dispose()
        {
            _onDispose();
        }
    }
}
