using MasterApp.Models;
using MasterApp.Storage;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
    private async Task RunSharedCodexTurnAsync(CodexChatRun run, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_resolvedExecutablePath))
        {
            throw new InvalidOperationException("Codex executable was not resolved.");
        }

        var tempDirectory = Path.Combine(_context.Paths.TempDirectory, "codex-shared");
        Directory.CreateDirectory(tempDirectory);

        var outputPath = Path.Combine(tempDirectory, $"last-message-{run.Id}.txt");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var args = BuildSharedCodexArguments(run, outputPath);
        AppendLog(run, "system", string.IsNullOrWhiteSpace(run.SharedSessionId)
            ? "Running one shared Codex turn."
            : $"Running one shared Codex turn in session {run.SharedSessionId}.");
        PublishRun(run);

        var eventLines = new List<string>();
        var exitCode = await RunProcessAsync(
            _resolvedExecutablePath!,
            args,
            run.WorkspacePath,
            line =>
            {
                eventLines.Add(line);
                ApplySharedCodexEvent(run, line);
                AppendLog(run, "codex", line);
                return Task.CompletedTask;
            },
            line =>
            {
                eventLines.Add(line);
                ApplySharedCodexEvent(run, line);
                AppendLog(run, "codex", line);
                return Task.CompletedTask;
            },
            cancellationToken,
            30 * 60 * 1000,
            Encoding.UTF8);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(BuildCodexFailureMessage(exitCode, eventLines));
        }

        var response = File.Exists(outputPath)
            ? (await File.ReadAllTextAsync(outputPath, cancellationToken)).Trim()
            : string.Empty;
        run.ResponseText = response;
        run.Summary = TrimForLog(string.IsNullOrWhiteSpace(response) ? "Completed." : response, 240);
        run.Status = "completed";
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(run.SharedSessionId))
        {
            AdoptSharedSessionId(run);
        }

        PublishRun(run);
    }

    private List<string> BuildSharedCodexArguments(CodexChatRun run, string outputPath)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(run.SharedSessionId))
        {
            args.Add("exec");
            args.Add("--json");
            args.Add("--skip-git-repo-check");
            args.Add("-o");
            args.Add(outputPath);
            args.Add("-C");
            args.Add(run.WorkspacePath);
            args.Add("-s");
            args.Add("workspace-write");
            AddTrustedWorkspaceArguments(args, run.WorkspacePath);
            if (IsOllamaProvider(run.Provider))
            {
                args.Add("--oss");
                args.Add("--local-provider");
                args.Add("ollama");
            }

            if (!string.IsNullOrWhiteSpace(run.Model))
            {
                args.Add("-m");
                args.Add(run.Model);
            }
        }
        else
        {
            args.Add("exec");
            args.Add("resume");
            args.Add("--json");
            args.Add("--skip-git-repo-check");
            args.Add("-o");
            args.Add(outputPath);
            if (!string.IsNullOrWhiteSpace(run.Model))
            {
                args.Add("-m");
                args.Add(run.Model);
            }

            AddTrustedWorkspaceArguments(args, run.WorkspacePath);
            args.Add(run.SharedSessionId);
        }

        args.Add(BuildSharedCodexPrompt(run));
        return args;
    }

    private void AddTrustedWorkspaceArguments(List<string> args, string primaryWorkspacePath)
    {
        var primary = Path.GetFullPath(primaryWorkspacePath);
        foreach (var path in GetAllowedWorkspacePaths())
        {
            var full = Path.GetFullPath(path);
            if (string.Equals(full, primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            args.Add("--add-dir");
            args.Add(full);
        }
    }

    private string BuildSharedCodexPrompt(CodexChatRun run)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are running inside the MasterApp Codex panel.");
        builder.AppendLine("Important host contract:");
        builder.AppendLine("- Do not stop, kill, taskkill, or raw-restart the MasterApp process yourself.");
        builder.AppendLine("- Do not run Stop-Process MasterApp, taskkill /IM MasterApp.exe, or scripts that terminate the host.");
        builder.AppendLine("- If MasterApp must restart after your code changes, finish the work and say that a MasterApp relaunch is required; the host will schedule the relaunch after your turn is safely persisted.");
        builder.AppendLine("- Build and file changes are fine when needed, but keep commands scoped to the selected workspace unless the user explicitly approves broader access.");
        builder.AppendLine();
        builder.AppendLine("User request:");
        builder.AppendLine(run.Prompt);
        return builder.ToString();
    }

    private void ApplySharedCodexEvent(CodexChatRun run, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("payload", out var payload) &&
                payload.TryGetProperty("id", out var idProp))
            {
                var sessionId = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    run.SharedSessionId = sessionId!;
                    AdoptSharedSessionId(run);
                }
            }
        }
        catch
        {
            // Non-JSON lines are expected from some Codex modes.
        }
    }

    private async Task<CodexDecisionEnvelope> AskBrokerForDecisionAsync(CodexChatRun run, CancellationToken cancellationToken)
    {
        return IsOllamaProvider(run.Provider)
            ? await AskOllamaForDecisionAsync(run, cancellationToken)
            : await AskCodexForDecisionAsync(run, cancellationToken);
    }

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
            10 * 60 * 1000,
            Encoding.UTF8);

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

    private async Task<CodexDecisionEnvelope> AskOllamaForDecisionAsync(CodexChatRun run, CancellationToken cancellationToken)
    {
        var prompt = BuildDecisionPrompt(run);
        var finalPrompt = $"{prompt}\n\nReturn a single raw JSON object only. Do not use markdown fences, prose, or explanations.";
        var json = await RequestOllamaTextAsync(run.Model, finalPrompt, cancellationToken);
        var normalized = ExtractJsonObject(json);

        try
        {
            return JsonSerializer.Deserialize<CodexDecisionEnvelope>(normalized, JsonOptions.Default)
                   ?? throw new InvalidOperationException("Ollama returned an invalid decision.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Ollama decision could not be parsed: {ex.Message}");
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
        builder.AppendLine("- A command exiting successfully does not prove that a file was changed.");
        builder.AppendLine("- If an edit attempt produced no confirmed file change yet, treat it as a no-op and keep investigating.");
        builder.AppendLine("- For UI or code fixes, identify the exact target file before editing; do not rely on blind wildcard replacements.");
        builder.AppendLine("- Do not choose build, restart, or final-success after an edit attempt unless the relevant file change is visible in Changed files so far, or the user asked only for inspection/restart.");
        builder.AppendLine("- Never ask for multiple commands at once.");
        builder.AppendLine("- Keep commands Windows PowerShell compatible.");
        builder.AppendLine("- Prefer specific, minimal commands.");
        builder.AppendLine("- Investigate only when investigation is actually necessary to answer the user.");
        builder.AppendLine("- Avoid broad repository scans and avoid searching the whole workspace by default.");
        builder.AppendLine("- Do not repeat the same command, the same path guess, or the same failed inspection twice.");
        builder.AppendLine("- If a path probe fails or returns no useful result, pivot to a different concrete file or directory based on prior evidence.");
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
        var workspaceChoice = FindWorkspaceChoice(run.WorkspacePath);
        var workspaceContext = BuildWorkspacePromptContext(run.WorkspacePath);
        builder.AppendLine($"Current workspace: {run.WorkspacePath}");
        builder.AppendLine($"Current model: {run.Model}");
        builder.AppendLine($"Current workspace kind: {workspaceChoice?.Kind ?? "workspace"}");
        foreach (var line in workspaceContext.PromptLines)
        {
            builder.AppendLine(line);
        }
        builder.AppendLine("Allowed workspaces:");
        foreach (var workspace in GetWorkspaceChoiceRecords())
        {
            builder.AppendLine($"- {workspace.Path} ({workspace.Kind})");
        }

        if (workspaceContext.IsMasterAppWorkspace)
        {
            builder.AppendLine();
            builder.AppendLine("MasterApp workspace hints:");
            builder.AppendLine("- MasterApp's visible application UI is primarily served from src/MasterApp/wwwroot.");
            builder.AppendLine("- For tab buttons, settings icons, dashboard controls, and app shell visuals, inspect src/MasterApp/wwwroot/masterapp-ui.js, src/MasterApp/wwwroot/masterapp-ui.css, dashboard.html, or related assets before assuming XAML/WPF resource files.");
            builder.AppendLine("- Do not probe App.xaml unless a prior command proved that file exists.");
        }

        if (workspaceChoice is not null &&
            string.Equals(workspaceChoice.Kind, "installed-app", StringComparison.OrdinalIgnoreCase))
        {
            var manifest = TryLoadWorkspaceManifest(run.WorkspacePath);
            builder.AppendLine();
            builder.AppendLine("Installed app packaging rules:");
            builder.AppendLine($"- Incoming package folder: {_context.Settings.IncomingFolder}");
            builder.AppendLine($"- Published artifacts folder: {_context.Settings.PublishedFolder}");
            builder.AppendLine("- This workspace is an installed MasterApp app package, not the main MasterApp repo.");
            builder.AppendLine("- If the user asks to fix this app, the work is not complete after editing files in place.");
            builder.AppendLine("- After the fix, create an updated zip package with app.manifest.json at the zip root and place that zip in the incoming package folder so MasterApp can ingest the update.");
            builder.AppendLine("- Do not claim success for an installed-app fix until the replacement zip exists in the incoming package folder, unless the user explicitly asked only for investigation.");
            builder.AppendLine("- Do not restart MasterApp as a substitute for packaging the installed app update.");
            if (manifest is not null)
            {
                builder.AppendLine($"- App manifest type: {manifest.AppType}");
                if (!string.IsNullOrWhiteSpace(manifest.Publish?.Command))
                {
                    builder.AppendLine($"- Manifest publish command: {TrimForLog(manifest.Publish.Command, 240)}");
                }
                else if (!string.IsNullOrWhiteSpace(manifest.Build?.InstallCommand))
                {
                    builder.AppendLine($"- Manifest build command: {TrimForLog(manifest.Build.InstallCommand, 240)}");
                }

                if (!string.IsNullOrWhiteSpace(manifest.Publish?.OutputPath))
                {
                    builder.AppendLine($"- Manifest publish output: {manifest.Publish.OutputPath}");
                }

                builder.AppendLine("- For source apps, prefer the app's manifest publish/build command for validation when needed, then package the full app root for ingestion.");
            }
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
        else
        {
            builder.AppendLine();
            builder.AppendLine("Changed files so far: none");
            builder.AppendLine("- If you expected an edit already, that edit did not land yet.");
            builder.AppendLine("- Do not build, restart, or claim the fix is complete until you can point to the concrete changed file.");
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
        var workspaceAccess = CodexWorkspacePolicy.EvaluateApprovalWorkspaceAccess(workingDirectory, GetAllowedWorkspacePaths());

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
            IsWorkingDirectoryAllowed = workspaceAccess.IsAllowed,
            CanTrustWorkspace = workspaceAccess.CanTrustWorkspace,
            TrustWorkspacePath = workspaceAccess.TrustWorkspacePath,
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
        if (normalized.Contains("&&", StringComparison.Ordinal) ||
            normalized.Contains("||", StringComparison.Ordinal) ||
            normalized.Contains('>') ||
            normalized.Contains('<') ||
            normalized.Contains("`n", StringComparison.Ordinal) ||
            normalized.Contains("`r", StringComparison.Ordinal))
        {
            return false;
        }

        if (ContainsSensitiveMarker(normalized) || ContainsMutatingMarker(normalized))
        {
            return false;
        }

        var segments = command
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        if (segments.Length == 0)
        {
            return false;
        }

        return segments.All(IsSafeReadOnlySegment);
    }

    private static bool IsSafeReadOnlySegment(string segment)
    {
        var normalized = segment.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (ContainsSensitiveMarker(normalized) || ContainsMutatingMarker(normalized))
        {
            return false;
        }

        if (normalized.Contains("&&", StringComparison.Ordinal) ||
            normalized.Contains("||", StringComparison.Ordinal) ||
            normalized.Contains('>') ||
            normalized.Contains('<') ||
            normalized.Contains("`n", StringComparison.Ordinal) ||
            normalized.Contains("`r", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized.StartsWith("$"))
        {
            return normalized.Contains("get-childitem", StringComparison.Ordinal) ||
                   normalized.Contains("get-content", StringComparison.Ordinal) ||
                   normalized.Contains("select-string", StringComparison.Ordinal) ||
                   normalized.Contains("test-path", StringComparison.Ordinal) ||
                   normalized.Contains("resolve-path", StringComparison.Ordinal) ||
                   normalized.Contains("get-item", StringComparison.Ordinal) ||
                   normalized.Contains("get-location", StringComparison.Ordinal) ||
                   normalized.Contains("write-output", StringComparison.Ordinal);
        }

        if (normalized.StartsWith("if ", StringComparison.Ordinal) || normalized.StartsWith("if(", StringComparison.Ordinal))
        {
            return normalized.Contains("get-childitem", StringComparison.Ordinal) ||
                   normalized.Contains("get-content", StringComparison.Ordinal) ||
                   normalized.Contains("select-string", StringComparison.Ordinal) ||
                   normalized.Contains("test-path", StringComparison.Ordinal) ||
                   normalized.Contains("resolve-path", StringComparison.Ordinal) ||
                   normalized.Contains("write-output", StringComparison.Ordinal);
        }

        string[] safePrefixes =
        {
            "get-childitem", "get-content", "select-string", "test-path", "resolve-path",
            "get-item", "get-location", "git status", "git diff", "dotnet --info",
            "type ", "dir ", "write-output"
        };

        return safePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool ContainsSensitiveMarker(string normalized)
    {
        string[] sensitiveMarkers =
        {
            "secret", "token", "password", "credential", ".env", "secrets.json", "runtime-state",
            "id_rsa", "id_ed25519", "appdata", "localappdata", "$env:", "ssh", "onedrive"
        };

        return sensitiveMarkers.Any(normalized.Contains);
    }

    private static bool ContainsMutatingMarker(string normalized)
    {
        string[] mutatingMarkers =
        {
            "remove-item", "set-content", "add-content", "out-file", "move-item", "copy-item",
            "rename-item", "new-item", "clear-content", "git apply", "git add", "git commit",
            "git checkout", "git switch", "git reset", "git clean", "dotnet build", "dotnet run",
            "msbuild", "start-process", "stop-process", "taskkill", "invoke-webrequest",
            "invoke-restmethod", "curl ", "wget ", "npm ", "pnpm ", "yarn ", "del ", "erase "
        };

        return mutatingMarkers.Any(normalized.Contains);
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
        var extracted = eventLines
            .Select(TryExtractCodexErrorMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return $"Codex exited with code {exitCode}. {TrimForLog(extracted, 900)}";
        }

        var detail = eventLines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(12).ToArray();
        return detail.Length == 0
            ? $"Codex exited with code {exitCode}."
            : $"Codex exited with code {exitCode}. {TrimForLog(string.Join(" | ", detail), 900)}";
    }

    private static string? TryExtractCodexErrorMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var messageProp) &&
                messageProp.ValueKind == JsonValueKind.String)
            {
                return messageProp.GetString();
            }

            if (root.TryGetProperty("error", out var errorProp))
            {
                if (errorProp.ValueKind == JsonValueKind.String)
                {
                    return errorProp.GetString();
                }

                if (errorProp.ValueKind == JsonValueKind.Object &&
                    errorProp.TryGetProperty("message", out var nestedMessage) &&
                    nestedMessage.ValueKind == JsonValueKind.String)
                {
                    return nestedMessage.GetString();
                }
            }

            if (root.TryGetProperty("type", out var typeProp) &&
                string.Equals(typeProp.GetString(), "error", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("payload", out var payload) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("message", out var payloadMessage) &&
                payloadMessage.ValueKind == JsonValueKind.String)
            {
                return payloadMessage.GetString();
            }
        }
        catch
        {
            // Ignore non-JSON output; callers fall back to recent raw lines.
        }

        return null;
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

    private static string ExtractJsonObject(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("The model returned an empty decision.");
        }

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine >= 0)
            {
                trimmed = trimmed[(firstNewLine + 1)..];
            }

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }

            trimmed = trimmed.Trim();
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace >= firstBrace)
        {
            return trimmed[firstBrace..(lastBrace + 1)];
        }

        return trimmed;
    }
}
