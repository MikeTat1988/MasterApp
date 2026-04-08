using MasterApp.Bootstrap;
using MasterApp.Diagnostics;
using MasterApp.Models;
using System.Diagnostics;

namespace MasterApp.Tunnel;

public sealed class TunnelManager : IDisposable
{
    private readonly object _gate = new();
    private readonly BootstrapContext _context;

    private Process? _process;
    private TunnelStatusSnapshot _snapshot = new();

    public TunnelManager(BootstrapContext context)
    {
        _context = context;
        TryAdoptPersistedProcess();
    }

    public TunnelStatusSnapshot Snapshot()
    {
        lock (_gate)
        {
            RefreshProcessStateUnsafe();
            return new TunnelStatusSnapshot
            {
                Status = _snapshot.Status,
                IsRunning = _snapshot.IsRunning,
                ProcessId = _snapshot.ProcessId,
                LastExitCode = _snapshot.LastExitCode,
                LastMessage = _snapshot.LastMessage,
                LastError = _snapshot.LastError,
                StartedAtUtc = _snapshot.StartedAtUtc,
                StoppedAtUtc = _snapshot.StoppedAtUtc
            };
        }
    }

    public OperationResult Start()
    {
        lock (_gate)
        {
            RefreshProcessStateUnsafe();
            if (_process is { HasExited: false })
            {
                return OperationResult.Success("Tunnel is already running.");
            }

            if (string.IsNullOrWhiteSpace(_context.Secrets.CloudflareTunnelToken) ||
                _context.Secrets.CloudflareTunnelToken.Contains("PASTE_TOKEN_HERE", StringComparison.OrdinalIgnoreCase))
            {
                UpdateSnapshot("Failed", false, "Token is missing.", "TOKEN_MISSING");
                return OperationResult.Failure("TOKEN_MISSING");
            }

            if (!File.Exists(_context.Settings.CloudflaredPath))
            {
                UpdateSnapshot("Failed", false, "cloudflared.exe was not found.", "CLOUDFLARED_NOT_FOUND");
                return OperationResult.Failure("CLOUDFLARED_NOT_FOUND");
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _context.Settings.CloudflaredPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(_context.Settings.CloudflaredPath)!
                };

                startInfo.ArgumentList.Add("tunnel");
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add("--token");
                startInfo.ArgumentList.Add(_context.Secrets.CloudflareTunnelToken);

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        _context.Log.Tunnel("cloudflared", args.Data);
                        lock (_gate)
                        {
                            _snapshot.LastMessage = args.Data;
                        }
                    }
                };

                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        _context.Log.Tunnel("cloudflared.stderr", args.Data);
                        lock (_gate)
                        {
                            _snapshot.LastMessage = args.Data;
                        }
                    }
                };

                _context.Log.Tunnel("TunnelManager", $"Starting: \"{_context.Settings.CloudflaredPath}\" tunnel run --token <REDACTED>");

                if (!process.Start())
                {
                    UpdateSnapshot("Failed", false, "cloudflared.exe did not start.", "TUNNEL_PROCESS_START_FAILED");
                    return OperationResult.Failure("TUNNEL_PROCESS_START_FAILED");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
                _snapshot = new TunnelStatusSnapshot
                {
                    Status = "Running",
                    IsRunning = true,
                    ProcessId = process.Id,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    LastMessage = "Tunnel process started."
                };
                _context.RuntimeStateStore.SetTunnelProcess(process.Id, _snapshot.StartedAtUtc);

                _context.Log.Tunnel("TunnelManager", $"Tunnel started. PID={process.Id}");
                return OperationResult.Success($"Tunnel started. PID={process.Id}");
            }
            catch (Exception ex)
            {
                UpdateSnapshot("Failed", false, ex.Message, "TUNNEL_PROCESS_START_FAILED");
                _context.Log.Tunnel("TunnelManager", "Tunnel start failed.", ex);
                return OperationResult.Failure(ex.Message);
            }
        }
    }

    public OperationResult Stop()
    {
        lock (_gate)
        {
            RefreshProcessStateUnsafe();
            if (_process is null || _process.HasExited)
            {
                UpdateSnapshot("Stopped", false, "Tunnel is already stopped.", null);
                _context.RuntimeStateStore.SetTunnelProcess(null, null);
                return OperationResult.Success("Tunnel is already stopped.");
            }

            try
            {
                var process = _process;
                var pid = process.Id;
                _process = null;
                _context.Log.Tunnel("TunnelManager", $"Stopping tunnel. PID={pid}");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);

                UpdateSnapshot("Stopped", false, $"Tunnel stopped. PID={pid}", null);
                _context.RuntimeStateStore.SetTunnelProcess(null, null);
                process.Dispose();
                return OperationResult.Success($"Tunnel stopped. PID={pid}");
            }
            catch (Exception ex)
            {
                UpdateSnapshot("Failed", false, ex.Message, "TUNNEL_STOP_FAILED");
                _context.Log.Tunnel("TunnelManager", "Tunnel stop failed.", ex);
                return OperationResult.Failure(ex.Message);
            }
        }
    }

    public OperationResult Restart()
    {
        var stop = Stop();
        var start = Start();

        if (start.Ok)
        {
            return OperationResult.Success($"Restart completed. {start.Message}");
        }

        return OperationResult.Failure($"Restart failed. Stop={stop.Message}; Start={start.Message}");
    }

    private void UpdateSnapshot(string status, bool isRunning, string message, string? error)
    {
        _snapshot.Status = status;
        _snapshot.IsRunning = isRunning;
        _snapshot.LastMessage = message;
        _snapshot.LastError = error;
        _snapshot.ProcessId = isRunning ? _snapshot.ProcessId : null;
        if (!isRunning)
        {
            _snapshot.StoppedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void TryAdoptPersistedProcess()
    {
        lock (_gate)
        {
            var persisted = _context.RuntimeStateStore.GetTunnelProcess();
            if (persisted.ProcessId is not int processId)
            {
                return;
            }

            try
            {
                var process = Process.GetProcessById(processId);
                if (process.HasExited || !string.Equals(process.ProcessName, "cloudflared", StringComparison.OrdinalIgnoreCase))
                {
                    process.Dispose();
                    _context.RuntimeStateStore.SetTunnelProcess(null, null);
                    return;
                }

                _process = process;
                _snapshot = new TunnelStatusSnapshot
                {
                    Status = "Running",
                    IsRunning = true,
                    ProcessId = process.Id,
                    StartedAtUtc = persisted.StartedAtUtc,
                    LastMessage = "Adopted existing tunnel process."
                };
                _context.Log.Tunnel("TunnelManager", $"Adopted existing tunnel process. PID={process.Id}");
            }
            catch
            {
                _context.RuntimeStateStore.SetTunnelProcess(null, null);
            }
        }
    }

    private void RefreshProcessStateUnsafe()
    {
        if (_process is null)
        {
            return;
        }

        var process = _process;
        if (!process.HasExited)
        {
            return;
        }

        _snapshot.IsRunning = false;
        _snapshot.Status = "Stopped";
        _snapshot.LastExitCode = process.ExitCode;
        _snapshot.ProcessId = null;
        _snapshot.StoppedAtUtc = DateTimeOffset.UtcNow;
        _snapshot.LastMessage = $"Tunnel process exited. Code={process.ExitCode}";
        _context.RuntimeStateStore.SetTunnelProcess(null, null);
        _process = null;
        process.Dispose();
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch
        {
            // ignore shutdown errors here
        }
    }
}
