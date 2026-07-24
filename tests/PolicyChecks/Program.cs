using MasterApp.Hosting;
using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Packages;
using MasterApp.Utilities;
using MasterApp.LifeJournal;
using MasterApp.Diagnostics;
using MasterApp.Storage;
using System.Diagnostics;

var failures = new List<string>();

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
AssertNoForbiddenText(
    repositoryRoot,
    new[]
    {
        "src",
        "scripts",
        "templates",
        "README.md",
        "MASTERAPP_CAPABILITIES_AND_FLOW.md",
        "docs",
        "distributions",
        "masterapp.ai.json",
        "sample-package"
    },
    new[] { "Codex", "Ollama", "Gemma" },
    failures);

var now = new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero);
var watchdogState = new WatchdogStateRecord();

watchdogState = WatchdogPolicy.RegisterLaunchFailure(watchdogState, now);
watchdogState = WatchdogPolicy.RegisterLaunchFailure(watchdogState, now.AddMinutes(10));
watchdogState = WatchdogPolicy.RegisterLaunchFailure(watchdogState, now.AddMinutes(20));

AssertTrue(
    !WatchdogPolicy.CanAttemptRestart(watchdogState, now.AddMinutes(21)),
    "Watchdog should stop restarting after repeated startup failures inside the retry window.",
    failures);

AssertTrue(
    WatchdogPolicy.CanAttemptRestart(watchdogState, now.AddHours(3)),
    "Watchdog should allow retries again after the retry window expires.",
    failures);

var recoveredState = WatchdogPolicy.RegisterLaunchSuccess(watchdogState, now.AddHours(3).AddMinutes(1));
AssertTrue(
    recoveredState.ConsecutiveLaunchFailures == 0 && WatchdogPolicy.CanAttemptRestart(recoveredState, now.AddHours(3).AddMinutes(2)),
    "A successful launch should reset watchdog failure counters.",
    failures);

AssertTrue(
    WatchdogPolicy.WasExplicitQuit(new ShutdownIntentRecord { Reason = "quit" }),
    "Explicit quit markers should suppress watchdog relaunch.",
    failures);

AssertTrue(
    !WatchdogPolicy.WasExplicitQuit(new ShutdownIntentRecord { Reason = "crash" }),
    "Only quit markers should suppress watchdog relaunch.",
    failures);

var singleInstanceName = $"MasterApp-PolicyChecks-{Environment.ProcessId}";
var firstLock = SingleInstanceGate.TryAcquire(singleInstanceName);
SingleInstanceLease? secondLock = null;
var secondLockThread = new Thread(() => secondLock = SingleInstanceGate.TryAcquire(singleInstanceName));
secondLockThread.Start();
secondLockThread.Join();

AssertTrue(
    firstLock.IsAcquired,
    "Single-instance gate should allow the first acquisition.",
    failures);

AssertTrue(
    secondLock is not null && !secondLock.IsAcquired,
    "Single-instance gate should reject a second acquisition for the same name.",
    failures);

firstLock.Dispose();
secondLock?.Dispose();

var reacquiredLock = SingleInstanceGate.TryAcquire(singleInstanceName);
AssertTrue(
    reacquiredLock.IsAcquired,
    "Single-instance gate should allow acquisition again after disposal.",
    failures);
reacquiredLock.Dispose();

var deleteStateRoot = Path.Combine(Path.GetTempPath(), $"MasterApp-DeleteState-{Environment.ProcessId}-{Guid.NewGuid():N}");
Directory.CreateDirectory(deleteStateRoot);
var deletePaths = CreateTestAppPaths(deleteStateRoot);
var deleteLog = new FileLogManager(deletePaths.LogsDirectory);
var deleteStateStore = new RuntimeStateStore(deletePaths.RuntimeStateFile, deleteLog);
var deleteAppRoot = Path.Combine(deletePaths.AppsDirectory, "locked-delete-app");
var deleteInstallRoot = Path.Combine(deleteAppRoot, "1.0.0");
Directory.CreateDirectory(deleteInstallRoot);
var lockedDeleteFilePath = Path.Combine(deleteInstallRoot, "locked.txt");
File.WriteAllText(lockedDeleteFilePath, "locked");
deleteStateStore.UpsertInstalledApp(new InstalledAppState
{
    Id = "locked-delete-app",
    Name = "Locked Delete App",
    ActiveVersion = "1.0.0",
    Versions = new List<string> { "1.0.0" },
    Manifest = new AppManifest
    {
        Id = "locked-delete-app",
        Name = "Locked Delete App",
        Version = "1.0.0",
        AppType = AppTypes.Static,
        Entry = "index.html",
        Launch = new AppLaunchManifest
        {
            Kind = LaunchKinds.Static
        },
        Display = new AppDisplayManifest
        {
            StoreVisible = true,
            ShowInLibrary = true
        }
    },
    RunState = new AppRunState
    {
        Status = "installed",
        IsRunning = false
    }
});

using (var lockedDeleteFile = new FileStream(lockedDeleteFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    using var deleteRuntime = new MasterAppRuntime(new BootstrapContext
    {
        Paths = deletePaths,
        Settings = AppSettings.CreateDefault(),
        Secrets = AppSecrets.CreateDefault(),
        RuntimeStateStore = deleteStateStore,
        Log = deleteLog,
        ValidationIssues = Array.Empty<string>()
    });
    var deleteResult = deleteRuntime.DeleteApp("locked-delete-app");

    AssertTrue(
        deleteResult.Ok &&
        deleteStateStore.GetApp("locked-delete-app") is null &&
        !deleteResult.Message.Contains("Cleanup warning", StringComparison.OrdinalIgnoreCase) &&
        !deleteResult.Message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase),
        "Deleting an app should remove runtime state even when app folder cleanup is blocked, " +
        "so it disappears from Store and Library without showing locked-file cleanup errors.",
        failures);
}

if (Directory.Exists(deleteAppRoot))
{
    Directory.Delete(deleteAppRoot, recursive: true);
}

var staleProcessRoot = Path.Combine(Path.GetTempPath(), $"MasterApp-StaleProcess-{Environment.ProcessId}-{Guid.NewGuid():N}");
Directory.CreateDirectory(staleProcessRoot);
var staleProcessPaths = CreateTestAppPaths(staleProcessRoot);
var staleProcessLog = new FileLogManager(staleProcessPaths.LogsDirectory);
var staleProcessStateStore = new RuntimeStateStore(staleProcessPaths.RuntimeStateFile, staleProcessLog);
var staleInstallRoot = Path.Combine(staleProcessPaths.AppsDirectory, "stale-process-app", "1.0.0");
Directory.CreateDirectory(staleInstallRoot);
var staleExePath = Path.Combine(staleInstallRoot, "stale.exe");
File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), staleExePath);
using var staleProcess = Process.Start(new ProcessStartInfo(staleExePath)
{
    Arguments = "/c ping -n 60 127.0.0.1",
    UseShellExecute = false,
    CreateNoWindow = true
}) ?? throw new InvalidOperationException("Could not start stale-process test helper.");
try
{
    staleProcessStateStore.UpsertInstalledApp(new InstalledAppState
    {
        Id = "stale-process-app",
        Name = "Stale Process App",
        ActiveVersion = "1.0.0",
        Versions = new List<string> { "1.0.0" },
        Manifest = new AppManifest
        {
            Id = "stale-process-app",
            Name = "Stale Process App",
            Version = "1.0.0",
            AppType = AppTypes.Portable,
            Launch = new AppLaunchManifest
            {
                Kind = LaunchKinds.WebApp,
                ExecutablePath = "stale.exe"
            }
        },
        RunState = new AppRunState
        {
            Status = "running",
            IsRunning = true,
            ProcessId = staleProcess.Id,
            Message = "Persisted process from an earlier MasterApp instance."
        }
    });

    var staleStopResult = new AppProcessManager(new BootstrapContext
    {
        Paths = staleProcessPaths,
        Settings = AppSettings.CreateDefault(),
        Secrets = AppSecrets.CreateDefault(),
        RuntimeStateStore = staleProcessStateStore,
        Log = staleProcessLog,
        ValidationIssues = Array.Empty<string>()
    }).Stop("stale-process-app");
    staleProcess.WaitForExit(5000);

    AssertTrue(
        staleStopResult.Ok && staleProcess.HasExited,
        "Stopping an app should terminate a persisted running process even when this MasterApp instance did not start it.",
        failures);

    var shutdownProcessRoot = Path.Combine(Path.GetTempPath(), $"MasterApp-ShutdownProcess-{Environment.ProcessId}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(shutdownProcessRoot);
    var shutdownProcessPaths = CreateTestAppPaths(shutdownProcessRoot);
    var shutdownProcessLog = new FileLogManager(shutdownProcessPaths.LogsDirectory);
    var shutdownProcessStateStore = new RuntimeStateStore(shutdownProcessPaths.RuntimeStateFile, shutdownProcessLog);
    var shutdownInstallRoot = Path.Combine(shutdownProcessPaths.AppsDirectory, "shutdown-process-app", "1.0.0");
    Directory.CreateDirectory(shutdownInstallRoot);
    var shutdownExePath = Path.Combine(shutdownInstallRoot, "shutdown.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), shutdownExePath);
    using var shutdownProcess = Process.Start(new ProcessStartInfo(shutdownExePath)
    {
        Arguments = "/c ping -n 60 127.0.0.1",
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Could not start shutdown-process test helper.");
    try
    {
        shutdownProcessStateStore.UpsertInstalledApp(new InstalledAppState
        {
            Id = "shutdown-process-app",
            Name = "Shutdown Process App",
            ActiveVersion = "1.0.0",
            Versions = new List<string> { "1.0.0" },
            Manifest = new AppManifest
            {
                Id = "shutdown-process-app",
                Name = "Shutdown Process App",
                Version = "1.0.0",
                AppType = AppTypes.Portable,
                Launch = new AppLaunchManifest
                {
                    Kind = LaunchKinds.WebApp,
                    ExecutablePath = "shutdown.exe"
                }
            },
            RunState = new AppRunState
            {
                Status = "running",
                IsRunning = true,
                ProcessId = shutdownProcess.Id,
                Message = "Persisted process from an earlier MasterApp instance."
            }
        });

        using (new AppProcessManager(new BootstrapContext
        {
            Paths = shutdownProcessPaths,
            Settings = AppSettings.CreateDefault(),
            Secrets = AppSecrets.CreateDefault(),
            RuntimeStateStore = shutdownProcessStateStore,
            Log = shutdownProcessLog,
            ValidationIssues = Array.Empty<string>()
        }))
        {
        }

        shutdownProcess.WaitForExit(5000);
        AssertTrue(
            shutdownProcess.HasExited,
            "Disposing AppProcessManager should stop persisted running app processes from earlier MasterApp instances.",
            failures);
    }
    finally
    {
        if (!shutdownProcess.HasExited)
        {
            shutdownProcess.Kill(entireProcessTree: true);
            shutdownProcess.WaitForExit(5000);
        }
    }
}
finally
{
    if (!staleProcess.HasExited)
    {
        staleProcess.Kill(entireProcessTree: true);
        staleProcess.WaitForExit(5000);
    }
}

var orphanDeleteRoot = Path.Combine(Path.GetTempPath(), $"MasterApp-OrphanDelete-{Environment.ProcessId}-{Guid.NewGuid():N}");
Directory.CreateDirectory(orphanDeleteRoot);
var orphanDeletePaths = CreateTestAppPaths(orphanDeleteRoot);
var orphanDeleteLog = new FileLogManager(orphanDeletePaths.LogsDirectory);
var orphanDeleteStateStore = new RuntimeStateStore(orphanDeletePaths.RuntimeStateFile, orphanDeleteLog);
var orphanDeleteAppRoot = Path.Combine(orphanDeletePaths.AppsDirectory, "orphan-delete-app");
var orphanDeleteInstallRoot = Path.Combine(orphanDeleteAppRoot, "1.0.0");
Directory.CreateDirectory(orphanDeleteInstallRoot);
var orphanDeleteExePath = Path.Combine(orphanDeleteInstallRoot, "orphan.exe");
File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), orphanDeleteExePath);
using var orphanDeleteProcess = Process.Start(new ProcessStartInfo(orphanDeleteExePath)
{
    Arguments = "/c ping -n 60 127.0.0.1",
    UseShellExecute = false,
    CreateNoWindow = true
}) ?? throw new InvalidOperationException("Could not start orphan-delete test helper.");
try
{
    orphanDeleteStateStore.UpsertInstalledApp(new InstalledAppState
    {
        Id = "orphan-delete-app",
        Name = "Orphan Delete App",
        ActiveVersion = "1.0.0",
        Versions = new List<string> { "1.0.0" },
        Manifest = new AppManifest
        {
            Id = "orphan-delete-app",
            Name = "Orphan Delete App",
            Version = "1.0.0",
            AppType = AppTypes.Portable,
            Launch = new AppLaunchManifest
            {
                Kind = LaunchKinds.WebApp,
                ExecutablePath = "orphan.exe"
            }
        },
        RunState = new AppRunState
        {
            Status = "stopped",
            IsRunning = false,
            Message = "Runtime state is stale, but the app process still exists."
        }
    });

    using var orphanDeleteRuntime = new MasterAppRuntime(new BootstrapContext
    {
        Paths = orphanDeletePaths,
        Settings = AppSettings.CreateDefault(),
        Secrets = AppSecrets.CreateDefault(),
        RuntimeStateStore = orphanDeleteStateStore,
        Log = orphanDeleteLog,
        ValidationIssues = Array.Empty<string>()
    });
    var orphanDeleteResult = orphanDeleteRuntime.DeleteApp("orphan-delete-app");
    orphanDeleteProcess.WaitForExit(5000);

    AssertTrue(
        orphanDeleteResult.Ok &&
        orphanDeleteStateStore.GetApp("orphan-delete-app") is null &&
        orphanDeleteProcess.HasExited &&
        !Directory.Exists(orphanDeleteAppRoot),
        "Deleting an app should stop orphaned app processes from the install folder and remove the app files.",
        failures);
}
finally
{
    if (!orphanDeleteProcess.HasExited)
    {
        orphanDeleteProcess.Kill(entireProcessTree: true);
        orphanDeleteProcess.WaitForExit(5000);
    }

    if (Directory.Exists(orphanDeleteAppRoot))
    {
        Directory.Delete(orphanDeleteAppRoot, recursive: true);
    }
}

var lifeRoot = Path.Combine(Path.GetTempPath(), $"MasterApp-LifeJournal-PolicyChecks-{Environment.ProcessId}-{Guid.NewGuid():N}");
Directory.CreateDirectory(lifeRoot);
var lifeLogger = new LifeJournalLogger(lifeRoot);
var lifeStore = new LifeJournalStore(lifeRoot, lifeLogger);
var lifeDate = new DateOnly(2026, 5, 7);
var lifeTimestamp = new DateTimeOffset(2026, 5, 7, 10, 15, 0, TimeSpan.FromHours(3));
var lifePhoto = new LifePhoto
{
    Id = "photo-1",
    TimestampUtc = lifeTimestamp.UtcDateTime,
    LocalTimestamp = lifeTimestamp,
    FileName = "10-15-00-photo.jpg",
    RelativePath = "photos/2026-05-07/10-15-00-photo.jpg",
    MimeType = "image/jpeg",
    SizeBytes = 12345,
    Width = 900,
    Height = 600
};

await lifeStore.AddPhotoAsync(lifeDate, lifePhoto, CancellationToken.None);
var savedLifeDay = await lifeStore.LoadDayAsync(lifeDate, CancellationToken.None);
AssertTrue(
    savedLifeDay.Photos.Count == 1 &&
    savedLifeDay.Photos[0].FileName == "10-15-00-photo.jpg" &&
    savedLifeDay.Photos[0].Width == 900,
    "LifeJournal store should persist photo metadata in the requested day file.",
    failures);

await lifeStore.AddEventAsync(lifeDate, "woke_up", lifeTimestamp.AddMinutes(5), CancellationToken.None);
savedLifeDay = await lifeStore.LoadDayAsync(lifeDate, CancellationToken.None);
AssertTrue(
    savedLifeDay.Events.Count == 1 &&
    savedLifeDay.Events[0].Type == "woke_up",
    "LifeJournal store should persist day events with timestamps.",
    failures);

var loadedToday = await lifeStore.LoadTodayAsync(lifeTimestamp, CancellationToken.None);
AssertTrue(
    loadedToday.Date == "2026-05-07",
    "LifeJournal today loading should use the local server date.",
    failures);

AssertTrue(
    LifeJournalPathSafety.TryResolvePhotoPath(Path.Combine(lifeRoot, "photos"), "2026-05-07", "10-15-00-photo.jpg", out _),
    "LifeJournal photo path resolver should accept generated file names under the date directory.",
    failures);

AssertTrue(
    !LifeJournalPathSafety.TryResolvePhotoPath(Path.Combine(lifeRoot, "photos"), "2026-05-07", "..\\secret.jpg", out _) &&
    !LifeJournalPathSafety.TryResolvePhotoPath(Path.Combine(lifeRoot, "photos"), "..\\2026-05-07", "secret.jpg", out _),
    "LifeJournal photo path resolver should reject path traversal attempts.",
    failures);

var metadataAnalyzer = new MetadataLifeJournalAnalyzer(lifeLogger);
var fallbackAnalysis = await metadataAnalyzer.AnalyzeAsync(savedLifeDay, Array.Empty<string>(), null, CancellationToken.None);
AssertTrue(
    fallbackAnalysis.UsedFallback &&
    fallbackAnalysis.Uncertainties.Any(item => item.Contains("metadata", StringComparison.OrdinalIgnoreCase)),
    "LifeJournal should keep working with a metadata-only analyzer after AI integration removal.",
    failures);

var finalizerRoot = Path.Combine(Path.GetTempPath(), $"MasterApp-LifeJournal-Finalizer-{Environment.ProcessId}-{Guid.NewGuid():N}");
Directory.CreateDirectory(finalizerRoot);
var finalizerLogger = new LifeJournalLogger(finalizerRoot);
var finalizerStore = new LifeJournalStore(finalizerRoot, finalizerLogger);
await finalizerStore.AddPhotoAsync(lifeDate, lifePhoto, CancellationToken.None);
var countingAnalyzer = new CountingLifeJournalAnalyzer();
var finalizer = new LifeJournalFinalizer(
    finalizerStore,
    countingAnalyzer,
    finalizerLogger,
    new LifeJournalSettings { AutoFinalizeHourLocal = 3 },
    Path.Combine(finalizerRoot, "finalizer-state.json"),
    () => new DateTimeOffset(2026, 5, 8, 3, 30, 0, TimeSpan.FromHours(3)));

await finalizer.RunOnceAsync(CancellationToken.None);
await finalizer.RunOnceAsync(CancellationToken.None);
var finalizedDay = await finalizerStore.LoadDayAsync(lifeDate, CancellationToken.None);
AssertTrue(
    countingAnalyzer.Count == 1 &&
    finalizedDay.FinalizedAtUtc is not null &&
    !string.IsNullOrWhiteSpace(finalizedDay.AnalysisMarkdown),
    "LifeJournal finalizer should analyze yesterday once and avoid duplicate analysis.",
    failures);

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine("Policy checks passed.");
return 0;

static AppPaths CreateTestAppPaths(string root)
{
    return new AppPaths
    {
        RootDirectory = root,
        StateDirectory = Path.Combine(root, "State"),
        BackupsDirectory = Path.Combine(root, "State", "Backups"),
        LogsDirectory = Path.Combine(root, "Logs"),
        TempDirectory = Path.Combine(root, "Temp"),
        AppsDirectory = Path.Combine(root, "Apps"),
        AppSpecsDirectory = Path.Combine(root, "AppSpecs"),
        SettingsFile = Path.Combine(root, "State", "settings.json"),
        SecretsFile = Path.Combine(root, "State", "secrets.json"),
        RuntimeStateFile = Path.Combine(root, "State", "runtime-state.json"),
        ShutdownIntentFile = Path.Combine(root, "State", "shutdown-intent.json"),
        WatchdogStateFile = Path.Combine(root, "State", "watchdog-state.json")
    };
}

static void AssertTrue(bool condition, string message, List<string> failures)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

static void AssertNoForbiddenText(string root, IReadOnlyList<string> relativePaths, IReadOnlyList<string> forbiddenTerms, List<string> failures)
{
    foreach (var relativePath in relativePaths)
    {
        var path = Path.Combine(root, relativePath);
        if (File.Exists(path))
        {
            AssertFileDoesNotContain(path, forbiddenTerms, failures);
            continue;
        }

        if (!Directory.Exists(path))
        {
            continue;
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("docs", "superpowers"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AssertFileDoesNotContain(file, forbiddenTerms, failures);
        }
    }
}

static void AssertFileDoesNotContain(string path, IReadOnlyList<string> forbiddenTerms, List<string> failures)
{
    var text = File.ReadAllText(path);
    foreach (var term in forbiddenTerms)
    {
        AssertTrue(
            !text.Contains(term, StringComparison.OrdinalIgnoreCase),
            $"Forbidden cleanup term '{term}' remains in {path}.",
            failures);
    }
}

sealed class CountingLifeJournalAnalyzer : ILifeJournalAnalyzer
{
    public int Count { get; private set; }

    public Task<LifeAnalysisOutput> AnalyzeAsync(
        LifeDay day,
        IReadOnlyList<string> selectedPhotoPaths,
        string? priorContext,
        CancellationToken cancellationToken)
    {
        Count++;
        return Task.FromResult(new LifeAnalysisOutput
        {
            Date = day.Date,
            ShortSummary = "A counted test day.",
            Markdown = $"# Day Summary - {day.Date}\n\nA counted test day.",
            Json = """{"shortSummary":"A counted test day."}""",
            RawOutput = "counted",
            Tags = new List<string> { "test" }
        });
    }
}
