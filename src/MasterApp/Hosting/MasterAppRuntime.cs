using MasterApp.Bootstrap;
using MasterApp.Diagnostics;
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
    private readonly CodexBrokerService _codexService;
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
        _appProcessManager = new AppProcessManager(_context);
        _tunnelManager = new TunnelManager(_context);
        _codexService = new CodexBrokerService(_context, this);
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

            _appProcessManager.Stop(appId);

            var appsRoot = Path.GetFullPath(_context.Paths.AppsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var appRoot = Path.GetFullPath(Path.Combine(appsRoot, app.Id))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!appRoot.StartsWith(appsRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("APP_DELETE_PATH_INVALID");
            }

            if (Directory.Exists(appRoot))
            {
                Directory.Delete(appRoot, recursive: true);
            }

            if (!_context.RuntimeStateStore.RemoveApp(appId))
            {
                return OperationResult.Failure($"APP_NOT_FOUND: {appId}");
            }

            _context.Log.Info("Runtime", $"Deleted app '{appId}' from {appRoot}.");
            return OperationResult.Success($"Deleted {GetPreferredDisplayName(app)}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }
    }

    public void OpenDashboard() => ShellHelper.OpenPath(LocalUrl);
    public void OpenPublic() => ShellHelper.OpenPath(PublicUrl);
    public void OpenPhoneQr() => ShellHelper.OpenPath($"{LocalUrl}/qr.html");
    public void OpenLogsFolder() => ShellHelper.OpenPath(_context.Paths.LogsDirectory);

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
        defaultFiles.DefaultFileNames.Add("dashboard.html");
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

        return content
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

}
