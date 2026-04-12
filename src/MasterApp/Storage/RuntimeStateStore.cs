using MasterApp.Diagnostics;
using MasterApp.Models;
using MasterApp.Packages;
using System.Text.Json;

namespace MasterApp.Storage;

public sealed class RuntimeStateStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly FileLogManager _log;
    private RuntimeState _state;

    public RuntimeStateStore(string path, FileLogManager log)
    {
        _path = path;
        _log = log;
        _state = Load();
        Save();
    }

    public RuntimeState Snapshot()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(_state, JsonOptions.Default);
            return JsonSerializer.Deserialize<RuntimeState>(json, JsonOptions.Default) ?? new RuntimeState();
        }
    }

    public IReadOnlyList<InstalledAppState> GetApps()
    {
        lock (_gate)
        {
            return _state.Apps.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public InstalledAppState? GetApp(string appId)
    {
        lock (_gate)
        {
            return _state.Apps.TryGetValue(appId, out var value)
                ? Clone(value)
                : null;
        }
    }

    public void UpsertInstalledApp(InstalledAppState app)
    {
        lock (_gate)
        {
            if (_state.Apps.TryGetValue(app.Id, out var existing))
            {
                existing.Name = app.Name;
                existing.ActiveVersion = app.ActiveVersion;
                existing.InstalledAtUtc = app.InstalledAtUtc;
                existing.Manifest = Clone(app.Manifest);
                existing.RunState = Clone(app.RunState);
                existing.LastPublishedArtifact = app.LastPublishedArtifact is null ? null : Clone(app.LastPublishedArtifact);
                if (!existing.Versions.Contains(app.ActiveVersion, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Versions.Add(app.ActiveVersion);
                }

                existing.Versions = existing.Versions
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                _state.Apps[app.Id] = Clone(app);
            }

            Save();
        }
    }

    public void SetLastPublishResult(PublishResult result)
    {
        lock (_gate)
        {
            _state.LastPublishResult = result;
            Save();
        }
    }

    public void UpdateRunState(string appId, AppRunState runState)
    {
        lock (_gate)
        {
            if (_state.Apps.TryGetValue(appId, out var app))
            {
                app.RunState = Clone(runState);
                Save();
            }
        }
    }

    public void SetAssignedPort(string appId, int? assignedPort)
    {
        lock (_gate)
        {
            if (_state.Apps.TryGetValue(appId, out var app))
            {
                app.AssignedPort = assignedPort;
                Save();
            }
        }
    }

    public void UpdatePublishedArtifact(string appId, PublishArtifactInfo artifact)
    {
        lock (_gate)
        {
            if (_state.Apps.TryGetValue(appId, out var app))
            {
                app.LastPublishedArtifact = Clone(artifact);
                Save();
            }
        }
    }

    public bool RemoveApp(string appId)
    {
        lock (_gate)
        {
            var removed = _state.Apps.Remove(appId);
            if (!removed)
            {
                return false;
            }

            if (string.Equals(_state.LastPublishResult?.AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                _state.LastPublishResult = null;
            }

            if (string.Equals(_state.LastPackageResult?.AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                _state.LastPackageResult = null;
            }

            Save();
            return true;
        }
    }

    public void SetLastPackageResult(PackageInstallResult result)
    {
        lock (_gate)
        {
            _state.LastPackageResult = result;
            Save();
        }
    }

    public void MarkScan(DateTimeOffset scanAtUtc, string reason)
    {
        lock (_gate)
        {
            _state.LastPackageScanAtUtc = scanAtUtc;
            _state.LastPackageScanReason = reason;
            Save();
        }
    }

    public TunnelProcessState GetTunnelProcess()
    {
        lock (_gate)
        {
            return new TunnelProcessState
            {
                ProcessId = _state.TunnelProcess.ProcessId,
                StartedAtUtc = _state.TunnelProcess.StartedAtUtc
            };
        }
    }

    public void SetTunnelProcess(int? processId, DateTimeOffset? startedAtUtc)
    {
        lock (_gate)
        {
            _state.TunnelProcess = new TunnelProcessState
            {
                ProcessId = processId,
                StartedAtUtc = startedAtUtc
            };
            Save();
        }
    }

    public CodexRuntimeState GetCodexRuntime()
    {
        lock (_gate)
        {
            return Clone(_state.Codex);
        }
    }

    public IReadOnlyList<CodexOperationRecord> GetCodexOperations()
    {
        lock (_gate)
        {
            return _state.CodexOperations
                .OrderByDescending(item => item.StartedAtUtc)
                .Select(Clone)
                .ToArray();
        }
    }

    public void UpsertCodexOperation(CodexOperationRecord operation, int maxItems)
    {
        lock (_gate)
        {
            var index = _state.CodexOperations.FindIndex(item => string.Equals(item.Id, operation.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _state.CodexOperations[index] = Clone(operation);
            }
            else
            {
                _state.CodexOperations.Add(Clone(operation));
            }

            _state.CodexOperations = _state.CodexOperations
                .OrderByDescending(item => item.StartedAtUtc)
                .Take(Math.Max(1, maxItems))
                .ToList();

            Save();
        }
    }

    public void SetCodexRuntime(CodexRuntimeState runtime)
    {
        lock (_gate)
        {
            _state.Codex = Clone(runtime);
            _state.Codex.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public RelaunchStatusRecord? GetLastRelaunch()
    {
        lock (_gate)
        {
            return _state.LastRelaunch is null ? null : Clone(_state.LastRelaunch);
        }
    }

    public void SetLastRelaunch(RelaunchStatusRecord relaunch)
    {
        lock (_gate)
        {
            _state.LastRelaunch = Clone(relaunch);
            Save();
        }
    }

    private RuntimeState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _log.Info("RuntimeStateStore", $"runtime-state.json not found. Creating a new one at {_path}");
                return new RuntimeState();
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<RuntimeState>(json, JsonOptions.Default) ?? new RuntimeState();
        }
        catch (Exception ex)
        {
            _log.Error("RuntimeStateStore", "Failed to load runtime-state.json. Using empty state.", ex);
            return new RuntimeState();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOptions.DefaultIndented));
        }
        catch (Exception ex)
        {
            _log.Error("RuntimeStateStore", "Failed to save runtime-state.json.", ex);
        }
    }

    private static InstalledAppState Clone(InstalledAppState value)
    {
        return new InstalledAppState
        {
            Id = value.Id,
            Name = value.Name,
            ActiveVersion = value.ActiveVersion,
            AssignedPort = value.AssignedPort,
            Versions = value.Versions.ToList(),
            InstalledAtUtc = value.InstalledAtUtc,
            Manifest = Clone(value.Manifest),
            RunState = Clone(value.RunState),
            LastPublishedArtifact = value.LastPublishedArtifact is null ? null : Clone(value.LastPublishedArtifact)
        };
    }

    private static AppManifest Clone(AppManifest value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions.Default);
        return JsonSerializer.Deserialize<AppManifest>(json, JsonOptions.Default) ?? new AppManifest();
    }

    private static AppRunState Clone(AppRunState value)
    {
        return new AppRunState
        {
            Status = value.Status,
            IsRunning = value.IsRunning,
            ProcessId = value.ProcessId,
            Port = value.Port,
            Url = value.Url,
            Message = value.Message,
            StartedAtUtc = value.StartedAtUtc,
            StoppedAtUtc = value.StoppedAtUtc
        };
    }

    private static PublishArtifactInfo Clone(PublishArtifactInfo value)
    {
        return new PublishArtifactInfo
        {
            ArtifactKind = value.ArtifactKind,
            OutputPath = value.OutputPath,
            ZipPath = value.ZipPath
        };
    }

    private static RelaunchStatusRecord Clone(RelaunchStatusRecord value)
    {
        return new RelaunchStatusRecord
        {
            Status = value.Status,
            Message = value.Message,
            BackupDirectory = value.BackupDirectory,
            Command = value.Command,
            OperationId = value.OperationId,
            RequestedAtUtc = value.RequestedAtUtc,
            CompletedAtUtc = value.CompletedAtUtc
        };
    }

    private static CodexRuntimeState Clone(CodexRuntimeState value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions.Default);
        return JsonSerializer.Deserialize<CodexRuntimeState>(json, JsonOptions.Default) ?? new CodexRuntimeState();
    }

    private static CodexOperationRecord Clone(CodexOperationRecord value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions.Default);
        return JsonSerializer.Deserialize<CodexOperationRecord>(json, JsonOptions.Default) ?? new CodexOperationRecord();
    }
}
