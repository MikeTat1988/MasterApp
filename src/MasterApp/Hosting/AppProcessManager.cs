using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Packages;
using MasterApp.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MasterApp.Hosting;

public sealed class AppProcessManager : IDisposable
{
    private readonly BootstrapContext _context;
    private readonly ConcurrentDictionary<string, ManagedAppProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private readonly SemaphoreSlim _portGate = new(1, 1);

    public AppProcessManager(BootstrapContext context)
    {
        _context = context;
    }

    public AppRunState Snapshot(string appId)
    {
        var installed = _context.RuntimeStateStore.GetApp(appId);
        if (installed is null)
        {
            return new AppRunState { Status = "missing", Message = "App is not installed." };
        }

        if (_processes.TryGetValue(appId, out var managed) && managed.Process is { HasExited: false })
        {
            return BuildRunState(installed, managed.Process, "running", "App is running.");
        }

        if (installed.RunState.IsRunning && string.Equals(installed.RunState.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return IsStoredRunStateAlive(installed)
                ? installed.RunState
                : new AppRunState
                {
                    Status = "stopped",
                    IsRunning = false,
                    Message = "App is not running.",
                    StoppedAtUtc = DateTimeOffset.UtcNow
                };
        }

        return installed.RunState;
    }

    public async Task<AppRunState> EnsureRunningAsync(string appId, CancellationToken cancellationToken = default)
    {
        var installed = _context.RuntimeStateStore.GetApp(appId)
                       ?? throw new InvalidOperationException($"APP_NOT_FOUND: {appId}");

        if (string.Equals(installed.Manifest.AppType, AppTypes.Static, StringComparison.OrdinalIgnoreCase))
        {
            var staticState = new AppRunState
            {
                Status = "static",
                IsRunning = true,
                Url = null,
                Message = "Static app is served directly by MasterApp."
            };
            _context.RuntimeStateStore.UpdateRunState(appId, staticState);
            return staticState;
        }

        if (_processes.TryGetValue(appId, out var existing) && existing.Process is { HasExited: false })
        {
            if (ShouldRestartForInstalledUpdate(installed, existing.Process))
            {
                _context.Log.Info("AppProcessManager", $"Restarting app '{appId}' to pick up installed version {installed.ActiveVersion}.");
                var stopResult = Stop(appId);
                if (!stopResult.Ok)
                {
                    throw new InvalidOperationException(stopResult.Message);
                }

                installed = _context.RuntimeStateStore.GetApp(appId) ?? installed;
            }
            else
            {
            var existingState = BuildRunState(installed, existing.Process, "running", "App is already running.");
            _context.RuntimeStateStore.UpdateRunState(appId, existingState);
            return existingState;
            }
        }

        installed = await EnsureAssignedPortAsync(installed, cancellationToken);
        var process = StartProcess(installed);
        var runState = await WaitUntilHealthyAsync(installed, process, cancellationToken);
        _processes[appId] = new ManagedAppProcess(installed.Id, process);
        _context.RuntimeStateStore.UpdateRunState(appId, runState);
        return runState;
    }

    public OperationResult Stop(string appId)
    {
        if (!_processes.TryRemove(appId, out var managed))
        {
            var installed = _context.RuntimeStateStore.GetApp(appId);
            string? persistedStopError = null;
            if (installed is not null && TryStopPersistedProcess(installed, out persistedStopError))
            {
                var persistedStoppedState = new AppRunState
                {
                    Status = "stopped",
                    IsRunning = false,
                    Message = "App stopped.",
                    StoppedAtUtc = DateTimeOffset.UtcNow
                };
                _context.RuntimeStateStore.UpdateRunState(appId, persistedStoppedState);
                return OperationResult.Success("App stopped.");
            }

            if (!string.IsNullOrWhiteSpace(persistedStopError))
            {
                return OperationResult.Failure(persistedStopError);
            }

            var stoppedState = new AppRunState
            {
                Status = "stopped",
                IsRunning = false,
                Message = "App is not running.",
                StoppedAtUtc = DateTimeOffset.UtcNow
            };
            _context.RuntimeStateStore.UpdateRunState(appId, stoppedState);
            return OperationResult.Success("App is not running.");
        }

        try
        {
            if (!managed.Process.HasExited)
            {
                managed.Process.Kill(entireProcessTree: true);
                managed.Process.WaitForExit(5000);
            }

            var stopped = new AppRunState
            {
                Status = "stopped",
                IsRunning = false,
                Message = "App stopped.",
                StoppedAtUtc = DateTimeOffset.UtcNow
            };
            _context.RuntimeStateStore.UpdateRunState(appId, stopped);
            return OperationResult.Success("App stopped.");
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }
        finally
        {
            managed.Process.Dispose();
        }
    }

    private bool TryStopPersistedProcess(InstalledAppState installed, out string? error)
    {
        error = null;
        var processId = installed.RunState.ProcessId;
        if (processId is null ||
            !installed.RunState.IsRunning ||
            !string.Equals(installed.RunState.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited)
            {
                return false;
            }

            if (!IsPersistedProcessForInstalledApp(installed, process))
            {
                _context.Log.Warn("AppProcessManager", $"Stored process {processId.Value} for app '{installed.Id}' is not running from the app install folder.");
                return false;
            }

            _context.Log.Info("AppProcessManager", $"Stopping persisted process {processId.Value} for app '{installed.Id}'.");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool IsPersistedProcessForInstalledApp(InstalledAppState installed, Process process)
    {
        try
        {
            var processPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            var installRoot = Path.GetFullPath(GetInstallRoot(installed))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var processFullPath = Path.GetFullPath(processPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return processFullPath.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   processFullPath.StartsWith(installRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _context.Log.Warn("AppProcessManager", $"Could not verify stored process path for app '{installed.Id}': {ex.Message}");
            return false;
        }
    }

    public string GetTargetBaseUrl(AppManifest manifest)
    {
        return GetTargetBaseUrl(manifest, manifest.Launch.Port);
    }

    public string GetTargetBaseUrl(InstalledAppState installed)
    {
        return GetTargetBaseUrl(installed.Manifest, GetAssignedOrPreferredPort(installed));
    }

    public void Dispose()
    {
        StopAll();

        _httpClient.Dispose();
        _portGate.Dispose();
    }

    public void StopAll()
    {
        var appIds = _processes.Keys
            .Concat(_context.RuntimeStateStore.GetApps()
                .Where(app => !string.Equals(app.Manifest.AppType, AppTypes.Static, StringComparison.OrdinalIgnoreCase))
                .Select(app => app.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var key in appIds)
        {
            Stop(key);
        }
    }

    private Process StartProcess(InstalledAppState installed)
    {
        var manifest = installed.Manifest;
        var installRoot = GetInstallRoot(installed);
        SyncPersistentData(installed, installRoot);
        var executablePath = ResolvePath(installRoot, manifest.Launch.ExecutablePath!);
        var workingDirectory = ResolvePath(installRoot, manifest.Launch.WorkingDirectory);

        var port = GetAssignedOrPreferredPort(installed);
        var startInfo = BuildStartInfo(executablePath, workingDirectory, manifest, port);
        _context.Log.Info("AppProcessManager", $"Starting app {installed.Id}: {startInfo.FileName} {startInfo.Arguments}");

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Exited += (_, _) =>
        {
            _processes.TryRemove(installed.Id, out _);
            var exitState = new AppRunState
            {
                Status = "stopped",
                IsRunning = false,
                Message = $"App exited with code {process.ExitCode}.",
                ProcessId = process.Id,
                StoppedAtUtc = DateTimeOffset.UtcNow
            };
            _context.RuntimeStateStore.UpdateRunState(installed.Id, exitState);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"APP_START_FAILED: {installed.Id}");
        }

        return process;
    }

    private ProcessStartInfo BuildStartInfo(string executablePath, string workingDirectory, AppManifest manifest, int? port)
    {
        var extension = Path.GetExtension(executablePath);
        ProcessStartInfo startInfo;

        if (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"{executablePath}\" {string.Join(' ', manifest.Launch.Arguments)}",
                WorkingDirectory = workingDirectory
            };
        }
        else if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo("dotnet")
            {
                Arguments = $"\"{executablePath}\" {string.Join(' ', manifest.Launch.Arguments.Select(QuoteArgument))}",
                WorkingDirectory = workingDirectory
            };
        }
        else
        {
            startInfo = new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = workingDirectory
            };

            foreach (var argument in manifest.Launch.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;

        foreach (var pair in manifest.Launch.EnvironmentVariables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        if (port is not null)
        {
            var url = GetTargetBaseUrl(manifest, port);
            startInfo.Environment["AppHost__Url"] = url;
            startInfo.Environment["ASPNETCORE_URLS"] = url;
            startInfo.Environment["MASTERAPP_PORT"] = port.Value.ToString();
        }

        startInfo.Environment["MASTERAPP_HOSTED"] = "1";
        startInfo.Environment["MASTERAPP_BASE_PATH"] = "/apps";

        return startInfo;
    }

    private async Task<InstalledAppState> EnsureAssignedPortAsync(InstalledAppState installed, CancellationToken cancellationToken)
    {
        await _portGate.WaitAsync(cancellationToken);
        try
        {
            var current = _context.RuntimeStateStore.GetApp(installed.Id) ?? installed;
            var currentPort = current.AssignedPort;
            if (currentPort is > 0 && IsPortAvailableForAssignment(current.Id, currentPort.Value))
            {
                return current;
            }

            var assignedPort = ReserveAvailablePort(current);
            _context.RuntimeStateStore.SetAssignedPort(current.Id, assignedPort);
            current.AssignedPort = assignedPort;
            _context.Log.Info("AppProcessManager", $"Assigned port {assignedPort} to app '{current.Id}'.");
            return current;
        }
        finally
        {
            _portGate.Release();
        }
    }

    private int ReserveAvailablePort(InstalledAppState installed)
    {
        var preferredPort = installed.Manifest.Launch.Port;
        if (preferredPort is > 0 && IsPortAvailableForAssignment(installed.Id, preferredPort.Value))
        {
            return preferredPort.Value;
        }

        for (var port = 20000; port <= 40000; port++)
        {
            if (IsPortAvailableForAssignment(installed.Id, port))
            {
                return port;
            }
        }

        return GetEphemeralPort();
    }

    private bool IsPortAvailableForAssignment(string appId, int port)
    {
        var reservedByOtherApp = _context.RuntimeStateStore.GetApps()
            .Where(app => !string.Equals(app.Id, appId, StringComparison.OrdinalIgnoreCase))
            .Any(app => GetAssignedOrPreferredPort(app) == port);

        if (reservedByOtherApp)
        {
            return false;
        }

        return !IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);
    }

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private bool IsStoredRunStateAlive(InstalledAppState installed)
    {
        try
        {
            var healthUrl = new Uri(new Uri(GetTargetBaseUrl(installed)), installed.Manifest.Launch.HealthPath ?? "/");
            using var response = _httpClient.GetAsync(healthUrl).GetAwaiter().GetResult();
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private async Task<AppRunState> WaitUntilHealthyAsync(InstalledAppState installed, Process process, CancellationToken cancellationToken)
    {
        var manifest = installed.Manifest;
        var timeout = TimeSpan.FromSeconds(Math.Max(2, manifest.Launch.StartupTimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var healthUrl = new Uri(new Uri(GetTargetBaseUrl(installed)), manifest.Launch.HealthPath ?? "/");

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException($"APP_START_FAILED: process exited with code {process.ExitCode}.");
            }

            try
            {
                using var response = await _httpClient.GetAsync(healthUrl, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    return BuildRunState(installed, process, "running", "App is running.");
                }
            }
            catch
            {
                // retry until timeout
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"APP_START_TIMEOUT: {installed.Id} did not become ready in time.");
    }

    private AppRunState BuildRunState(InstalledAppState installed, Process process, string status, string message)
    {
        var port = GetAssignedOrPreferredPort(installed);
        var startedAtUtc = installed.RunState.StartedAtUtc;
        if (startedAtUtc is null)
        {
            try
            {
                startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch
            {
                startedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        return new AppRunState
        {
            Status = status,
            IsRunning = true,
            ProcessId = process.Id,
            Port = port,
            Url = GetTargetBaseUrl(installed),
            Message = message,
            StartedAtUtc = startedAtUtc
        };
    }

    private static bool ShouldRestartForInstalledUpdate(InstalledAppState installed, Process process)
    {
        if (installed.InstalledAtUtc == default)
        {
            return false;
        }

        try
        {
            var processStartedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return processStartedUtc < installed.InstalledAtUtc;
        }
        catch
        {
            return false;
        }
    }

    private void SyncPersistentData(InstalledAppState installed, string installRoot)
    {
        if (installed.Manifest.DataDirectories.Count == 0)
        {
            return;
        }

        var sharedRoot = Path.Combine(_context.Paths.AppsDirectory, installed.Id, "_shared");
        Directory.CreateDirectory(sharedRoot);

        foreach (var relativePath in installed.Manifest.DataDirectories)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var sharedPath = ResolvePath(sharedRoot, normalized);
            var installedPath = ResolvePath(installRoot, normalized);

            Directory.CreateDirectory(sharedPath);
            Directory.CreateDirectory(installedPath);
            FileSystemHelpers.MergeDirectory(sharedPath, installedPath);
        }
    }

    private static int? GetAssignedOrPreferredPort(InstalledAppState installed)
    {
        return installed.AssignedPort is > 0 ? installed.AssignedPort : installed.Manifest.Launch.Port;
    }

    private static string GetTargetBaseUrl(AppManifest manifest, int? port)
    {
        if (port is null or <= 0)
        {
            throw new InvalidOperationException("APP_PORT_MISSING");
        }

        return manifest.Launch.UrlTemplate.Replace("{port}", port.Value.ToString(), StringComparison.Ordinal);
    }

    private string GetInstallRoot(InstalledAppState installed)
    {
        return Path.Combine(_context.Paths.AppsDirectory, installed.Id, installed.ActiveVersion);
    }

    private static string ResolvePath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("APP_PATH_INVALID");
        }

        return candidate;
    }

    private static string QuoteArgument(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private sealed record ManagedAppProcess(string AppId, Process Process);
}
