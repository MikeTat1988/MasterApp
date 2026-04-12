using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Storage;
using System.Collections.Concurrent;
using System.Text.Json;
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
            if (string.IsNullOrWhiteSpace(model))
            {
                model = TryReadCurrentCodexModel() ?? string.Empty;
            }
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
        if (decision is not "approve" and not "reject")
        {
            throw new InvalidOperationException("Decision must be 'approve' or 'reject'.");
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

            activeRun = _runtimeState.ActiveRun is null ? null : Clone(_runtimeState.ActiveRun);
            _runtimeState.PendingApproval.Status = decision == "approve" ? "approved" : "rejected";
            _runtimeState.PendingApproval.ResolvedAtUtc = DateTimeOffset.UtcNow;
            _runtimeState.PendingApproval = null;

            if (_runtimeState.ActiveRun is not null)
            {
                _runtimeState.ActiveRun.Status = decision == "approve" ? "running-command" : "processing";
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
            UpdateRun(run, "processing", "Preparing Codex request.");

            if (IsOllamaProvider(run.Provider))
            {
                UpdateRun(run, "processing", "Sending request to local Ollama.");
                run.ResponseText = await RequestOllamaResponseAsync(
                    run.Model,
                    run.Prompt,
                    GetConversationMessages(run, 12),
                    cancellationToken);
                run.Summary = TrimForLog(string.IsNullOrWhiteSpace(run.ResponseText) ? "Completed." : run.ResponseText, 240);
                run.Status = "completed";
                return;
            }

            for (var step = 0; step < MaxDecisionSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var decision = await AskCodexForDecisionAsync(run, cancellationToken);
                if (string.Equals(decision.Kind, "final", StringComparison.OrdinalIgnoreCase))
                {
                    run.ResponseText = (decision.Response ?? string.Empty).Trim();
                    run.Summary = TrimForLog(string.IsNullOrWhiteSpace(run.ResponseText) ? "Completed." : run.ResponseText, 240);
                    run.Status = "completed";
                    break;
                }

                var approval = CreateApprovalRequest(run, decision);
                if (ShouldAutoApproveReadOnlyCommand(approval))
                {
                    AppendLog(run, "approval", $"Auto-approved read-only command: {approval.Summary}");
                    var autoRecord = await ExecuteApprovedActionAsync(run, decision, approval, cancellationToken);
                    autoRecord.Decision = "auto-approved";
                    run.ApprovalHistory.Add(autoRecord);

                    try
                    {
                        run.ChangedFiles = GetChangedFiles(baselineSnapshot, CaptureWorkspaceSnapshot(run.WorkspacePath));
                    }
                    catch (Exception ex)
                    {
                        AppendLog(run, "system", $"Changed file scan warning: {ex.Message}");
                    }

                    run.Status = "processing";
                    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    PublishRun(run);
                    continue;
                }

                var resolution = await WaitForApprovalAsync(run, approval, cancellationToken);
                if (string.Equals(resolution.Decision, "reject", StringComparison.OrdinalIgnoreCase))
                {
                    run.ApprovalHistory.Add(new CodexApprovalRecord
                    {
                        Id = approval.Id,
                        Kind = approval.Kind,
                        Summary = approval.Summary,
                        Command = approval.Command,
                        WorkingDirectory = approval.WorkingDirectory,
                        Decision = "reject",
                        OutputSummary = "User rejected the requested action.",
                        RequestedAtUtc = approval.RequestedAtUtc,
                        ResolvedAtUtc = DateTimeOffset.UtcNow
                    });
                    AppendLog(run, "approval", $"{approval.Kind} rejected: {approval.Summary}");
                    PublishRun(run);
                    continue;
                }

                var record = await ExecuteApprovedActionAsync(run, decision, approval, cancellationToken);
                run.ApprovalHistory.Add(record);

                try
                {
                    run.ChangedFiles = GetChangedFiles(baselineSnapshot, CaptureWorkspaceSnapshot(run.WorkspacePath));
                }
                catch (Exception ex)
                {
                    AppendLog(run, "system", $"Changed file scan warning: {ex.Message}");
                }

                if (string.Equals(approval.Kind, "restart", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                run.Status = "processing";
                run.UpdatedAtUtc = DateTimeOffset.UtcNow;
                PublishRun(run);
            }

            if (!IsTerminal(run.Status))
            {
                run.Status = "failed";
                run.FailureMessage = "Codex reached the step limit before finishing.";
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
            recentChats = GetRecentChats(5),
            activeRun = runtime.ActiveRun,
            pendingApproval = runtime.PendingApproval,
            autoApproveReadOnlyCommands = true,
            ollama,
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
