using MasterApp.Models;
using MasterApp.Storage;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace MasterApp.Hosting;

public sealed partial class CodexBrokerService
{
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

            lock (_gate)
            {
                if (_runtimeState.ActiveRun is not null &&
                    !string.IsNullOrWhiteSpace(marker.OperationId) &&
                    string.Equals(_runtimeState.ActiveRun.Id, marker.OperationId, StringComparison.OrdinalIgnoreCase))
                {
                    _runtimeState.ActiveRun.RestartStatus = marker;
                    if (string.Equals(marker.Status, "launched", StringComparison.OrdinalIgnoreCase))
                    {
                        _runtimeState.ActiveRun.Status = "completed";
                        _runtimeState.ActiveRun.CompletedAtUtc = marker.CompletedAtUtc ?? DateTimeOffset.UtcNow;
                    }

                    PersistRuntimeState_NoLock();
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log.Codex("CodexBrokerService", $"Failed to apply relaunch marker: {ex.Message}", ex);
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

    private static bool TryResolveConfiguredPath(string configuredCommand, out string? resolvedPath)
    {
        resolvedPath = null;

        if (Path.IsPathRooted(configuredCommand) && File.Exists(configuredCommand))
        {
            resolvedPath = configuredCommand;
            return true;
        }

        if ((configuredCommand.Contains(Path.DirectorySeparatorChar) || configuredCommand.Contains(Path.AltDirectorySeparatorChar)) &&
            File.Exists(Path.GetFullPath(configuredCommand)))
        {
            resolvedPath = Path.GetFullPath(configuredCommand);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> FindCommandOnPath(string commandName)
    {
        try
        {
            var capture = RunProcessCapture("where.exe", new[] { commandName }, Directory.GetCurrentDirectory(), 10_000);
            return (capture.StandardOutput + Environment.NewLine + capture.StandardError)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string BuildResolutionError(IReadOnlyList<string> attemptedPaths)
    {
        if (attemptedPaths.Count == 0)
        {
            return "Codex executable was not found.";
        }

        return "Codex executable was not found. Tried: " + string.Join(" | ", attemptedPaths.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static Task<int> RunPowerShellCommandAsync(
        string command,
        string workingDirectory,
        Func<string, Task> onLine,
        CancellationToken cancellationToken,
        int timeoutMilliseconds)
    {
        return RunProcessAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-Command", command },
            workingDirectory,
            onLine,
            onLine,
            cancellationToken,
            timeoutMilliseconds);
    }

    private static ProcessCaptureResult RunProcessCapture(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
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
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore kill failures after timeout
            }

            throw new TimeoutException($"Process timed out after {timeoutMilliseconds}ms: {fileName}");
        }

        return new ProcessCaptureResult(process.ExitCode, stdout.ToString(), stderr.ToString());
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

    private sealed record ProcessCaptureResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record CodexCliResolutionState
    {
        public string Status { get; init; } = "idle";
        public string? ResolvedExecutablePath { get; init; }
        public List<string> AttemptedPaths { get; init; } = new();
        public string? LastError { get; init; }
        public DateTimeOffset? ResolvedAtUtc { get; init; }
    }

    public sealed record CodexCliProbeState
    {
        public string Status { get; init; } = "idle";
        public bool IsReady { get; init; }
        public string? Mode { get; init; }
        public bool StructuredOutput { get; init; }
        public string? Version { get; init; }
        public DateTimeOffset? ProbedAtUtc { get; init; }
        public string? LastError { get; init; }
    }

public sealed class CodexChatRequest
{
    public string Prompt { get; set; } = string.Empty;
    public string? WorkspacePath { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Mode { get; set; }
}

public sealed class CodexStopRequest
{
    public string? RunId { get; set; }
}

public sealed class CodexNewSessionRequest
{
    public string? RunId { get; set; }
}

public sealed class CodexModelRequest
{
    public string Provider { get; set; } = CodexProvider;
    public string Model { get; set; } = string.Empty;
}

    public sealed class CodexApprovalDecisionRequest
    {
        public string RunId { get; set; } = string.Empty;
        public string ApprovalId { get; set; } = string.Empty;
        public string Decision { get; set; } = string.Empty;
    }

    public sealed class CodexModelInfo
    {
        public string Provider { get; set; } = CodexProvider;
        public string Slug { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string> SupportedReasoningLevels { get; set; } = new();
    }

    public sealed class CodexRecentChat
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public string Cwd { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public string UserPreview { get; set; } = string.Empty;
        public string AssistantPreview { get; set; } = string.Empty;
        public List<CodexChatMessage> Messages { get; set; } = new();
        public string? SessionPath { get; set; }
    }

    public sealed record CodexServerEvent(string Type, object Payload);

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
