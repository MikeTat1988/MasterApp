using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Storage;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
    private readonly BootstrapContext _context;
    private readonly MasterAppRuntime _runtime;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, Channel<CodexServerEvent>> _subscribers = new();
    private readonly Dictionary<string, PendingApprovalContext> _pendingApprovalWaits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _runCancellationSources = new(StringComparer.OrdinalIgnoreCase);

    private CodexRuntimeState _runtimeState;
    private CodexCliResolutionState _resolutionState = new();
    private CodexCliProbeState _probeState = new();
    private string? _resolvedExecutablePath;

    public CodexBrokerService(BootstrapContext context, MasterAppRuntime runtime)
    {
        _context = context;
        _runtime = runtime;
        _runtimeState = _context.RuntimeStateStore.GetCodexRuntime();

        RefreshCliState();
        ApplyPendingRelaunchMarker();
        RecoverInterruptedRun();
    }

    public object GetDashboardResponse()
    {
        EnsureCliState();
        return BuildDashboardResponse();
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
        channel.Writer.TryWrite(new CodexServerEvent("codex.snapshot", BuildDashboardResponse()));
        return new CodexEventSubscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    public Task<CodexChatRun> StartRunAsync(CodexChatRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = request.Prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("Prompt is required.");
        }

        var workspacePath = ResolveWorkspace(request.WorkspacePath);
        var provider = NormalizeProvider(request.Provider, request.Model);
        var requestedMode = NormalizeRequestedMode(request.Mode);
        var taskMode = ResolveTaskModeSelection(prompt, requestedMode);
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? ResolveCurrentModel(_runtimeState)
            : request.Model.Trim();

        if (IsOllamaProvider(provider))
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                model = DefaultOllamaModel;
            }
        }
        else
        {
            EnsureCliReadyForExecution();
            model = ResolveSupportedCodexModel(string.IsNullOrWhiteSpace(model) ? TryReadCurrentCodexModel() : model);
        }

        CodexChatRun run;

        var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_gate)
        {
            if (_runtimeState.ActiveRun is not null &&
                !IsTerminal(_runtimeState.ActiveRun.Status))
            {
                runCancellation.Dispose();
                throw new InvalidOperationException("A Codex run is already in progress.");
            }

            run = new CodexChatRun
            {
                Id = Guid.NewGuid().ToString("N"),
                SharedSessionId = request.SessionId?.Trim() ?? string.Empty,
                Status = "queued",
                Provider = provider,
                RequestedMode = requestedMode,
                TaskMode = taskMode.Mode,
                TaskModeSource = taskMode.Source,
                TaskModeConfidence = taskMode.Confidence,
                TaskModeReason = taskMode.Reason,
                WorkspacePath = workspacePath,
                Model = model,
                Prompt = prompt,
                StartedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            _runtimeState.ActiveRun = Clone(run);
            _runtimeState.PendingApproval = null;
            _runCancellationSources[run.Id] = runCancellation;
            PersistRuntimeState_NoLock();
        }

        AppendUserMessageToCurrentSession(run);
        PublishSnapshot();
        _ = Task.Run(() => ExecuteRunAsync(run, runCancellation.Token), CancellationToken.None);
        return Task.FromResult(Clone(run));
    }

    public Task<CodexChatRun> StopRunAsync(CodexStopRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CancellationTokenSource? runCancellation;
        CodexChatRun activeRun;

        lock (_gate)
        {
            if (_runtimeState.ActiveRun is null || IsTerminal(_runtimeState.ActiveRun.Status))
            {
                throw new InvalidOperationException("No active Codex run is in progress.");
            }

            if (!string.IsNullOrWhiteSpace(request.RunId) &&
                !string.Equals(_runtimeState.ActiveRun.Id, request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Requested run was not found.");
            }

            activeRun = Clone(_runtimeState.ActiveRun);
            if (!_runCancellationSources.TryGetValue(activeRun.Id, out runCancellation))
            {
                activeRun = MarkRunInterrupted(activeRun, "Recovered a stale Codex session that no longer had a live process.");
                _runtimeState.ActiveRun = Clone(activeRun);
                if (_runtimeState.PendingApproval is not null &&
                    string.Equals(_runtimeState.PendingApproval.RunId, activeRun.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingApprovalWaits.Remove(_runtimeState.PendingApproval.Id);
                    _runtimeState.PendingApproval = null;
                }

                PersistRuntimeState_NoLock();
                PublishSnapshot();
                return Task.FromResult(activeRun);
            }

            activeRun.Status = "stopping";
            activeRun.Summary = "Stop requested.";
            activeRun.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _runtimeState.ActiveRun = Clone(activeRun);
            PersistRuntimeState_NoLock();
        }

        PublishSnapshot();
        runCancellation!.Cancel();
        if (IsOllamaProvider(activeRun.Provider))
        {
            TryStopOllamaModel(activeRun.Model);
        }

        return Task.FromResult(activeRun);
    }

    public Task ClearSessionAsync(CodexNewSessionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_runtimeState.ActiveRun is not null &&
                !IsTerminal(_runtimeState.ActiveRun.Status) &&
                _runCancellationSources.ContainsKey(_runtimeState.ActiveRun.Id))
            {
                throw new InvalidOperationException("Stop the active run before starting a new session.");
            }

            if (_runtimeState.ActiveRun is not null &&
                !string.IsNullOrWhiteSpace(request.RunId) &&
                !string.Equals(_runtimeState.ActiveRun.Id, request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Requested session was not found.");
            }

            if (_runtimeState.ActiveRun is not null &&
                _runCancellationSources.Remove(_runtimeState.ActiveRun.Id, out var runCancellation))
            {
                runCancellation.Dispose();
            }

            if (_runtimeState.PendingApproval is not null)
            {
                _pendingApprovalWaits.Remove(_runtimeState.PendingApproval.Id);
            }

            _runtimeState.ActiveRun = null;
            _runtimeState.PendingApproval = null;
            PersistRuntimeState_NoLock();
        }

        ResetCurrentSession();
        PublishSnapshot();
        return Task.CompletedTask;
    }

    public Task SetModelAsync(CodexModelRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var model = request.Model?.Trim();
        var provider = NormalizeProvider(request.Provider, request.Model);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Model is required.");
        }

        if (!IsOllamaProvider(provider))
        {
            EnsureCliReadyForExecution();
            model = ResolveSupportedCodexModel(model);
        }

        lock (_gate)
        {
            _runtimeState.CurrentProvider = provider;
            _runtimeState.CurrentModel = model;
            PersistRuntimeState_NoLock();
        }

        PublishSnapshot();
        return Task.CompletedTask;
    }

    public Task<CodexChatRun> ResolveApprovalAsync(CodexApprovalDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = request.Decision?.Trim().ToLowerInvariant();
        if (decision is not "approve" and not "approve-once" and not "trust" and not "reject")
        {
            throw new InvalidOperationException("Decision must be 'approve', 'approve-once', 'trust', or 'reject'.");
        }

        PendingApprovalContext? pendingContext;
        CodexChatRun? activeRun;

        lock (_gate)
        {
            if (_runtimeState.PendingApproval is null)
            {
                throw new InvalidOperationException("No approval is currently pending.");
            }

            if (!string.Equals(_runtimeState.PendingApproval.Id, request.ApprovalId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_runtimeState.PendingApproval.RunId, request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Approval request not found.");
            }

            if (!_pendingApprovalWaits.TryGetValue(request.ApprovalId, out pendingContext))
            {
                throw new InvalidOperationException("Approval request has already been resolved.");
            }

            if (decision == "trust" && !_runtimeState.PendingApproval.CanTrustWorkspace)
            {
                throw new InvalidOperationException("This approval cannot trust a workspace.");
            }

            if (decision == "trust")
            {
                TrustWorkspace_NoLock(_runtimeState.PendingApproval.TrustWorkspacePath);
            }

            activeRun = _runtimeState.ActiveRun is null ? null : Clone(_runtimeState.ActiveRun);
            _runtimeState.PendingApproval.Status = decision is "approve" or "approve-once" or "trust" ? "approved" : "rejected";
            _runtimeState.PendingApproval.ResolvedAtUtc = DateTimeOffset.UtcNow;
            _runtimeState.PendingApproval = null;

            if (_runtimeState.ActiveRun is not null)
            {
                _runtimeState.ActiveRun.Status = decision is "approve" or "approve-once" or "trust" ? "running-command" : "processing";
                _runtimeState.ActiveRun.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            PersistRuntimeState_NoLock();
        }

        pendingContext!.Completion.TrySetResult(new CodexApprovalDecisionRequest
        {
            RunId = request.RunId,
            ApprovalId = request.ApprovalId,
            Decision = decision
        });

        PublishSnapshot();
        return Task.FromResult(activeRun ?? new CodexChatRun());
    }

    private void TrustWorkspace_NoLock(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new InvalidOperationException("Workspace path is required.");
        }

        var fullPath = Path.GetFullPath(workspacePath);
        if (!_context.Settings.WorkspacePaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            _context.Settings.WorkspacePaths.Add(fullPath);
            File.WriteAllText(
                _context.Paths.SettingsFile,
                JsonSerializer.Serialize(_context.Settings, JsonOptions.DefaultIndented));
            _context.Log.Codex("CodexBrokerService", $"Trusted Codex workspace: {fullPath}");
        }
    }

    private async Task ExecuteRunAsync(CodexChatRun run, CancellationToken cancellationToken)
    {
        WorkspaceSnapshot baselineSnapshot;

        try
        {
            baselineSnapshot = CaptureWorkspaceSnapshot(run.WorkspacePath);
        }
        catch (Exception ex)
        {
            baselineSnapshot = new WorkspaceSnapshot(run.WorkspacePath);
            AppendLog(run, "system", $"Workspace snapshot warning: {ex.Message}");
        }

        try
        {
            UpdateRun(run, "processing", string.IsNullOrWhiteSpace(run.SharedSessionId)
                ? "Starting shared Codex session."
                : "Resuming shared Codex session.");

            await RunSharedCodexTurnAsync(run, cancellationToken);
            try
            {
                run.ChangedFiles = GetChangedFiles(baselineSnapshot, CaptureWorkspaceSnapshot(run.WorkspacePath));
            }
            catch (Exception ex)
            {
                AppendLog(run, "system", $"Changed file scan warning: {ex.Message}");
            }

            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                ShouldScheduleHostRestartAfterSharedTurn(run))
            {
                var relaunch = await _runtime.ScheduleSelfRelaunchAsync(
                    run.Id,
                    run.WorkspacePath,
                    "Codex completed a run that requested a MasterApp restart.");
                run.RestartStatus = relaunch;
                run.Status = relaunch.Status is "scheduled" or "launched" ? "restart-scheduled" : "failed";
                run.FailureMessage = run.Status == "failed" ? relaunch.Message : null;
                AppendLog(run, "restart", relaunch.Message);
            }
        }
        catch (OperationCanceledException)
        {
            run.Status = "stopped";
            run.Summary = "Stopped.";
            run.FailureMessage = "Run stopped.";
            AppendLog(run, "system", "Run stopped.");
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.FailureMessage = ex.Message;
            AppendLog(run, "error", ex.ToString());
            _context.Log.Codex("CodexBrokerService", $"Run {run.Id} failed.", ex);
        }
        finally
        {
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (IsTerminal(run.Status))
            {
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                var assistantText = !string.IsNullOrWhiteSpace(run.ResponseText)
                    ? run.ResponseText
                    : (!string.IsNullOrWhiteSpace(run.FailureMessage) ? run.FailureMessage : run.Summary);
                AppendAssistantMessageToCurrentSession(run, assistantText, run.Status);
            }

            TrimForPersistence(run);
            lock (_gate)
            {
                if (_runCancellationSources.Remove(run.Id, out var runCancellation))
                {
                    runCancellation.Dispose();
                }

                _runtimeState.ActiveRun = Clone(run);
                if (_runtimeState.PendingApproval is not null &&
                    string.Equals(_runtimeState.PendingApproval.RunId, run.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _runtimeState.PendingApproval = null;
                }

                PersistRuntimeState_NoLock();
            }

            PublishSnapshot();
        }
    }

    private bool TrySkipRepeatedCommand(CodexChatRun run, CodexApprovalRequest approval, out string? repeatedLoopMessage)
    {
        repeatedLoopMessage = null;
        if (!string.Equals(approval.Kind, "command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = NormalizeLoopCommand(approval.Command);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var matches = run.ApprovalHistory
            .Where(item => string.Equals(item.Kind, "command", StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(NormalizeLoopCommand(item.Command), normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return false;
        }

        const string summary = "Skipped a repeated command so Codex can pivot instead of burning orchestration steps.";
        AppendLog(run, "system", $"{summary} Command: {approval.Command}");
        run.ApprovalHistory.Add(new CodexApprovalRecord
        {
            Id = approval.Id,
            Kind = approval.Kind,
            Summary = approval.Summary,
            Command = approval.Command,
            WorkingDirectory = approval.WorkingDirectory,
            Decision = "skipped",
            OutputSummary = summary,
            RequestedAtUtc = approval.RequestedAtUtc,
            ResolvedAtUtc = DateTimeOffset.UtcNow
        });

        if (matches.Count >= 2)
        {
            repeatedLoopMessage = "Codex entered a repeated-command loop and was stopped before wasting more orchestration steps. The broker now requires it to pivot after duplicate inspections.";
        }

        return true;
    }

    private static string NormalizeLoopCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        return Regex.Replace(command.Trim(), "\\s+", " ");
    }

    private static bool ShouldScheduleHostRestartAfterSharedTurn(CodexChatRun run)
    {
        var prompt = run.Prompt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var text = prompt.ToLowerInvariant();
        if (!text.Contains("restart", StringComparison.Ordinal) &&
            !text.Contains("relaunch", StringComparison.Ordinal) &&
            !text.Contains("перезап", StringComparison.Ordinal))
        {
            return false;
        }

        return run.ChangedFiles.Count > 0 ||
               text.Contains("after the change", StringComparison.Ordinal) ||
               text.Contains("после", StringComparison.Ordinal);
    }

    private Task<CodexApprovalDecisionRequest> WaitForApprovalAsync(
        CodexChatRun run,
        CodexApprovalRequest approval,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CodexApprovalDecisionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _runtimeState.PendingApproval = Clone(approval);
            _runtimeState.ActiveRun = Clone(run);
            _runtimeState.ActiveRun.Status = "waiting-approval";
            _runtimeState.ActiveRun.UpdatedAtUtc = DateTimeOffset.UtcNow;
            PersistRuntimeState_NoLock();
            _pendingApprovalWaits[approval.Id] = new PendingApprovalContext(approval, completion);
        }

        PublishSnapshot();
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    private object BuildDashboardResponse()
    {
        CodexRuntimeState runtime;
        CodexCliResolutionState resolution;
        CodexCliProbeState probe;

        lock (_gate)
        {
            runtime = Clone(_runtimeState);
            resolution = _resolutionState with { AttemptedPaths = _resolutionState.AttemptedPaths.ToList() };
            probe = _probeState with { };
        }

        var currentProvider = ResolveCurrentProvider(runtime);
        var currentModel = ResolveCurrentModel(runtime);
        var ollama = GetOllamaStatus();

        return new
        {
            resolvedExecutablePath = resolution.ResolvedExecutablePath,
            cliResolutionStatus = resolution.Status,
            cliResolutionError = resolution.LastError,
            cliResolutionAttemptedPaths = resolution.AttemptedPaths,
            cliProbe = probe,
            currentProvider,
            currentModel,
            availableModes = GetAvailableTaskModes(),
            availableModels = GetAvailableModels(),
            configuredWorkspaces = GetWorkspaceChoices(),
            currentSessionId = GetCurrentSessionId(),
            recentChats = GetRecentChats(4),
            activeRun = runtime.ActiveRun,
            pendingApproval = runtime.PendingApproval,
            autoApproveReadOnlyCommands = true,
            ollama,
            usage = BuildUsageSummary(runtime, currentProvider, currentModel),
            logsPath = _context.Log.GetPath(Diagnostics.LogKind.Codex),
            preferredBuildCommand = _context.Settings.PreferredBuildCommand,
            preferredRestartCommand = _context.Settings.PreferredRestartCommand,
            lastRelaunch = _context.RuntimeStateStore.GetLastRelaunch()
        };
    }

    private void PublishRun(CodexChatRun run)
    {
        lock (_gate)
        {
            _runtimeState.ActiveRun = Clone(run);
            PersistRuntimeState_NoLock();
        }

        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        var payload = BuildDashboardResponse();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(new CodexServerEvent("codex.snapshot", payload));
        }
    }

    private void PersistRuntimeState_NoLock()
    {
        _context.RuntimeStateStore.SetCodexRuntime(_runtimeState);
    }

    private void EnsureCliState()
    {
        lock (_gate)
        {
            if (!string.Equals(_resolutionState.Status, "idle", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        RefreshCliState();
    }

    private void EnsureCliReadyForExecution()
    {
        EnsureCliState();

        lock (_gate)
        {
            if (!string.Equals(_resolutionState.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(_resolutionState.LastError ?? "Codex executable is not available.");
            }

            if (!string.Equals(_probeState.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(_probeState.LastError ?? "Codex CLI probe failed.");
            }
        }
    }
}
