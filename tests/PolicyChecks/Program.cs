using MasterApp.Hosting;
using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Packages;
using MasterApp.Utilities;
using MasterApp.LifeJournal;
using MasterApp.Diagnostics;
using MasterApp.Storage;

var failures = new List<string>();

AssertTrue(
    CodexWorkspacePolicy.CalculateDecisionStepBudget(
        provider: "codex",
        taskMode: "investigate",
        workspaceKind: "workspace",
        prompt: "icons disappeared from all my buttons, inspect masterapp and explain why",
        codexBaseBudget: 20,
        ollamaBaseBudget: 36,
        isMasterAppWorkspace: true,
        hasExplicitWorkspaceHints: true) > 20,
    "MasterApp investigate budget should grow above the base Codex budget.",
    failures);

AssertTrue(
    CodexWorkspacePolicy.CalculateDecisionStepBudget(
        provider: "ollama",
        taskMode: "code",
        workspaceKind: "installed-app",
        prompt: "fix the broken app and repackage it",
        codexBaseBudget: 20,
        ollamaBaseBudget: 36,
        isMasterAppWorkspace: false,
        hasExplicitWorkspaceHints: false) > 36,
    "Installed-app code budget should grow above the base Ollama budget.",
    failures);

var context = CodexWorkspacePolicy.CreatePromptContext(
    workspacePath: @"C:\Dev\MasterApp\sample-package",
    workspaceKind: "installed-app",
    appId: "hello-app",
    version: "1.0.0",
    manifest: new AppManifest
    {
        Id = "hello-app",
        Version = "1.0.0",
        AppType = AppTypes.Static,
        Entry = "index.html",
        Launch = new AppLaunchManifest
        {
            Kind = LaunchKinds.Static
        }
    });

AssertTrue(
    context.HasExplicitHints,
    "Sample package prompt context should load explicit workspace hints.",
    failures);

AssertTrue(
    context.PromptLines.Any(line => line.Contains("Preferred entry point", StringComparison.OrdinalIgnoreCase)),
    "Prompt context should surface preferred entry points from masterapp.ai.json.",
    failures);

AssertTrue(
    context.PromptLines.Any(line => line.Contains("Manifest appType: static", StringComparison.OrdinalIgnoreCase)),
    "Prompt context should include manifest-derived package information.",
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

var sessionLogPath = Path.Combine(Path.GetTempPath(), $"masterapp-session-{Environment.ProcessId}.jsonl");
File.WriteAllLines(sessionLogPath, new[]
{
    """{"type":"session_meta","payload":{"cwd":"C:\\Dev\\MasterApp"}}""",
    """{"type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"ignore me"}]}}""",
    """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"# AGENTS.md instructions for C:\\Dev\\MasterApp\n\n<INSTRUCTIONS>\nignore me\n</INSTRUCTIONS>"}]}}""",
    """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"You are running inside the MasterApp Codex panel.\nImportant host contract:\n- Do not stop MasterApp.\n\nUser request:\nshow my chat"}]}}""",
    """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"chat is visible"}]}}"""
});

var parsedChat = CodexSessionLogReader.ReadRecentChat(
    sessionPath: sessionLogPath,
    id: "session-test",
    title: "Session test",
    updatedAtUtc: now);

AssertTrue(
    parsedChat is not null &&
    parsedChat.Cwd == @"C:\Dev\MasterApp" &&
    parsedChat.Messages.Count == 2 &&
    parsedChat.UserPreview == "show my chat" &&
    parsedChat.AssistantPreview.Contains("chat is visible", StringComparison.OrdinalIgnoreCase),
    "Codex Desktop session parser should read input_text/output_text transcript items and ignore non-chat roles.",
    failures);

File.Delete(sessionLogPath);

var externalWorkspace = Path.GetFullPath(@"C:\Dev\OtherProject");
var workspaceAccess = CodexWorkspacePolicy.EvaluateApprovalWorkspaceAccess(
    externalWorkspace,
    new[] { Path.GetFullPath(@"C:\Dev\MasterApp") });

AssertTrue(
    !workspaceAccess.IsAllowed &&
    workspaceAccess.RequiresApproval &&
    workspaceAccess.CanTrustWorkspace &&
    workspaceAccess.TrustWorkspacePath == externalWorkspace,
    "External command working directories should become explicit one-time/trust approvals instead of hard failures.",
    failures);

AssertTrue(
    WatchdogPolicy.WasExplicitQuit(new ShutdownIntentRecord { Reason = "quit" }),
    "Explicit quit markers should suppress watchdog relaunch.",
    failures);

AssertTrue(
    !WatchdogPolicy.WasExplicitQuit(new ShutdownIntentRecord { Reason = "crash" }),
    "Only quit markers should suppress watchdog relaunch.",
    failures);

var fakeUserProfile = Path.Combine(Path.GetTempPath(), $"MasterApp-CodexResolver-{Environment.ProcessId}-{Guid.NewGuid():N}");
var fakeLocalCacheCodex = Path.Combine(
    fakeUserProfile,
    "AppData",
    "Local",
    "Packages",
    "OpenAI.Codex_2p2nqsd0c76g0",
    "LocalCache",
    "Local",
    "OpenAI",
    "Codex",
    "bin",
    "codex.exe");
Directory.CreateDirectory(Path.GetDirectoryName(fakeLocalCacheCodex)!);
File.WriteAllText(fakeLocalCacheCodex, "fake codex");
var fakeWindowsAppsCodex = Path.Combine(
    fakeUserProfile,
    "AppData",
    "Local",
    "Microsoft",
    "WindowsApps",
    "OpenAI.Codex_2p2nqsd0c76g0",
    "codex.exe");

var codexResolution = CodexExecutableResolver.Resolve(
    "codex",
    fakeUserProfile,
    new[] { fakeWindowsAppsCodex });
AssertTrue(
    codexResolution.IsReady &&
    string.Equals(codexResolution.ResolvedExecutablePath, fakeLocalCacheCodex, StringComparison.OrdinalIgnoreCase) &&
    codexResolution.AttemptedPaths.Contains(fakeWindowsAppsCodex, StringComparer.OrdinalIgnoreCase),
    "Codex executable resolver should skip WindowsApps aliases and prefer the real LocalCache CLI.",
    failures);

var explicitCodexResolution = CodexExecutableResolver.Resolve(
    fakeLocalCacheCodex,
    fakeUserProfile,
    Array.Empty<string>());
AssertTrue(
    explicitCodexResolution.IsReady &&
    string.Equals(explicitCodexResolution.ResolvedExecutablePath, fakeLocalCacheCodex, StringComparison.OrdinalIgnoreCase),
    "Codex executable resolver should preserve an explicit usable executable path.",
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
        deleteResult.Ok && deleteStateStore.GetApp("locked-delete-app") is null,
        "Deleting an app should remove runtime state even when app folder cleanup is blocked, " +
        "so it disappears from Store and Library.",
        failures);
}

if (Directory.Exists(deleteAppRoot))
{
    Directory.Delete(deleteAppRoot, recursive: true);
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

var fallbackAnalyzer = new CodexCliLifeJournalAnalyzer(
    new LifeJournalSettings
    {
        CodexExecutablePath = Path.Combine(lifeRoot, "missing-codex.exe"),
        AnalysisTimeoutSeconds = 1
    },
    lifeLogger,
    new FakeLifeJournalAnalyzer(lifeLogger));
var fallbackAnalysis = await fallbackAnalyzer.AnalyzeAsync(savedLifeDay, Array.Empty<string>(), null, CancellationToken.None);
AssertTrue(
    fallbackAnalysis.UsedFallback &&
    fallbackAnalysis.ShortSummary.Contains("photo diary", StringComparison.OrdinalIgnoreCase),
    "LifeJournal Codex analyzer should fall back to the fake analyzer when the Codex executable cannot run.",
    failures);

var lifeCodexArgs = CodexCliLifeJournalAnalyzer.BuildCodexArguments(
    new[] { Path.Combine(lifeRoot, "photo.jpg") },
    "Summarize this day.");
AssertTrue(
    lifeCodexArgs.Count >= 7 &&
    string.Equals(lifeCodexArgs[0], "exec", StringComparison.OrdinalIgnoreCase) &&
    lifeCodexArgs.Contains("--skip-git-repo-check", StringComparer.OrdinalIgnoreCase) &&
    lifeCodexArgs.Contains("--sandbox", StringComparer.OrdinalIgnoreCase) &&
    lifeCodexArgs.Contains("--image", StringComparer.OrdinalIgnoreCase) &&
    !lifeCodexArgs.Contains("--ask-for-approval", StringComparer.OrdinalIgnoreCase) &&
    lifeCodexArgs[^2] == "--" &&
    lifeCodexArgs[^1] == "Summarize this day.",
    "LifeJournal Codex analyzer should use supported non-interactive codex exec arguments.",
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
        RelaunchStateFile = Path.Combine(root, "State", "relaunch-state.json"),
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
