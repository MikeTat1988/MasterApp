using MasterApp.Models;
using MasterApp.Storage;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
    private async Task<CodexDecisionEnvelope> AskCodexForDecisionAsync(CodexChatRun run, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_resolvedExecutablePath))
        {
            throw new InvalidOperationException("Codex executable was not resolved.");
        }

        var tempDirectory = Path.Combine(_context.Paths.TempDirectory, "codex-chat");
        Directory.CreateDirectory(tempDirectory);

        var schemaPath = Path.Combine(tempDirectory, $"decision-schema-{run.Id}.json");
        var outputPath = Path.Combine(tempDirectory, $"decision-output-{run.Id}.json");
        WriteDecisionSchema(schemaPath);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var prompt = BuildDecisionPrompt(run);
        var args = new List<string>
        {
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--output-schema",
            schemaPath,
            "-o",
            outputPath,
            "-C",
            run.WorkspacePath
        };

        if (!string.IsNullOrWhiteSpace(run.Model))
        {
            args.Add("-m");
            args.Add(run.Model);
        }

        args.Add(prompt);
        UpdateRun(run, "processing", "Waiting for Codex.");

        var eventLines = new List<string>();
        var exitCode = await RunProcessAsync(
            _resolvedExecutablePath!,
            args,
            run.WorkspacePath,
            line =>
            {
                eventLines.Add(line);
                AppendLog(run, "codex", line);
                return Task.CompletedTask;
            },
            line =>
            {
                eventLines.Add(line);
                AppendLog(run, "codex", line);
                return Task.CompletedTask;
            },
            cancellationToken,
            10 * 60 * 1000);

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException(BuildCodexFailureMessage(exitCode, eventLines));
        }

        var json = (await File.ReadAllTextAsync(outputPath, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Codex returned an empty decision.");
        }

        try
        {
            return JsonSerializer.Deserialize<CodexDecisionEnvelope>(json, JsonOptions.Default)
                   ?? throw new InvalidOperationException("Codex returned an invalid decision.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Codex decision could not be parsed: {ex.Message}");
        }
    }

    private string BuildDecisionPrompt(CodexChatRun run)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are operating inside the MasterApp Codex panel.");
        builder.AppendLine("Return JSON only and follow the schema exactly.");
        builder.AppendLine("You do not have direct shell access in this mode.");
        builder.AppendLine("Instead, choose the single next action for MasterApp to broker.");
        builder.AppendLine();
        builder.AppendLine("Rules:");
        builder.AppendLine("- Choose exactly one kind: final, command, build, or restart.");
        builder.AppendLine("- Use command when you need one PowerShell command to inspect or change files.");
        builder.AppendLine("- Use build when MasterApp should run its configured build command next.");
        builder.AppendLine("- Use restart only when the work is complete and MasterApp should schedule a safe relaunch next.");
        builder.AppendLine("- Restart decisions must include the final user-facing response in response.");
        builder.AppendLine("- Build and restart happen through MasterApp, not by raw self-management commands.");
        builder.AppendLine("- Never ask for multiple commands at once.");
        builder.AppendLine("- Keep commands Windows PowerShell compatible.");
        builder.AppendLine("- Prefer specific, minimal commands.");
        builder.AppendLine("- Investigate only when investigation is actually necessary to answer the user.");
        builder.AppendLine("- Avoid broad repository scans and avoid searching the whole workspace by default.");
        builder.AppendLine("- Do not use commands like Select-String -Path * or recursive wildcard scans unless the user explicitly asked to investigate.");
        builder.AppendLine("- Do not assume rg is available; if search is needed, prefer targeted PowerShell file reads or verify the command exists first.");
        builder.AppendLine("- Read-only inspection commands may be auto-approved when they stay in the workspace and avoid sensitive files.");
        builder.AppendLine();
        builder.AppendLine($"Detected task mode: {run.TaskMode}");
        builder.AppendLine($"Mode source: {run.TaskModeSource}");
        builder.AppendLine($"Mode confidence: {run.TaskModeConfidence:P0}");
        if (!string.IsNullOrWhiteSpace(run.TaskModeReason))
        {
            builder.AppendLine($"Mode reason: {run.TaskModeReason}");
        }

        switch ((run.TaskMode ?? string.Empty).Trim().ToLowerInvariant())
        {
            case TaskModeAction:
                builder.AppendLine("- This is an action request on the local machine.");
                builder.AppendLine("- Prefer the smallest concrete action that could satisfy it before any investigation.");
                builder.AppendLine("- If a configured build or restart command already matches the request, prefer that over exploratory commands.");
                builder.AppendLine("- Do not spend steps listing folders or searching files unless the action depends on discovering an exact path.");
                break;
            case TaskModeInvestigate:
                builder.AppendLine("- This is an investigation request.");
                builder.AppendLine("- Use narrowly scoped inspection commands and quickly converge on a concrete explanation.");
                break;
            case TaskModeCode:
                builder.AppendLine("- This is a code-change request inside the workspace.");
                builder.AppendLine("- Prefer the next command that directly unblocks an edit instead of general exploration.");
                break;
            case TaskModeAsk:
                builder.AppendLine("- This is primarily a question-answering request.");
                builder.AppendLine("- If you already have enough context, return final instead of inspecting the workspace.");
                break;
        }

        builder.AppendLine("- When investigation is needed, prefer simple file-scoped read-only commands such as Get-Content, Select-String, Test-Path, Get-ChildItem, git status, or git diff.");
        builder.AppendLine();
        builder.AppendLine($"Current workspace: {run.WorkspacePath}");
        builder.AppendLine($"Current model: {run.Model}");
        builder.AppendLine("Allowed workspaces:");
        foreach (var workspace in GetWorkspaceChoiceRecords())
        {
            builder.AppendLine($"- {workspace.Path} ({workspace.Kind})");
        }

        builder.AppendLine();
        builder.AppendLine($"Configured build command: {GetBuildCommand(run.WorkspacePath, run.Id)}");
        builder.AppendLine($"Configured restart command: {GetRestartCommand(run.WorkspacePath)}");

        if (run.ChangedFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Changed files so far:");
            foreach (var file in run.ChangedFiles.Take(40))
            {
                builder.AppendLine($"- {file}");
            }
        }

        if (run.ApprovalHistory.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Previous approved or rejected actions:");
            foreach (var item in run.ApprovalHistory.TakeLast(6))
            {
                builder.AppendLine($"- Kind: {item.Kind}");
                builder.AppendLine($"  Summary: {item.Summary}");
                builder.AppendLine($"  Command: {item.Command}");
                builder.AppendLine($"  Decision: {item.Decision}");
                builder.AppendLine($"  ExitCode: {(item.ExitCode.HasValue ? item.ExitCode.Value.ToString() : "-")}");
                builder.AppendLine($"  Output: {TrimForLog(item.OutputSummary, MaxPromptOutputCharacters)}");
            }
        }

        if (run.BuildResult is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Latest build result:");
            builder.AppendLine($"- Status: {run.BuildResult.Status}");
            builder.AppendLine($"- Summary: {run.BuildResult.Summary}");
            builder.AppendLine($"- ExitCode: {(run.BuildResult.ExitCode.HasValue ? run.BuildResult.ExitCode.Value.ToString() : "-")}");
        }

        builder.AppendLine();
        builder.AppendLine("User request:");
        builder.AppendLine(run.Prompt);
        return builder.ToString();
    }

    private async Task<CodexApprovalRecord> ExecuteApprovedActionAsync(
        CodexChatRun run,
        CodexDecisionEnvelope decision,
        CodexApprovalRequest approval,
        CancellationToken cancellationToken)
    {
        if (string.Equals(approval.Kind, "restart", StringComparison.OrdinalIgnoreCase))
        {
            var relaunch = await _runtime.ScheduleSelfRelaunchAsync(run.Id, run.WorkspacePath, approval.Summary);
            run.RestartStatus = relaunch;
            run.ResponseText = string.IsNullOrWhiteSpace(decision.Response)
                ? "Restart was requested and scheduled."
                : decision.Response.Trim();
            run.Summary = TrimForLog(run.ResponseText, 240);
            run.Status = relaunch.Status is "scheduled" or "launched" ? "restart-scheduled" : "failed";
            run.FailureMessage = run.Status == "failed" ? relaunch.Message : null;

            return new CodexApprovalRecord
            {
                Id = approval.Id,
                Kind = approval.Kind,
                Summary = approval.Summary,
                Command = approval.Command,
                WorkingDirectory = approval.WorkingDirectory,
                Decision = "approve",
                OutputSummary = relaunch.Message,
                LogLines = new List<string> { $"[{DateTimeOffset.Now:HH:mm:ss}] [restart] {relaunch.Message}" },
                RequestedAtUtc = approval.RequestedAtUtc,
                ResolvedAtUtc = DateTimeOffset.UtcNow
            };
        }

        if (string.Equals(approval.Kind, "build", StringComparison.OrdinalIgnoreCase))
        {
            var buildResult = await RunBuildAsync(run, cancellationToken);
            run.BuildResult = buildResult;
            return new CodexApprovalRecord
            {
                Id = approval.Id,
                Kind = approval.Kind,
                Summary = approval.Summary,
                Command = buildResult.Command,
                WorkingDirectory = approval.WorkingDirectory,
                Decision = "approve",
                ExitCode = buildResult.ExitCode,
                OutputSummary = buildResult.Summary,
                LogLines = buildResult.LogLines.TakeLast(MaxApprovalLogLines).ToList(),
                RequestedAtUtc = approval.RequestedAtUtc,
                ResolvedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var commandResult = await RunApprovedCommandAsync(run, approval, cancellationToken);
        return new CodexApprovalRecord
        {
            Id = approval.Id,
            Kind = approval.Kind,
            Summary = approval.Summary,
            Command = approval.Command,
            WorkingDirectory = approval.WorkingDirectory,
            Decision = "approve",
            ExitCode = commandResult.ExitCode,
            OutputSummary = commandResult.Summary,
            LogLines = commandResult.LogLines.TakeLast(MaxApprovalLogLines).ToList(),
            RequestedAtUtc = approval.RequestedAtUtc,
            ResolvedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task<CommandExecutionSummary> RunApprovedCommandAsync(
        CodexChatRun run,
        CodexApprovalRequest approval,
        CancellationToken cancellationToken)
    {
        var commandLogs = new List<string>();
        AppendLog(run, "command", $"Approved command: {approval.Command}");

        var exitCode = await RunPowerShellCommandAsync(
            approval.Command,
            approval.WorkingDirectory,
            line =>
            {
                commandLogs.Add(line);
                AppendLog(run, "command", line);
                return Task.CompletedTask;
            },
            cancellationToken,
            15 * 60 * 1000);

        return new CommandExecutionSummary
        {
            ExitCode = exitCode,
            Summary = exitCode == 0
                ? "Command completed successfully."
                : $"Command failed with exit code {exitCode}.",
            LogLines = commandLogs
        };
    }

    private async Task<CodexBuildResult> RunBuildAsync(CodexChatRun run, CancellationToken cancellationToken)
    {
        var command = GetBuildCommand(run.WorkspacePath, run.Id);
        var buildLogs = new List<string>();
        var build = new CodexBuildResult
        {
            Status = "running",
            Command = command,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Summary = "Build started."
        };

        run.Status = "building";
        run.BuildResult = build;
        PublishRun(run);

        try
        {
            var exitCode = await RunPowerShellCommandAsync(
                command,
                run.WorkspacePath,
                line =>
                {
                    buildLogs.Add(line);
                    build.LogLines = buildLogs.TakeLast(MaxLiveLogLines).ToList();
                    AppendLog(run, "build", line);
                    return Task.CompletedTask;
                },
                cancellationToken,
                20 * 60 * 1000);

            build.ExitCode = exitCode;
            build.Success = exitCode == 0;
            build.Status = build.Success ? "completed" : "failed";
            build.Summary = build.Success
                ? "Build completed successfully."
                : $"Build failed with exit code {exitCode}.";
        }
        catch (Exception ex)
        {
            build.Status = "failed";
            build.Success = false;
            build.Summary = ex.Message;
            buildLogs.Add(ex.Message);
            AppendLog(run, "build", ex.Message);
        }
        finally
        {
            build.CompletedAtUtc = DateTimeOffset.UtcNow;
            build.LogLines = buildLogs.TakeLast(MaxPersistedLogLines).ToList();
        }

        return build;
    }

    private CodexApprovalRequest CreateApprovalRequest(CodexChatRun run, CodexDecisionEnvelope decision)
    {
        var kind = decision.Kind.Trim().ToLowerInvariant();
        if (kind is not "command" and not "build" and not "restart")
        {
            throw new InvalidOperationException($"Unsupported Codex decision kind: {decision.Kind}");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(decision.WorkingDirectory)
            ? run.WorkspacePath
            : Path.GetFullPath(decision.WorkingDirectory);
        EnsureWorkingDirectoryAllowed(workingDirectory);

        var command = kind switch
        {
            "build" => GetBuildCommand(run.WorkspacePath, run.Id),
            "restart" => GetRestartCommand(run.WorkspacePath),
            _ => decision.Command?.Trim() ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Codex requested an action without a command.");
        }

        return new CodexApprovalRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = run.Id,
            Kind = kind,
            Summary = string.IsNullOrWhiteSpace(decision.Summary) ? GetDefaultApprovalSummary(kind) : decision.Summary.Trim(),
            Command = command,
            WorkingDirectory = workingDirectory,
            RequestedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private void EnsureWorkingDirectoryAllowed(string workingDirectory)
    {
        var allowed = GetAllowedWorkspacePaths();

        if (!allowed.Contains(workingDirectory, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Requested working directory is not allowed: {workingDirectory}");
        }
    }

    private static string GetDefaultApprovalSummary(string kind)
    {
        return kind switch
        {
            "build" => "Run the configured build command.",
            "restart" => "Schedule a safe MasterApp relaunch.",
            _ => "Run a workspace command."
        };
    }

    private static bool ShouldAutoApproveReadOnlyCommand(CodexApprovalRequest approval)
    {
        if (!string.Equals(approval.Kind, "command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var command = (approval.Command ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var normalized = command.ToLowerInvariant();
        if (normalized.Contains(';') ||
            normalized.Contains("&&", StringComparison.Ordinal) ||
            normalized.Contains("||", StringComparison.Ordinal) ||
            normalized.Contains('>') ||
            normalized.Contains('<') ||
            normalized.Contains("`n", StringComparison.Ordinal) ||
            normalized.Contains("`r", StringComparison.Ordinal))
        {
            return false;
        }

        string[] sensitiveMarkers =
        {
            "secret", "token", "password", "credential", ".env", "secrets.json", "runtime-state",
            "id_rsa", "id_ed25519", "appdata", "localappdata", "$env:", "ssh", "onedrive"
        };

        if (sensitiveMarkers.Any(normalized.Contains))
        {
            return false;
        }

        string[] mutatingMarkers =
        {
            "remove-item", "set-content", "add-content", "out-file", "move-item", "copy-item",
            "rename-item", "new-item", "clear-content", "git apply", "git add", "git commit",
            "git checkout", "git switch", "git reset", "git clean", "dotnet build", "dotnet run",
            "msbuild", "start-process", "stop-process", "taskkill", "invoke-webrequest",
            "invoke-restmethod", "curl ", "wget ", "npm ", "pnpm ", "yarn ", "del ", "erase "
        };

        if (mutatingMarkers.Any(normalized.Contains))
        {
            return false;
        }

        string[] safePrefixes =
        {
            "get-childitem", "get-content", "select-string", "test-path", "resolve-path",
            "get-item", "get-location", "git status", "git diff", "dotnet --info", "type ", "dir "
        };

        return safePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static void WriteDecisionSchema(string path)
    {
        const string schema = """
{
  "type": "object",
  "additionalProperties": false,
  "required": ["kind", "summary", "response", "command", "workingDirectory"],
  "properties": {
    "kind": { "type": "string", "enum": ["final", "command", "build", "restart"] },
    "summary": { "type": "string" },
    "response": { "type": "string" },
    "command": { "type": "string" },
    "workingDirectory": { "type": "string" }
  }
}
""";

        File.WriteAllText(path, schema, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string BuildCodexFailureMessage(int exitCode, IReadOnlyList<string> eventLines)
    {
        var detail = eventLines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(12).ToArray();
        return detail.Length == 0
            ? $"Codex exited with code {exitCode}."
            : $"Codex exited with code {exitCode}. {TrimForLog(string.Join(" | ", detail), 900)}";
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

    private sealed record WorkspaceSnapshot(string RootPath)
    {
        public Dictionary<string, WorkspaceFileFingerprint> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record WorkspaceFileFingerprint(long Length, DateTime LastWriteTimeUtc);

    private sealed record CodexDecisionEnvelope
    {
        public string Kind { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string Response { get; init; } = string.Empty;
        public string Command { get; init; } = string.Empty;
        public string WorkingDirectory { get; init; } = string.Empty;
    }

    private sealed class CommandExecutionSummary
    {
        public int ExitCode { get; init; }
        public string Summary { get; init; } = string.Empty;
        public List<string> LogLines { get; init; } = new();
    }
}
