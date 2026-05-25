using MasterApp.Bootstrap;
using MasterApp.Diagnostics;
using MasterApp.LifeJournal;
using MasterApp.Models;
using MasterApp.Packages;
using MasterApp.Tunnel;
using MasterApp.Utilities;
using MasterApp.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using MasterApp.Storage;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MasterApp.Hosting;

public sealed class MasterAppRuntime : IDisposable
{
    private readonly BootstrapContext _context;
    private readonly PackageManager _packageManager;
    private readonly AppPublisher _appPublisher;
    private readonly PackageWatcherService _packageWatcher;
    private readonly AppProcessManager _appProcessManager;
    private readonly TunnelManager _tunnelManager;
    private readonly WatchdogStateStore _watchdogStateStore;
    private readonly CodexBrokerService _codexService;
    private readonly LifeJournalService _lifeJournal;
    private readonly LifeJournalFinalizer _lifeJournalFinalizer;
    private readonly HttpClient _proxyClient;
    private readonly FileExtensionContentTypeProvider _contentTypes = new();
    private readonly object _gate = new();

    private WebApplication? _webApp;
    private bool _disposed;

    public MasterAppRuntime(BootstrapContext context)
    {
        _context = context;
        _packageManager = new PackageManager(_context);
        _appPublisher = new AppPublisher(_context);
        _packageWatcher = new PackageWatcherService(_packageManager, _context.Log, _context.Settings.PackageScanIntervalSeconds);
        _watchdogStateStore = new WatchdogStateStore(_context.Paths, _context.Log);
        _appProcessManager = new AppProcessManager(_context);
        _tunnelManager = new TunnelManager(_context);
        _codexService = new CodexBrokerService(_context, this);
        _lifeJournal = new LifeJournalService(_context);
        _lifeJournalFinalizer = new LifeJournalFinalizer(
            _lifeJournal.Store,
            _lifeJournal.Analyzer,
            new LifeJournalLogger(_lifeJournal.RootDirectory),
            _context.Settings.LifeJournal,
            Path.Combine(_lifeJournal.RootDirectory, "finalizer-state.json"));
        _proxyClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true);
    }

    public string LocalUrl => $"http://localhost:{_context.Secrets.LocalPort}";
    public string PublicUrl => $"https://{_context.Secrets.PublicHostname}";
    public string LogsDirectory => _context.Paths.LogsDirectory;

    public void Start()
    {
        lock (_gate)
        {
            if (_webApp is not null)
            {
                return;
            }

            _context.Log.Info("Runtime", "Starting local web host.");
            _webApp = BuildWebApplication();
            _webApp.StartAsync().GetAwaiter().GetResult();
            _context.Log.Info("Runtime", $"Local web host started on {LocalUrl}");

            _packageWatcher.Start();
            _lifeJournalFinalizer.Start();

            if (_context.Settings.AutoStartTunnel)
            {
                var result = _tunnelManager.Start();
                _context.Log.Info("Runtime", $"AutoStartTunnel result: {result.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            _context.Log.Info("Runtime", "Shutdown requested.");

            try
            {
                _packageWatcher.Stop();
            }
            catch (Exception ex)
            {
                _context.Log.Error("Runtime", "Package watcher stop failed.", ex);
            }

            try
            {
                _lifeJournalFinalizer.Dispose();
            }
            catch (Exception ex)
            {
                _context.Log.Error("Runtime", "LifeJournal finalizer stop failed.", ex);
            }

            try
            {
                _tunnelManager.Stop();
            }
            catch (Exception ex)
            {
                _context.Log.Error("Runtime", "Tunnel stop failed.", ex);
            }

            try
            {
                _appProcessManager.Dispose();
            }
            catch (Exception ex)
            {
                _context.Log.Error("Runtime", "App process manager stop failed.", ex);
            }

            try
            {
                if (_webApp is not null)
                {
                    _webApp.StopAsync().GetAwaiter().GetResult();
                    _webApp.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    _webApp = null;
                }
            }
            catch (Exception ex)
            {
                _context.Log.Error("Runtime", "Web host stop failed.", ex);
            }

            _context.Log.Info("Runtime", "Shutdown complete.");
        }
    }

    public OperationResult StartTunnel() => _tunnelManager.Start();
    public OperationResult StopTunnel() => _tunnelManager.Stop();
    public OperationResult RestartTunnel() => _tunnelManager.Restart();
    public Task<OperationResult> RescanPackagesAsync() => _packageWatcher.ScanNowAsync("manual");
    public object GetCodexResponse() => _codexService.GetDashboardResponse();
    public Task<CodexChatRun> StartCodexRunAsync(CodexBrokerService.CodexChatRequest request, CancellationToken cancellationToken = default)
        => _codexService.StartRunAsync(request, cancellationToken);
    public Task<CodexChatRun> StopCodexRunAsync(CodexBrokerService.CodexStopRequest request, CancellationToken cancellationToken = default)
        => _codexService.StopRunAsync(request, cancellationToken);
    public Task ClearCodexSessionAsync(CodexBrokerService.CodexNewSessionRequest request, CancellationToken cancellationToken = default)
        => _codexService.ClearSessionAsync(request, cancellationToken);
    public Task SetCodexModelAsync(CodexBrokerService.CodexModelRequest request, CancellationToken cancellationToken = default)
        => _codexService.SetModelAsync(request, cancellationToken);
    public Task<CodexChatRun> ResolveCodexApprovalAsync(CodexBrokerService.CodexApprovalDecisionRequest request, CancellationToken cancellationToken = default)
        => _codexService.ResolveApprovalAsync(request, cancellationToken);
    public async Task<OperationResult> StartAppAsync(string appId)
    {
        try
        {
            var state = await _appProcessManager.EnsureRunningAsync(appId);
            return OperationResult.Success(state.Message ?? "App started.");
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }
    }

    public OperationResult StopApp(string appId) => _appProcessManager.Stop(appId);

    public OperationResult PublishApp(string appId)
    {
        try
        {
            var result = _appPublisher.Publish(appId);
            return OperationResult.Success(result.Message);
        }
        catch (Exception ex)
        {
            var failed = new PublishResult
            {
                Success = false,
                AppId = appId,
                Message = ex.Message,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            _context.RuntimeStateStore.SetLastPublishResult(failed);
            return OperationResult.Failure(ex.Message);
        }
    }

    public OperationResult DeleteApp(string appId)
    {
        try
        {
            var app = _context.RuntimeStateStore.GetApp(appId);
            if (app is null)
            {
                return OperationResult.Failure($"APP_NOT_FOUND: {appId}");
            }

            var stopResult = _appProcessManager.Stop(appId);
            if (!stopResult.Ok)
            {
                _context.Log.Warn("Runtime", $"Delete requested for app '{appId}', but stopping it first failed: {stopResult.Message}");
            }

            var appsRoot = Path.GetFullPath(_context.Paths.AppsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var appRoot = Path.GetFullPath(Path.Combine(appsRoot, app.Id))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!appRoot.StartsWith(appsRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("APP_DELETE_PATH_INVALID");
            }

            StopAppProcessesFromDirectory(app.Id, appRoot);

            if (!_context.RuntimeStateStore.RemoveApp(appId))
            {
                return OperationResult.Failure($"APP_NOT_FOUND: {appId}");
            }

            var cleanupWarning = TryDeleteAppDirectory(app.Id, appRoot);
            if (!string.IsNullOrWhiteSpace(cleanupWarning))
            {
                return OperationResult.Success($"Deleted {GetPreferredDisplayName(app)} from MasterApp.");
            }

            _context.Log.Info("Runtime", $"Deleted app '{appId}' from {appRoot}.");
            return OperationResult.Success($"Deleted {GetPreferredDisplayName(app)} from MasterApp.");
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }
    }

    private string? TryDeleteAppDirectory(string appId, string appRoot)
    {
        if (!Directory.Exists(appRoot))
        {
            return null;
        }

        const int maxAttempts = 3;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(appRoot, recursive: true);
                _context.Log.Info("Runtime", $"Deleted app files for '{appId}' from {appRoot}.");
                return null;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                {
                    Thread.Sleep(250);
                }
            }
        }

        var message = $"App files could not be removed yet: {lastError?.Message}";
        _context.Log.Warn("Runtime", $"Deleted app '{appId}' from runtime state, but file cleanup failed for {appRoot}. {lastError?.Message}");
        return message;
    }

    private void StopAppProcessesFromDirectory(string appId, string appRoot)
    {
        var normalizedRoot = Path.GetFullPath(appRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? processPath;
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    processPath = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (!IsPathUnderDirectory(processPath, normalizedRoot))
                {
                    continue;
                }

                try
                {
                    _context.Log.Info("Runtime", $"Stopping process {process.Id} from deleted app '{appId}': {processPath}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    _context.Log.Warn("Runtime", $"Could not stop process {process.Id} while deleting app '{appId}': {ex.Message}");
                }
            }
        }
    }

    private static bool IsPathUnderDirectory(string? path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(directory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void OpenDashboard() => ShellHelper.OpenPath(LocalUrl);
    public void OpenPublic() => ShellHelper.OpenPath(PublicUrl);
    public void OpenPhoneQr() => ShellHelper.OpenPath($"{LocalUrl}/qr.html");
    public void OpenLogsFolder() => ShellHelper.OpenPath(_context.Paths.LogsDirectory);
    public void MarkExplicitQuit() => _watchdogStateStore.MarkExplicitQuit();

    public object GetStatusResponse()
    {
        var runtimeState = _context.RuntimeStateStore.Snapshot();
        return new
        {
            appName = "MasterApp",
            localUrl = LocalUrl,
            publicUrl = PublicUrl,
            publicHostname = _context.Secrets.PublicHostname,
            localPort = _context.Secrets.LocalPort,
            settingsFile = _context.Paths.SettingsFile,
            secretsFile = _context.Paths.SecretsFile,
            logsDirectory = _context.Paths.LogsDirectory,
            publishedDirectory = _context.Settings.PublishedFolder,
            tokenPresent = !string.IsNullOrWhiteSpace(_context.Secrets.CloudflareTunnelToken) &&
                           !_context.Secrets.CloudflareTunnelToken.Contains("PASTE_TOKEN_HERE", StringComparison.OrdinalIgnoreCase),
            tunnel = _tunnelManager.Snapshot(),
            packageWatcher = _packageWatcher.Snapshot(),
            lastPackageResult = runtimeState.LastPackageResult,
            lastPublishResult = runtimeState.LastPublishResult,
            lastPackageScanAtUtc = runtimeState.LastPackageScanAtUtc,
            lastPackageScanReason = runtimeState.LastPackageScanReason,
            configIssues = GetConfigIssues()
        };
    }

    public object GetSettingsResponse()
    {
        return new
        {
            cloudflaredPath = _context.Settings.CloudflaredPath,
            incomingFolder = _context.Settings.IncomingFolder,
            processedFolder = _context.Settings.ProcessedFolder,
            failedFolder = _context.Settings.FailedFolder,
            publishedFolder = _context.Settings.PublishedFolder,
            autoStartTunnel = _context.Settings.AutoStartTunnel,
            logLevel = _context.Settings.LogLevel,
            codexCommand = _context.Settings.CodexCommand,
            workspacePaths = _context.Settings.WorkspacePaths,
            preferredBuildCommand = _context.Settings.PreferredBuildCommand,
            preferredRestartCommand = _context.Settings.PreferredRestartCommand,
            publicHostname = _context.Secrets.PublicHostname,
            localPort = _context.Secrets.LocalPort,
            tokenPresent = !string.IsNullOrWhiteSpace(_context.Secrets.CloudflareTunnelToken) &&
                           !_context.Secrets.CloudflareTunnelToken.Contains("PASTE_TOKEN_HERE", StringComparison.OrdinalIgnoreCase)
        };
    }

    public IReadOnlyList<object> GetAppsResponse()
    {
        return _context.RuntimeStateStore.GetApps()
            .Select(app => (object)new
            {
                app.Id,
                app.Name,
                DisplayName = GetPreferredDisplayName(app),
                ShortName = GetPreferredShortName(app),
                app.Manifest.AppType,
                app.ActiveVersion,
                app.Versions,
                app.InstalledAtUtc,
                IconUrl = BuildIconUrl(app),
                LaunchUrl = $"/apps/{Uri.EscapeDataString(app.Id)}/",
                CanPublish = app.Manifest.Publish is { Command.Length: > 0, OutputPath.Length: > 0 },
                StoreVisible = app.Manifest.Display.StoreVisible,
                ShowInLibrary = app.Manifest.Display.ShowInLibrary,
                RunState = _appProcessManager.Snapshot(app.Id),
                LastPublishedArtifact = app.LastPublishedArtifact
            })
            .ToArray();
    }

    public object GetLogsResponse(string logName, int lines)
    {
        var kind = logName.ToLowerInvariant() switch
        {
            "app" => LogKind.App,
            "tunnel" => LogKind.Tunnel,
            "packages" => LogKind.Packages,
            "ui" => LogKind.Ui,
            "codex" => LogKind.Codex,
            _ => LogKind.App
        };

        return new
        {
            logName = kind.ToString().ToLowerInvariant(),
            lines = _context.Log.ReadTail(kind, Math.Clamp(lines, 10, 1000))
        };
    }

    public bool TryResolveInstalledAppRequest(PathString requestPath, out string? filePath, out string? reason)
    {
        filePath = null;
        reason = null;

        if (!requestPath.StartsWithSegments("/installed", out var remainder))
        {
            return false;
        }

        var trimmed = remainder.Value?.Trim('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            reason = "Missing app id.";
            return true;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var appId = parts[0];
        var app = _context.RuntimeStateStore.GetApp(appId);

        if (app is null)
        {
            reason = $"App '{appId}' is not installed.";
            return true;
        }

        var relativePath = parts.Length > 1
            ? string.Join('/', parts.Skip(1))
            : app.Manifest.Entry;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = app.Manifest.Entry;
        }

        filePath = ResolveInstalledAssetPath(app, relativePath);
        if (filePath is null)
        {
            reason = $"Installed file not found: {relativePath}";
            return true;
        }

        return true;
    }

    public async Task<bool> TryHandleHostedAppRequestAsync(HttpContext context, string appId, string relativePath)
    {
        var app = _context.RuntimeStateStore.GetApp(appId);
        if (app is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync($"App '{appId}' is not installed.");
            return true;
        }

        if (string.Equals(app.Manifest.AppType, AppTypes.Static, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveStaticAppPath(app, relativePath, out var filePath, out var reason))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync(reason ?? "App file not found.");
                return true;
            }

            if (!_contentTypes.TryGetContentType(filePath!, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var html = await File.ReadAllTextAsync(filePath!, context.RequestAborted);
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(InjectHostedAppChrome(html), context.RequestAborted);
                return true;
            }

            context.Response.ContentType = contentType;
            await context.Response.SendFileAsync(filePath!);
            return true;
        }

        try
        {
            await _appProcessManager.EnsureRunningAsync(appId, context.RequestAborted);
            await ProxyToAppAsync(context, app, relativePath);
            return true;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync(ex.Message);
            return true;
        }
    }

    private WebApplication BuildWebApplication()
    {
        var options = new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(MasterAppRuntime).Assembly.FullName!,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        };

        var builder = WebApplication.CreateBuilder(options);
        builder.WebHost.UseUrls(LocalUrl);

        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.WriteIndented = true;
        });

        builder.Services.AddSingleton(this);

        var app = builder.Build();

        app.UseMiddleware<HostedAppMiddleware>();
        app.UseMiddleware<InstalledAppMiddleware>();
        var defaultFiles = new DefaultFilesOptions();
        defaultFiles.DefaultFileNames.Clear();
        defaultFiles.DefaultFileNames.Add("store.html");
        app.UseDefaultFiles(defaultFiles);
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                var fileName = Path.GetFileName(context.File.Name);
                if (fileName is "masterapp-ui.js" or "masterapp-ui.css" or "dashboard.html" or "index.html" or "store.html")
                {
                    context.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    context.Context.Response.Headers["Pragma"] = "no-cache";
                    context.Context.Response.Headers["Expires"] = "0";
                }
            }
        });

        app.MapGet("/healthz", () =>
        {
            _context.Log.Ui("Api", "GET /healthz");
            return Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow });
        });

        app.MapGet("/api/status", () =>
        {
            _context.Log.Ui("Api", "GET /api/status");
            return Results.Json(GetStatusResponse());
        });

        app.MapGet("/api/settings", () =>
        {
            _context.Log.Ui("Api", "GET /api/settings");
            return Results.Json(GetSettingsResponse());
        });

        app.MapGet("/api/apps", () =>
        {
            _context.Log.Ui("Api", "GET /api/apps");
            return Results.Json(GetAppsResponse());
        });

        app.MapPost("/api/apps/{appId}/start", async (string appId) =>
        {
            _context.Log.Ui("Api", $"POST /api/apps/{appId}/start");
            return Results.Json(await StartAppAsync(appId));
        });

        app.MapPost("/api/apps/{appId}/stop", (string appId) =>
        {
            _context.Log.Ui("Api", $"POST /api/apps/{appId}/stop");
            return Results.Json(StopApp(appId));
        });

        app.MapPost("/api/apps/{appId}/publish", (string appId) =>
        {
            _context.Log.Ui("Api", $"POST /api/apps/{appId}/publish");
            return Results.Json(PublishApp(appId));
        });

        app.MapPost("/api/apps/{appId}/delete", (string appId) =>
        {
            _context.Log.Ui("Api", $"POST /api/apps/{appId}/delete");
            return Results.Json(DeleteApp(appId));
        });

        app.MapGet("/api/logs/{name}", (string name, int? lines) =>
        {
            _context.Log.Ui("Api", $"GET /api/logs/{name}?lines={lines}");
            return Results.Json(GetLogsResponse(name, lines ?? 200));
        });

        app.MapGet("/api/codex", () =>
        {
            _context.Log.Ui("Api", "GET /api/codex");
            return Results.Json(GetCodexResponse());
        });

        app.MapGet("/api/codex/events", async (HttpContext context) =>
        {
            _context.Log.Ui("Api", "GET /api/codex/events");
            await WriteCodexEventsAsync(context);
        });

        app.MapPost("/api/codex/messages", async (CodexBrokerService.CodexChatRequest request, HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/codex/messages");
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.BadRequest(new { ok = false, message = "Prompt is required." });
            }

            try
            {
                var run = await StartCodexRunAsync(request, context.RequestAborted);
                return Results.Json(new { ok = true, run });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapPost("/api/codex/model", async (CodexBrokerService.CodexModelRequest request, HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/codex/model");
            try
            {
                await SetCodexModelAsync(request, context.RequestAborted);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapPost("/api/codex/stop", async (CodexBrokerService.CodexStopRequest request, HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/codex/stop");
            try
            {
                var run = await StopCodexRunAsync(request, context.RequestAborted);
                return Results.Json(new { ok = true, run });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapPost("/api/codex/session/new", async (CodexBrokerService.CodexNewSessionRequest request, HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/codex/session/new");
            try
            {
                await ClearCodexSessionAsync(request, context.RequestAborted);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapPost("/api/codex/approval", async (CodexBrokerService.CodexApprovalDecisionRequest request, HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/codex/approval");
            try
            {
                var run = await ResolveCodexApprovalAsync(request, context.RequestAborted);
                return Results.Json(new { ok = true, run });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapGet("/life", () =>
        {
            _context.Log.Ui("Api", "GET /life");
            _lifeJournalFinalizer.RequestBackgroundCheck();
            return Results.Content(ReadLifePage(), "text/html; charset=utf-8");
        });

        app.MapGet("/life/history", () =>
        {
            _context.Log.Ui("Api", "GET /life/history");
            _lifeJournalFinalizer.RequestBackgroundCheck();
            return Results.Content(ReadLifePage(), "text/html; charset=utf-8");
        });

        app.MapGet("/api/life/today", async (HttpContext context) =>
        {
            _context.Log.Ui("Api", "GET /api/life/today");
            _lifeJournalFinalizer.RequestBackgroundCheck();
            return Results.Json(await _lifeJournal.GetTodayAsync(context.RequestAborted));
        });

        app.MapGet("/api/life/day", async (string date, HttpContext context) =>
        {
            _context.Log.Ui("Api", $"GET /api/life/day?date={date}");
            if (!LifeJournalPathSafety.TryParseDate(date, out var parsedDate))
            {
                return Results.BadRequest(new { ok = false, message = "date must be yyyy-MM-dd." });
            }

            return Results.Json(await _lifeJournal.GetDayAsync(parsedDate, context.RequestAborted));
        });

        app.MapPost("/api/life/photo", async (HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/life/photo");
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest(new { ok = false, message = "multipart form data is required." });
            }

            try
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var file = form.Files.GetFile("photo") ?? form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
                if (file is null)
                {
                    return Results.BadRequest(new { ok = false, message = "photo file is required." });
                }

                DateOnly? requestedDate = null;
                var dateText = form.TryGetValue("date", out var dateValues) ? dateValues.FirstOrDefault() : null;
                if (!string.IsNullOrWhiteSpace(dateText))
                {
                    if (!LifeJournalPathSafety.TryParseDate(dateText, out var parsedDate))
                    {
                        return Results.BadRequest(new { ok = false, message = "date must be yyyy-MM-dd." });
                    }

                    requestedDate = parsedDate;
                }

                var day = await _lifeJournal.SavePhotoAsync(file, requestedDate, context.RequestAborted);
                return Results.Json(new { ok = true, day });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapPost("/api/life/event", async (LifeEventRequest request, HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/life/event");
            if (string.IsNullOrWhiteSpace(request.Type))
            {
                return Results.BadRequest(new { ok = false, message = "type is required." });
            }

            try
            {
                var day = await _lifeJournal.SaveEventAsync(request.Type, context.RequestAborted);
                return Results.Json(new { ok = true, day });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapPost("/api/life/analyze", async (HttpContext context) =>
        {
            _context.Log.Ui("Api", "POST /api/life/analyze");
            try
            {
                LifeAnalyzeRequest? request = null;
                if ((context.Request.ContentLength ?? 0) > 0)
                {
                    request = await JsonSerializer.DeserializeAsync<LifeAnalyzeRequest>(
                        context.Request.Body,
                        MasterApp.Storage.JsonOptions.Default,
                        context.RequestAborted);
                }

                var date = DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);
                if (!string.IsNullOrWhiteSpace(request?.Date))
                {
                    if (!LifeJournalPathSafety.TryParseDate(request.Date, out date))
                    {
                        return Results.BadRequest(new { ok = false, message = "date must be yyyy-MM-dd." });
                    }
                }

                var day = await _lifeJournal.AnalyzeDayAsync(date, markFinalized: false, context.RequestAborted);
                return Results.Json(new { ok = true, day });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        app.MapGet("/api/life/history", async (HttpContext context) =>
        {
            _context.Log.Ui("Api", "GET /api/life/history");
            return Results.Json(await _lifeJournal.GetHistoryAsync(context.RequestAborted));
        });

        app.MapGet("/api/life/photo/{date}/{filename}", (string date, string filename) =>
        {
            _context.Log.Ui("Api", $"GET /api/life/photo/{date}/{filename}");
            if (!_lifeJournal.TryResolvePhotoPath(date, filename, out var path))
            {
                return Results.NotFound();
            }

            return Results.File(path, "image/jpeg");
        });

        app.MapGet("/api/phone-qr.svg", () =>
        {
            _context.Log.Ui("Api", "GET /api/phone-qr.svg");
            var svg = QrCodeHelper.GenerateSvg(PublicUrl);
            return Results.Content(svg, "image/svg+xml");
        });

        app.MapPost("/api/tunnel/start", () =>
        {
            _context.Log.Ui("Api", "POST /api/tunnel/start");
            return Results.Json(StartTunnel());
        });

        app.MapPost("/api/tunnel/stop", () =>
        {
            _context.Log.Ui("Api", "POST /api/tunnel/stop");
            return Results.Json(StopTunnel());
        });

        app.MapPost("/api/tunnel/restart", () =>
        {
            _context.Log.Ui("Api", "POST /api/tunnel/restart");
            return Results.Json(RestartTunnel());
        });

        app.MapPost("/api/packages/rescan", async () =>
        {
            _context.Log.Ui("Api", "POST /api/packages/rescan");
            return Results.Json(await RescanPackagesAsync());
        });

        return app;
    }

    private static string ReadLifePage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "life.html");
        return File.Exists(path)
            ? File.ReadAllText(path)
            : "<!doctype html><html><body><h1>LifeJournal</h1><p>life.html was not found.</p></body></html>";
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (var header in source.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!destination.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && destination.Content is not null)
            {
                destination.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    public async Task WriteCodexEventsAsync(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        using var subscription = _codexService.Subscribe();

        try
        {
            while (await subscription.Reader.WaitToReadAsync(context.RequestAborted))
            {
                while (subscription.Reader.TryRead(out var ev))
                {
                    var json = JsonSerializer.Serialize(ev, MasterApp.Storage.JsonOptions.Default);
                    await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
    }

    public Task<RelaunchStatusRecord> ScheduleSelfRelaunchAsync(string operationId, string workspacePath, string reason)
    {
        try
        {
            var backupDirectory = BackupImportantState(operationId, workspacePath);
            var command = GetRestartCommand(workspacePath);
            var record = new RelaunchStatusRecord
            {
                Status = "scheduled",
                Message = reason,
                BackupDirectory = backupDirectory,
                Command = command,
                OperationId = operationId,
                RequestedAtUtc = DateTimeOffset.UtcNow
            };

            _context.RuntimeStateStore.SetLastRelaunch(record);
            File.WriteAllText(_context.Paths.RelaunchStateFile, JsonSerializer.Serialize(record, MasterApp.Storage.JsonOptions.DefaultIndented));

            var scriptPath = CreateRelaunchScript(record, workspacePath);
            StartRelaunchHelper(scriptPath);

            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                ExitForRelaunch();
            });

            _context.Log.Codex("Runtime", $"Scheduled self relaunch with helper script: {scriptPath}");
            return Task.FromResult(record);
        }
        catch (Exception ex)
        {
            var failed = new RelaunchStatusRecord
            {
                Status = "failed",
                Message = ex.Message,
                OperationId = operationId,
                RequestedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

            _context.RuntimeStateStore.SetLastRelaunch(failed);
            _context.Log.Codex("Runtime", "Failed to schedule self relaunch.", ex);
            return Task.FromResult(failed);
        }
    }

    private async Task ProxyToAppAsync(HttpContext context, InstalledAppState app, string relativePath)
    {
        var refreshedApp = _context.RuntimeStateStore.GetApp(app.Id) ?? app;
        var baseUrl = _appProcessManager.GetTargetBaseUrl(refreshedApp).TrimEnd('/');
        var targetPath = string.IsNullOrWhiteSpace(relativePath) ? "/" : "/" + relativePath;
        var targetUri = new Uri(baseUrl + targetPath + context.Request.QueryString);
        var appPrefix = $"/apps/{Uri.EscapeDataString(app.Id)}";

        using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        CopyRequestHeaders(context.Request, requestMessage);

        using var responseMessage = await _proxyClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        context.Response.StatusCode = (int)responseMessage.StatusCode;
        foreach (var header in responseMessage.Headers)
        {
            if (string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = new StringValues(header.Value.Select(value => RewriteLocationHeader(value, appPrefix)).ToArray());
                continue;
            }

            context.Response.Headers[header.Key] = new StringValues(header.Value.ToArray());
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            context.Response.Headers[header.Key] = new StringValues(header.Value.ToArray());
        }

        context.Response.Headers.Remove("transfer-encoding");
        if (ShouldRewriteProxyResponse(responseMessage.Content.Headers.ContentType?.MediaType))
        {
            var original = await responseMessage.Content.ReadAsStringAsync(context.RequestAborted);
            var rewritten = RewriteHostedResponseBody(original, appPrefix, responseMessage.Content.Headers.ContentType?.MediaType);
            var encoding = GetResponseEncoding(responseMessage.Content.Headers.ContentType?.CharSet);
            var bytes = encoding.GetBytes(rewritten);
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            return;
        }

        await responseMessage.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool ShouldRewriteProxyResponse(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("text/css", StringComparison.OrdinalIgnoreCase);
    }

    private static string RewriteHostedResponseBody(string content, string appPrefix, string? mediaType)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var rewritten = content
            .Replace("href=\"/", $"href=\"{appPrefix}/", StringComparison.Ordinal)
            .Replace("src=\"/", $"src=\"{appPrefix}/", StringComparison.Ordinal)
            .Replace("action=\"/", $"action=\"{appPrefix}/", StringComparison.Ordinal)
            .Replace("content=\"/", $"content=\"{appPrefix}/", StringComparison.Ordinal)
            .Replace("url(/", $"url({appPrefix}/", StringComparison.Ordinal)
            .Replace("url('/", $"url('{appPrefix}/", StringComparison.Ordinal)
            .Replace("url(\"/", $"url(\"{appPrefix}/", StringComparison.Ordinal)
            .Replace("fetch('/", $"fetch('{appPrefix}/", StringComparison.Ordinal)
            .Replace("fetch(\"/", $"fetch(\"{appPrefix}/", StringComparison.Ordinal)
            .Replace("`/api/", $"`{appPrefix}/api/", StringComparison.Ordinal)
            .Replace("'/api/", $"'{appPrefix}/api/", StringComparison.Ordinal)
            .Replace("\"/api/", $"\"{appPrefix}/api/", StringComparison.Ordinal)
            .Replace("`/media/", $"`{appPrefix}/media/", StringComparison.Ordinal)
            .Replace("'/media/", $"'{appPrefix}/media/", StringComparison.Ordinal)
            .Replace("\"/media/", $"\"{appPrefix}/media/", StringComparison.Ordinal);

        if (mediaType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            rewritten = InjectHostedAppChrome(rewritten);
        }

        return rewritten;
    }

    private static string InjectHostedAppChrome(string html)
    {
        if (string.IsNullOrWhiteSpace(html) ||
            html.Contains("masterapp-hosted-backtab", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        const string chrome = """
<style>
.masterapp-hosted-backtab{position:fixed;left:0;top:50%;transform:translate(-72px,-50%);z-index:2147483647;display:flex;align-items:center;gap:10px;padding:10px 14px 10px 18px;border:none;border-radius:0 18px 18px 0;background:linear-gradient(180deg,rgba(9,14,28,.96),rgba(14,22,44,.98));box-shadow:0 18px 36px rgba(0,0,0,.32);color:#f4f7ff;font:600 14px/1.1 "Segoe UI Variable Text","Segoe UI",sans-serif;letter-spacing:.01em;cursor:pointer;opacity:.92;transition:transform .18s ease,opacity .18s ease;}
.masterapp-hosted-backtab:hover,.masterapp-hosted-backtab:focus-visible,.masterapp-hosted-backtab.is-open{transform:translate(0,-50%);opacity:1;outline:none;}
.masterapp-hosted-backtab::before{content:"";width:11px;height:11px;border-left:2px solid currentColor;border-bottom:2px solid currentColor;transform:rotate(45deg);margin-left:2px;}
.masterapp-hosted-backtab-label{white-space:nowrap;}
@media (max-width:700px){.masterapp-hosted-backtab{top:auto;bottom:18px;transform:translate(-62px,0);border-radius:18px;left:12px;padding:10px 14px;}.masterapp-hosted-backtab:hover,.masterapp-hosted-backtab:focus-visible,.masterapp-hosted-backtab.is-open{transform:translate(0,0);}}
</style>
<button type="button" class="masterapp-hosted-backtab" aria-label="Back to MasterApp" title="Back to MasterApp">
  <span class="masterapp-hosted-backtab-label">MasterApp</span>
</button>
<script>
(()=>{const button=document.querySelector('.masterapp-hosted-backtab');if(!button){return;}let armed=false;const disarm=()=>{armed=false;button.classList.remove('is-open');};button.addEventListener('click',()=>{if(!armed){armed=true;button.classList.add('is-open');window.setTimeout(disarm,2200);return;}window.location.href='/store.html';});button.addEventListener('blur',()=>window.setTimeout(disarm,120));document.addEventListener('keydown',event=>{if(event.key==='Escape'){disarm();}});})();
</script>
""";

        if (html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
        {
            return html.Replace("</body>", chrome + "\n</body>", StringComparison.OrdinalIgnoreCase);
        }

        if (html.Contains("</html>", StringComparison.OrdinalIgnoreCase))
        {
            return html.Replace("</html>", chrome + "\n</html>", StringComparison.OrdinalIgnoreCase);
        }

        return html + chrome;
    }

    private static string RewriteLocationHeader(string value, string appPrefix)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith(appPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return $"{appPrefix}{value}";
    }

    private static Encoding GetResponseEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private bool TryResolveStaticAppPath(InstalledAppState app, string relativePath, out string? filePath, out string? reason)
    {
        filePath = null;
        reason = null;

        var requestedPath = string.IsNullOrWhiteSpace(relativePath) ? app.Manifest.Entry : relativePath;
        var appRoot = Path.Combine(_context.Paths.AppsDirectory, app.Id, app.ActiveVersion, "wwwroot");
        var fullRoot = Path.GetFullPath(appRoot);
        var candidate = Path.GetFullPath(Path.Combine(appRoot, requestedPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Blocked invalid app path.";
            return false;
        }

        if (Directory.Exists(candidate))
        {
            candidate = Path.Combine(candidate, "index.html");
        }

        if (!File.Exists(candidate))
        {
            reason = $"Installed file not found: {requestedPath}";
            return false;
        }

        filePath = candidate;
        return true;
    }

    private string? ResolveInstalledAssetPath(InstalledAppState app, string relativePath)
    {
        var installRoot = Path.Combine(_context.Paths.AppsDirectory, app.Id, app.ActiveVersion);

        foreach (var root in GetInstalledContentRoots(app, installRoot))
        {
            var fullRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(candidate))
            {
                candidate = Path.Combine(candidate, "index.html");
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> GetInstalledContentRoots(InstalledAppState app, string installRoot)
    {
        yield return installRoot;

        if (string.Equals(app.Manifest.AppType, AppTypes.Static, StringComparison.OrdinalIgnoreCase))
        {
            var webRoot = Path.Combine(installRoot, "wwwroot");
            if (Directory.Exists(webRoot))
            {
                yield return webRoot;
            }
        }
    }

    private string? TryResolveAutoIconPath(InstalledAppState app)
    {
        var installRoot = Path.Combine(_context.Paths.AppsDirectory, app.Id, app.ActiveVersion);
        var candidateFileNames = new[]
        {
            "icon.png",
            "icon.svg",
            "icon.ico",
            "favicon.ico",
            "logo.png",
            "logo.svg",
            "apple-touch-icon.png"
        };
        var candidateDirectories = new[]
        {
            installRoot,
            Path.Combine(installRoot, "assets"),
            Path.Combine(installRoot, "icons"),
            Path.Combine(installRoot, "images"),
            Path.Combine(installRoot, "public"),
            Path.Combine(installRoot, "wwwroot"),
            Path.Combine(installRoot, "wwwroot", "assets"),
            Path.Combine(installRoot, "wwwroot", "icons"),
            Path.Combine(installRoot, "wwwroot", "images")
        };

        foreach (var root in candidateDirectories
                     .Where(Directory.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var fileName in candidateFileNames)
            {
                var candidate = Path.Combine(root, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetRelativePath(installRoot, candidate).Replace('\\', '/');
                }
            }
        }

        return null;
    }

    private string? BuildIconUrl(InstalledAppState app)
    {
        if (!string.IsNullOrWhiteSpace(app.Manifest.Icon) && ResolveInstalledAssetPath(app, app.Manifest.Icon) is not null)
        {
            return $"/installed/{Uri.EscapeDataString(app.Id)}/{app.Manifest.Icon.Replace('\\', '/')}";
        }

        var autoIconPath = TryResolveAutoIconPath(app);
        if (string.IsNullOrWhiteSpace(autoIconPath))
        {
            return null;
        }

        return $"/installed/{Uri.EscapeDataString(app.Id)}/{autoIconPath}";
    }

    private static string GetPreferredDisplayName(InstalledAppState app)
    {
        return FirstNonEmpty(
                   app.Manifest.Display.ShortName,
                   app.Manifest.Pwa?.Name,
                   app.Manifest.Pwa?.ShortName,
                   app.Manifest.Name,
                   app.Name,
                   app.Id)
               ?? app.Id;
    }

    private static string GetPreferredShortName(InstalledAppState app)
    {
        return FirstNonEmpty(
                   app.Manifest.Display.ShortName,
                   app.Manifest.Pwa?.ShortName,
                   app.Manifest.Pwa?.Name,
                   app.Manifest.Name,
                   app.Name,
                   app.Id)
               ?? app.Id;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private IReadOnlyList<string> GetConfigIssues()
    {
        var issues = new List<string>(_context.ValidationIssues);

        return issues;
    }

    private string BackupImportantState(string operationId, string workspacePath)
    {
        var backupDirectory = Path.Combine(
            _context.Paths.BackupsDirectory,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{operationId[..Math.Min(8, operationId.Length)]}");
        Directory.CreateDirectory(backupDirectory);

        CopyFileIfPresent(_context.Paths.SettingsFile, Path.Combine(backupDirectory, "settings.json"));
        CopyFileIfPresent(_context.Paths.SecretsFile, Path.Combine(backupDirectory, "secrets.json"));
        CopyFileIfPresent(_context.Paths.RuntimeStateFile, Path.Combine(backupDirectory, "runtime-state.json"));

        var environmentFile = Path.Combine(workspacePath, ".codex", "environments", "environment.toml");
        if (File.Exists(environmentFile))
        {
            var environmentBackup = Path.Combine(backupDirectory, "workspace", ".codex", "environments", "environment.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(environmentBackup)!);
            File.Copy(environmentFile, environmentBackup, overwrite: true);
        }

        CleanupOldBackups();
        return backupDirectory;
    }

    private void CleanupOldBackups()
    {
        var retention = Math.Max(1, _context.Settings.ConfigBackupRetentionCount);
        var directories = new DirectoryInfo(_context.Paths.BackupsDirectory)
            .GetDirectories()
            .OrderByDescending(directory => directory.CreationTimeUtc)
            .ToArray();

        foreach (var directory in directories.Skip(retention))
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception ex)
            {
                _context.Log.Codex("Runtime", $"Backup cleanup warning for {directory.FullName}: {ex.Message}", ex);
            }
        }
    }

    public string GetRestartCommand(string workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(_context.Settings.PreferredRestartCommand))
        {
            return _context.Settings.PreferredRestartCommand;
        }

        var script = Path.Combine(workspacePath, "scripts", "run-masterapp.bat");
        if (File.Exists(script))
        {
            return ".\\scripts\\run-masterapp.bat";
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Could not determine a restart command for MasterApp.");
        }

        return $"\"{executable}\"";
    }

    private string CreateRelaunchScript(RelaunchStatusRecord record, string workspacePath)
    {
        var scriptPath = Path.Combine(_context.Paths.TempDirectory, $"masterapp-relaunch-{record.OperationId ?? Guid.NewGuid().ToString("N")}.ps1");
        var markerPath = _context.Paths.RelaunchStateFile.Replace("'", "''");
        var workingDirectory = workspacePath.Replace("'", "''");
        var command = (record.Command ?? string.Empty).Replace("'", "''");
        var backupDirectory = (record.BackupDirectory ?? string.Empty).Replace("'", "''");
        var operationId = (record.OperationId ?? string.Empty).Replace("'", "''");

        var script = $$"""
param()
$ErrorActionPreference = 'Continue'
$pidToWait = {{Process.GetCurrentProcess().Id}}
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
  $existing = Get-Process -Id $pidToWait -ErrorAction SilentlyContinue
  if (-not $existing) { break }
  Start-Sleep -Milliseconds 750
}
if (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {
  Stop-Process -Id $pidToWait -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
}
$launching = @{
  status = 'launching'
  message = 'Relaunch helper is starting MasterApp.'
  backupDirectory = '{{backupDirectory}}'
  command = '{{command}}'
  operationId = '{{operationId}}'
  requestedAtUtc = '{{record.RequestedAtUtc:O}}'
}
$launching | ConvertTo-Json -Compress | Set-Content -LiteralPath '{{markerPath}}' -Encoding UTF8
Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', '{{command}}' -WorkingDirectory '{{workingDirectory}}' -WindowStyle Hidden
$launched = @{
  status = 'launched'
  message = 'MasterApp relaunch command started.'
  backupDirectory = '{{backupDirectory}}'
  command = '{{command}}'
  operationId = '{{operationId}}'
  requestedAtUtc = '{{record.RequestedAtUtc:O}}'
  completedAtUtc = '{{DateTimeOffset.UtcNow:O}}'
}
$launched | ConvertTo-Json -Compress | Set-Content -LiteralPath '{{markerPath}}' -Encoding UTF8
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
""";

        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        return scriptPath;
    }

    private static void StartRelaunchHelper(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        Process.Start(startInfo);
    }

    private void ExitForRelaunch()
    {
        try
        {
            Dispose();
        }
        catch (Exception ex)
        {
            _context.Log.Codex("Runtime", "Dispose during relaunch failed.", ex);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static void CopyFileIfPresent(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private sealed class LifeEventRequest
    {
        public string Type { get; set; } = string.Empty;
    }
}
