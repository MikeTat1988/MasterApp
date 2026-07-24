# Lazy Local MasterApp And Codex Removal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the built-in Codex/Ollama integration from MasterApp completely, then add a lightweight lazy-local distribution that runs on same-Wi-Fi with QR access, dynamic local port fallback, Drive folder setup, and simple user docs.

**Architecture:** Treat Codex removal as a product cleanup, not a feature flag: no Codex tab, no Codex API routes, no Codex backend classes, no Codex runtime state, no Codex settings, no Ollama/Gemma helper scripts, and no LifeJournal dependency on Codex. The lazy-local distribution reuses the existing app install/store platform but adds a separate setup runner under `distributions/lazy-local/` and a lazy runtime mode that binds to the LAN only for same-Wi-Fi QR sessions.

**Tech Stack:** .NET 8 Windows WinForms tray app, ASP.NET Core minimal APIs, static HTML/CSS/JS in `wwwroot`, QRCoder, PowerShell setup runner, batch build wrappers, `tests/PolicyChecks` console checks.

---

## Non-Negotiable Requirements

- Codex integration is removed from both standard and lazy-local flows. Do not keep a `codexEnabled` switch.
- The app still installs, hosts, starts, stops, deletes, and publishes MasterApp packages.
- Existing installed app state under `%LOCALAPPDATA%\MasterApp` is not deleted or migrated destructively.
- Standard flow keeps existing package, tray, store, library, dashboard, logs, publish, hosted-app proxy, and optional Cloudflare tunnel behavior unless explicitly replaced by lazy-local mode.
- Lazy-local flow does not require Cloudflare, public hostnames, secrets, manual JSON editing, or port editing.
- Lazy-local phone access works only from loopback/local machine or same Wi-Fi clients that have scanned the current QR code.
- Lazy-local port selection tries `19057` first, then `19058` through `19077`, stores the actual selected port in runtime state, and always builds QR URLs from the actual selected port.
- The distribution output is a simple folder/zip, not a full MSI installer.
- The setup runner may attempt a Private-network Windows Firewall rule. It must not fail the whole setup if elevation is declined.

## Current Code Map

**Codex backend to remove:**
- Delete: `src/MasterApp/Hosting/CodexBrokerService.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Classification.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Execution.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Metadata.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Ollama.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Process.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.State.cs`
- Delete: `src/MasterApp/Hosting/CodexCliService.cs`
- Delete: `src/MasterApp/Hosting/CodexExecutableResolver.cs`
- Delete: `src/MasterApp/Hosting/CodexSessionLogReader.cs`
- Delete: `src/MasterApp/Hosting/CodexWorkspacePolicy.cs`

**Codex host references to remove:**
- Modify: `src/MasterApp/Hosting/MasterAppRuntime.cs`
- Modify: `src/MasterApp/Bootstrap/Bootstrapper.cs`
- Modify: `src/MasterApp/Diagnostics/LogKind.cs`
- Modify: `src/MasterApp/Diagnostics/FileLogManager.cs`
- Modify: `src/MasterApp/Models/AppSettings.cs`
- Modify: `src/MasterApp/Models/RuntimeState.cs`
- Modify: `src/MasterApp/Storage/RuntimeStateStore.cs`
- Modify: `templates/state/settings.example.json`
- Modify: `templates/state/runtime-state.example.json`

**Codex UI and assets to remove:**
- Modify: `src/MasterApp/wwwroot/masterapp-ui.js`
- Modify: `src/MasterApp/wwwroot/masterapp-ui.css`
- Delete if unused after UI cleanup: `src/MasterApp/wwwroot/icons/chat.svg`

**LifeJournal Codex dependency to remove:**
- Modify: `src/MasterApp/LifeJournal/LifeJournalService.cs`
- Modify: `src/MasterApp/LifeJournal/LifeJournalAnalyzers.cs`
- Modify: `src/MasterApp/LifeJournal/LifeJournalModels.cs`
- Modify: `Documentation/LifeJournal.md`

**Ollama/Gemma cleanup:**
- Delete: `docs/local-gemma-ollama-setup.md`
- Delete: `scripts/start_gemma.bat`
- Delete: `scripts/stop_gemma.bat`
- Delete: `scripts/test_gemma.bat`
- Delete: `scripts/test_gemma_api.ps1`

**Docs and metadata cleanup:**
- Modify: `README.md`
- Modify: `MASTERAPP_CAPABILITIES_AND_FLOW.md`
- Modify: `docs/app-package-spec.md`
- Modify: `docs/llm-app-authoring-guide.md`
- Modify: `masterapp.ai.json`
- Modify: `sample-package/masterapp.ai.json`

**Lazy-local runtime additions:**
- Create: `src/MasterApp/Access/RemoteAccessPolicy.cs`
- Create: `src/MasterApp/Access/RemoteAccessSessionManager.cs`
- Create: `src/MasterApp/Networking/WifiNetworkInspector.cs`
- Create: `src/MasterApp/Networking/WifiNetworkSnapshot.cs`
- Create: `src/MasterApp/Web/RemoteAccessMiddleware.cs`
- Create: `src/MasterApp/Hosting/LocalPortSelector.cs`
- Modify: `src/MasterApp/Hosting/MasterAppRuntime.cs`
- Modify: `src/MasterApp/Tray/MasterAppApplicationContext.cs`
- Modify: `src/MasterApp/Models/AppSettings.cs`
- Modify: `src/MasterApp/Models/RuntimeState.cs`
- Modify: `src/MasterApp/wwwroot/qr.html`
- Modify: `src/MasterApp/wwwroot/masterapp-ui.js`

**Lazy-local distribution additions:**
- Create: `distributions/lazy-local/README.md`
- Create: `distributions/lazy-local/GPT_INSTRUCTIONS.md`
- Create: `distributions/lazy-local/DRIVE_SETUP.md`
- Create: `distributions/lazy-local/Setup Lazy MasterApp.bat`
- Create: `distributions/lazy-local/setup-lazy-masterapp.ps1`
- Create: `distributions/lazy-local/build-lazy-local.bat`

---

### Task 1: Add Failing Policy Checks For The Cleanup Contract

**Files:**
- Modify: `tests/PolicyChecks/Program.cs`

- [ ] **Step 1: Add a scan helper that can prove product-facing Codex/Ollama/Gemma references are gone**

Add these helper methods near the bottom of `tests/PolicyChecks/Program.cs`:

```csharp
static void AssertNoForbiddenText(string root, IReadOnlyList<string> relativePaths, IReadOnlyList<string> forbiddenTerms)
{
    foreach (var relativePath in relativePaths)
    {
        var path = Path.Combine(root, relativePath);
        if (File.Exists(path))
        {
            AssertFileDoesNotContain(path, forbiddenTerms);
            continue;
        }

        if (Directory.Exists(path))
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.Combine("docs", "superpowers"), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AssertFileDoesNotContain(file, forbiddenTerms);
            }
        }
    }
}

static void AssertFileDoesNotContain(string path, IReadOnlyList<string> forbiddenTerms)
{
    var text = File.ReadAllText(path);
    foreach (var term in forbiddenTerms)
    {
        Assert(
            !text.Contains(term, StringComparison.OrdinalIgnoreCase),
            $"Forbidden cleanup term '{term}' remains in {path}.");
    }
}
```

- [ ] **Step 2: Add a failing cleanup assertion**

Add this near the other top-level checks in `tests/PolicyChecks/Program.cs`:

```csharp
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
        "masterapp.ai.json",
        "sample-package"
    },
    new[] { "Codex", "Ollama", "Gemma" });
```

- [ ] **Step 3: Run the policy check and confirm it fails before cleanup**

Run:

```powershell
dotnet run --project tests\PolicyChecks\PolicyChecks.csproj
```

Expected result: FAIL with a message naming one of the current product-facing references, for example `Forbidden cleanup term 'Codex' remains`.

---

### Task 2: Remove Codex Settings, Runtime Models, Logging, And Storage

**Files:**
- Modify: `src/MasterApp/Models/AppSettings.cs`
- Modify: `src/MasterApp/Models/RuntimeState.cs`
- Modify: `src/MasterApp/Storage/RuntimeStateStore.cs`
- Modify: `src/MasterApp/Bootstrap/Bootstrapper.cs`
- Modify: `src/MasterApp/Diagnostics/LogKind.cs`
- Modify: `src/MasterApp/Diagnostics/FileLogManager.cs`
- Modify: `templates/state/settings.example.json`
- Modify: `templates/state/runtime-state.example.json`
- Modify: `tests/PolicyChecks/Program.cs`

- [ ] **Step 1: Remove settings fields**

In `src/MasterApp/Models/AppSettings.cs`, remove these properties:

```csharp
public string CodexCommand { get; set; } = "codex";
public List<string> WorkspacePaths { get; set; } = new();
public string PreferredBuildCommand { get; set; } = string.Empty;
public string PreferredRestartCommand { get; set; } = string.Empty;
public bool PreferCodexJsonOutput { get; set; } = true;
public int CodexHistoryLimit { get; set; } = 8;
public int CodexMaxDecisionSteps { get; set; } = 20;
public int OllamaMaxDecisionSteps { get; set; } = 36;
public int CodexUsageRequestsPer5Hours { get; set; } = 200;
public int CodexUsageRequestsPerWeek { get; set; } = 2000;
```

- [ ] **Step 2: Remove runtime-state classes**

In `src/MasterApp/Models/RuntimeState.cs`, remove these properties from `RuntimeState`:

```csharp
public List<CodexOperationRecord> CodexOperations { get; set; } = new();
public CodexRuntimeState Codex { get; set; } = new();
```

Remove these classes from the same file:

```csharp
public sealed class CodexRuntimeState
public sealed class CodexChatSession
public sealed class CodexChatMessage
public sealed class CodexChatRun
public sealed class CodexOperationRecord
public sealed class CodexFollowUpAction
public sealed class CodexApprovalRequest
public sealed class CodexApprovalRecord
public sealed class CodexBuildResult
```

Keep `RelaunchStatusRecord` only if non-Codex code still references it after Task 4. If no references remain, remove it in Task 4.

- [ ] **Step 3: Remove storage methods**

In `src/MasterApp/Storage/RuntimeStateStore.cs`, remove methods and private clone helpers for Codex runtime and operations:

```csharp
GetCodexRuntime()
GetCodexOperations()
UpsertCodexOperation(...)
SetCodexRuntime(...)
Clone(CodexRuntimeState value)
Clone(CodexOperationRecord value)
```

- [ ] **Step 4: Remove bootstrap normalization**

In `src/MasterApp/Bootstrap/Bootstrapper.cs`, remove normalization of workspace paths, preferred Codex commands, Codex limits, and `LifeJournal.CodexExecutablePath`. Keep directory normalization for package folders and `ConfigBackupRetentionCount`.

- [ ] **Step 5: Remove Codex log kind**

In `src/MasterApp/Diagnostics/LogKind.cs`, remove `Codex`.

In `src/MasterApp/Diagnostics/FileLogManager.cs`, remove:

```csharp
public void Codex(string source, string message, Exception? ex = null) => Write(LogKind.Codex, "INFO", source, message, ex);
```

- [ ] **Step 6: Clean state templates**

In `templates/state/settings.example.json`, remove all Codex/Ollama fields and remove `lifeJournal.codexExecutablePath`.

In `templates/state/runtime-state.example.json`, remove `codex` and `codexOperations`.

- [ ] **Step 7: Update policy checks that reference removed Codex types**

Remove the existing `CodexWorkspacePolicy`, `CodexSessionLogReader`, and `CodexExecutableResolver` checks from `tests/PolicyChecks/Program.cs`. Keep package, hosted-app, lifecycle, LifeJournal non-Codex, and app deletion checks.

- [ ] **Step 8: Build to expose remaining compile errors**

Run:

```powershell
dotnet build src\MasterApp\MasterApp.csproj -c Debug
```

Expected result at this stage: FAIL if runtime/UI/API still references removed types. Use the errors as the task list for Task 3 and Task 4.

---

### Task 3: Remove Codex Backend Services And API Routes

**Files:**
- Delete: `src/MasterApp/Hosting/CodexBrokerService.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Classification.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Execution.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Metadata.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Ollama.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.Process.cs`
- Delete: `src/MasterApp/Hosting/CodexBrokerService.State.cs`
- Delete: `src/MasterApp/Hosting/CodexCliService.cs`
- Delete: `src/MasterApp/Hosting/CodexExecutableResolver.cs`
- Delete: `src/MasterApp/Hosting/CodexSessionLogReader.cs`
- Delete: `src/MasterApp/Hosting/CodexWorkspacePolicy.cs`
- Modify: `src/MasterApp/Hosting/MasterAppRuntime.cs`

- [ ] **Step 1: Delete Codex hosting files**

Delete the files listed above. Do not leave empty classes or compatibility wrappers.

- [ ] **Step 2: Remove Codex field initialization**

In `src/MasterApp/Hosting/MasterAppRuntime.cs`, remove:

```csharp
private readonly CodexBrokerService _codexService;
_codexService = new CodexBrokerService(_context, this);
```

- [ ] **Step 3: Remove public Codex methods**

Remove these methods from `MasterAppRuntime`:

```csharp
public object GetCodexResponse()
public Task<CodexChatRun> StartCodexRunAsync(...)
public Task<CodexChatRun> StopCodexRunAsync(...)
public Task ClearCodexSessionAsync(...)
public Task SetCodexModelAsync(...)
public Task<CodexChatRun> ResolveCodexApprovalAsync(...)
public async Task WriteCodexEventsAsync(HttpContext context)
```

- [ ] **Step 4: Remove Codex API routes**

Remove route mappings for:

```text
GET  /api/codex
GET  /api/codex/events
POST /api/codex/messages
POST /api/codex/model
POST /api/codex/stop
POST /api/codex/session/new
POST /api/codex/approval
```

- [ ] **Step 5: Remove Codex log route option**

In `MasterAppRuntime.GetLogKind`, remove the `"codex" => LogKind.Codex` branch.

- [ ] **Step 6: Remove Codex-only relaunch and workspace backup code**

In `MasterAppRuntime`, remove methods and references that exist only to support Codex self-relaunch, Codex workspace backup, `.codex/environments/environment.toml`, or restart after a Codex run. Keep tray quit/watchdog behavior. After removal, if `RelaunchStatusRecord` has no references, remove it from `RuntimeState.cs`.

- [ ] **Step 7: Rebuild**

Run:

```powershell
dotnet build src\MasterApp\MasterApp.csproj -c Debug
```

Expected result: remaining failures should be UI, LifeJournal, docs/test references only. There must be no references to deleted Codex hosting classes.

---

### Task 4: Preserve LifeJournal Without Codex

**Files:**
- Modify: `src/MasterApp/LifeJournal/LifeJournalService.cs`
- Modify: `src/MasterApp/LifeJournal/LifeJournalAnalyzers.cs`
- Modify: `src/MasterApp/LifeJournal/LifeJournalModels.cs`
- Modify: `Documentation/LifeJournal.md`
- Modify: `tests/PolicyChecks/Program.cs`

- [ ] **Step 1: Remove Codex-specific settings**

In `src/MasterApp/LifeJournal/LifeJournalModels.cs`, remove:

```csharp
public string CodexExecutablePath { get; set; } = "codex";
```

- [ ] **Step 2: Replace analyzer construction**

In `src/MasterApp/LifeJournal/LifeJournalService.cs`, replace:

```csharp
var fallback = new FakeLifeJournalAnalyzer(_log);
_analyzer = new CodexCliLifeJournalAnalyzer(ResolveSettings(context), _log, fallback);
```

with:

```csharp
_analyzer = new MetadataLifeJournalAnalyzer(_log);
```

Remove the `ResolveSettings` method and `using MasterApp.Hosting;`.

- [ ] **Step 3: Rename fallback analyzer to the production analyzer**

In `src/MasterApp/LifeJournal/LifeJournalAnalyzers.cs`, rename `FakeLifeJournalAnalyzer` to `MetadataLifeJournalAnalyzer`. Update messages so they do not mention Codex. Use this exact uncertainty text:

```csharp
uncertainties = new[] { "Visual interpretation is not enabled, so this summary only uses saved metadata." }
```

Use this exact markdown footer:

```csharp
_Metadata-only analyzer used. Visual details were not interpreted._
```

- [ ] **Step 4: Delete the Codex analyzer**

Remove the entire `CodexCliLifeJournalAnalyzer` class and the private `CodexProcessOutput` record from `LifeJournalAnalyzers.cs`. Keep `SelectEvenlyDistributedPhotos` only if another non-Codex analyzer still uses it; otherwise remove it.

- [ ] **Step 5: Update LifeJournal tests**

In `tests/PolicyChecks/Program.cs`, remove checks for Codex argument construction and executable fallback. Add a check that `MetadataLifeJournalAnalyzer` returns `UsedFallback == true` and a metadata-only uncertainty.

Use this check body:

```csharp
var lifeLog = new LifeJournalLogger(Path.Combine(Path.GetTempPath(), $"MasterApp-LifeJournal-{Guid.NewGuid():N}"));
var metadataAnalyzer = new MetadataLifeJournalAnalyzer(lifeLog);
var metadataOutput = metadataAnalyzer.AnalyzeAsync(
    new LifeDay
    {
        Date = DateOnly.FromDateTime(DateTime.Today),
        Photos = new List<LifePhoto>
        {
            new()
            {
                Id = "photo1",
                LocalTimestamp = DateTimeOffset.Now,
                FileName = "sample.jpg",
                RelativePath = "photos/sample.jpg",
                MimeType = "image/jpeg"
            }
        }
    },
    Array.Empty<string>(),
    null,
    CancellationToken.None).GetAwaiter().GetResult();

Assert(
    metadataOutput.UsedFallback &&
    metadataOutput.Uncertainties.Any(item => item.Contains("metadata", StringComparison.OrdinalIgnoreCase)),
    "LifeJournal should keep working with a metadata-only analyzer after AI integration removal.");
```

- [ ] **Step 6: Update docs**

In `Documentation/LifeJournal.md`, replace the Codex CLI dependency section with a short section titled `Metadata-Only Analysis` that states LifeJournal no longer launches an external AI CLI and currently summarizes saved photo/event metadata only.

---

### Task 5: Remove Codex UI, Polling, Styling, And Icon Surface

**Files:**
- Modify: `src/MasterApp/wwwroot/masterapp-ui.js`
- Modify: `src/MasterApp/wwwroot/masterapp-ui.css`
- Delete if unused: `src/MasterApp/wwwroot/icons/chat.svg`

- [ ] **Step 1: Remove Codex state**

In `masterapp-ui.js`, remove state fields beginning with `latestCodex`, `codexDraft`, `codexSelected`, `codexHistoryScope`, `codexRecentsOpen`, `codexDetailsOpen`, `codexConnectionState`, `codexLastError`, `codexEventSource`, `codexScrollTop`, and `codexStickToBottom`.

- [ ] **Step 2: Remove Codex initialization and refresh**

Remove calls to:

```javascript
connectCodexEvents();
setInterval(refreshCodex, 12000);
refreshCodex();
```

In `refreshAll()`, fetch only `/api/status` and `/api/apps`.

- [ ] **Step 3: Remove the Codex page and bottom nav item**

Remove `renderCodexPage()` from the shell render and remove the bottom nav item:

```javascript
{ id: "codex", label: "Codex", icon: "chat" }
```

- [ ] **Step 4: Remove Codex interaction functions**

Delete functions whose names include `Codex` or `codex`, including submit, stop, approval, model, recent chat, event source, history preview, status formatting, and scroll helpers.

- [ ] **Step 5: Remove Codex logs from settings/log picker**

Replace the current log list:

```javascript
["app", "tunnel", "packages", "ui", "codex"]
```

with:

```javascript
["app", "tunnel", "packages", "ui"]
```

- [ ] **Step 6: Remove Codex CSS**

In `masterapp-ui.css`, remove selectors beginning with `.codex-`, selectors containing `#page-codex`, and `.app-content--codex`.

- [ ] **Step 7: Delete the chat icon if unused**

Run:

```powershell
rg -n "chat" src\MasterApp\wwwroot
```

If the only remaining reference is `src\MasterApp\wwwroot\icons\chat.svg`, delete that icon.

- [ ] **Step 8: Browser check after UI removal**

After the app builds again, open the app in the in-app browser at `http://localhost:19057/store.html` or the actual debug port. Verify the bottom navigation shows only Dashboard, Library, and Store, and the console has no failed `/api/codex` requests.

---

### Task 6: Remove Codex/Ollama/Gemma Docs, Scripts, And Metadata

**Files:**
- Delete: `docs/local-gemma-ollama-setup.md`
- Delete: `scripts/start_gemma.bat`
- Delete: `scripts/stop_gemma.bat`
- Delete: `scripts/test_gemma.bat`
- Delete: `scripts/test_gemma_api.ps1`
- Modify: `README.md`
- Modify: `MASTERAPP_CAPABILITIES_AND_FLOW.md`
- Modify: `docs/app-package-spec.md`
- Modify: `docs/llm-app-authoring-guide.md`
- Modify: `masterapp.ai.json`
- Modify: `sample-package/masterapp.ai.json`

- [ ] **Step 1: Delete Ollama/Gemma files**

Delete the files listed above. They are not part of the remaining product.

- [ ] **Step 2: Rewrite README product setup**

Remove the Codex Chat panel section and Codex settings snippets. Keep setup instructions for package folders, Cloudflare standard flow, build-release, and app package usage until lazy-local docs are added in Task 10.

- [ ] **Step 3: Rewrite capability docs**

In `MASTERAPP_CAPABILITIES_AND_FLOW.md`, remove the built-in maintenance panel capability, `/api/codex` endpoint list, Codex runtime state references, and Codex-assisted maintenance flow. Keep package install, hosted app routing, store, publish, logs, tunnel, and LifeJournal sections.

- [ ] **Step 4: Update app package docs**

In `docs/app-package-spec.md` and `docs/llm-app-authoring-guide.md`, remove statements recommending `masterapp.ai.json` for Codex/Ollama broker inspection. Keep package manifest, static/portable/source app, publish, and icon guidance.

- [ ] **Step 5: Update metadata files**

In `masterapp.ai.json` and `sample-package/masterapp.ai.json`, remove Codex/Ollama references and replace the summary with current package/store/hosting behavior only.

- [ ] **Step 6: Run final text scan**

Run:

```powershell
rg -n -i "codex|ollama|gemma" src scripts templates README.md MASTERAPP_CAPABILITIES_AND_FLOW.md docs masterapp.ai.json sample-package -g "!docs/superpowers/**"
```

Expected result: no matches.

---

### Task 7: Add Lazy-Local Runtime Mode With Same-Wi-Fi QR Access

**Files:**
- Create: `src/MasterApp/Access/RemoteAccessPolicy.cs`
- Create: `src/MasterApp/Access/RemoteAccessSessionManager.cs`
- Create: `src/MasterApp/Networking/WifiNetworkInspector.cs`
- Create: `src/MasterApp/Networking/WifiNetworkSnapshot.cs`
- Create: `src/MasterApp/Web/RemoteAccessMiddleware.cs`
- Create: `src/MasterApp/Hosting/LocalPortSelector.cs`
- Modify: `src/MasterApp/Models/AppSettings.cs`
- Modify: `src/MasterApp/Models/RuntimeState.cs`
- Modify: `src/MasterApp/Storage/RuntimeStateStore.cs`
- Modify: `src/MasterApp/Hosting/MasterAppRuntime.cs`
- Modify: `src/MasterApp/wwwroot/qr.html`

- [ ] **Step 1: Add lazy-local settings**

Add these properties to `AppSettings`:

```csharp
public string RuntimeMode { get; set; } = "standard";
public int PreferredLocalPort { get; set; } = 19057;
public int LocalPortFallbackCount { get; set; } = 20;
public bool WifiOnly { get; set; } = true;
public int SessionQrTtlSeconds { get; set; } = 60;
```

Standard mode keeps existing Cloudflare behavior. Lazy-local mode ignores Cloudflare for phone access.

- [ ] **Step 2: Add selected-port runtime state**

Add this property to `RuntimeState`:

```csharp
public int? ActiveLocalPort { get; set; }
```

Add `RuntimeStateStore.SetActiveLocalPort(int port)` and `RuntimeStateStore.GetActiveLocalPort()` methods.

- [ ] **Step 3: Add port selector**

Create `src/MasterApp/Hosting/LocalPortSelector.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace MasterApp.Hosting;

public static class LocalPortSelector
{
    public static int SelectAvailablePort(int preferredPort, int fallbackCount)
    {
        var start = Math.Clamp(preferredPort, 1, 65535);
        var attempts = Math.Clamp(fallbackCount, 0, 100);
        for (var port = start; port <= Math.Min(65535, start + attempts); port++)
        {
            if (CanBind(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No available local port found from {start} to {Math.Min(65535, start + attempts)}.");
    }

    private static bool CanBind(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
```

- [ ] **Step 4: Port RemoteAccess and Wi-Fi classes**

Copy the same-Wi-Fi access classes from `C:\Dev\MasterAppLocal` into the new files:

```text
src/MasterApp/Access/RemoteAccessPolicy.cs
src/MasterApp/Access/RemoteAccessSessionManager.cs
src/MasterApp/Networking/WifiNetworkInspector.cs
src/MasterApp/Networking/WifiNetworkSnapshot.cs
src/MasterApp/Web/RemoteAccessMiddleware.cs
```

Use namespace `MasterApp.Access`, `MasterApp.Networking`, and `MasterApp.Web`.

- [ ] **Step 5: Runtime URL behavior**

In `MasterAppRuntime`, add:

```csharp
public bool IsLazyLocalMode => string.Equals(_context.Settings.RuntimeMode, "lazy-local", StringComparison.OrdinalIgnoreCase);
public int ActiveLocalPort { get; private set; }
public string LoopbackUrl => $"http://127.0.0.1:{ActiveLocalPort}";
public string LocalUrl => IsLazyLocalMode ? LoopbackUrl : $"http://localhost:{_context.Secrets.LocalPort}";
```

During startup/build, set `ActiveLocalPort`:

```csharp
ActiveLocalPort = IsLazyLocalMode
    ? LocalPortSelector.SelectAvailablePort(_context.Settings.PreferredLocalPort, _context.Settings.LocalPortFallbackCount)
    : _context.Secrets.LocalPort;
_context.RuntimeStateStore.SetActiveLocalPort(ActiveLocalPort);
```

- [ ] **Step 6: Bind lazy-local to LAN**

In `BuildWebApplication()`, use:

```csharp
builder.WebHost.UseUrls(IsLazyLocalMode ? $"http://0.0.0.0:{ActiveLocalPort}" : LocalUrl);
```

Register `RemoteAccessMiddleware` before hosted/installed app middleware only when `IsLazyLocalMode` is true.

- [ ] **Step 7: Generate QR from LAN URL in lazy-local mode**

Add a `GeneratePhoneQrSvg()` method equivalent to `MasterAppLocal`: if lazy-local mode has no active Wi-Fi private IPv4, return a QR message SVG explaining Wi-Fi is unavailable. Otherwise create a one-time ticket and generate:

```text
http://<wifi-ip>:<ActiveLocalPort>/phone/connect?ticket=<ticket>
```

Standard mode keeps generating QR from `PublicUrl`.

- [ ] **Step 8: Add status fields**

In `GetStatusResponse()`, include:

```csharp
runtimeMode = _context.Settings.RuntimeMode,
activeLocalPort = ActiveLocalPort,
loopbackUrl = LoopbackUrl,
lanUrl = IsLazyLocalMode ? BuildLanUrl(GetWifiSnapshot()) : null,
wifiOnly = IsLazyLocalMode && _context.Settings.WifiOnly,
remoteSessionRequired = IsLazyLocalMode,
activeRemoteSessions = IsLazyLocalMode ? _remoteAccessSessionManager.Snapshot().ActiveSessions : 0,
pendingQrTickets = IsLazyLocalMode ? _remoteAccessSessionManager.Snapshot().PendingTickets : 0
```

- [ ] **Step 9: Verify standard mode still builds and starts**

Run:

```powershell
dotnet build src\MasterApp\MasterApp.csproj -c Debug
```

Expected result: PASS.

---

### Task 8: Adjust Tray And UI For Lazy-Local Without Breaking Standard Flow

**Files:**
- Modify: `src/MasterApp/Tray/MasterAppApplicationContext.cs`
- Modify: `src/MasterApp/wwwroot/masterapp-ui.js`
- Modify: `src/MasterApp/wwwroot/qr.html`
- Modify: `src/MasterApp/wwwroot/masterapp-ui.css`

- [ ] **Step 1: Tray menu labels**

In lazy-local mode, tray should show:

```text
Open Store
Open Phone QR
Open Logs Folder
Quit
```

In standard mode, keep existing tunnel actions. Codex actions must not exist in either mode.

- [ ] **Step 2: Dashboard language**

In `masterapp-ui.js`, when `status.runtimeMode === "lazy-local"`, replace tunnel/public URL text with Wi-Fi/local QR text. Do not show tunnel start/stop controls in lazy-local mode.

- [ ] **Step 3: QR page behavior**

In `qr.html`, render the current QR image from `/api/phone-qr.svg` and display:

```javascript
document.getElementById("urlText").textContent = status.lanUrl || status.publicUrl || status.loopbackUrl || "";
```

- [ ] **Step 4: Browser verification**

Run the app in standard mode and verify Dashboard, Library, Store, logs, and package actions still render.

Run the app in lazy-local mode and verify Dashboard, Library, Store, Phone QR, and no tunnel controls render.

---

### Task 9: Add Lazy-Local Setup Runner And Distribution Builder

**Files:**
- Create: `distributions/lazy-local/README.md`
- Create: `distributions/lazy-local/GPT_INSTRUCTIONS.md`
- Create: `distributions/lazy-local/DRIVE_SETUP.md`
- Create: `distributions/lazy-local/Setup Lazy MasterApp.bat`
- Create: `distributions/lazy-local/setup-lazy-masterapp.ps1`
- Create: `distributions/lazy-local/build-lazy-local.bat`

- [ ] **Step 1: Add setup batch wrapper**

Create `distributions/lazy-local/Setup Lazy MasterApp.bat`:

```bat
@echo off
setlocal
set SCRIPT_DIR=%~dp0
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%setup-lazy-masterapp.ps1"
if errorlevel 1 exit /b %errorlevel%
echo.
echo MasterApp lazy-local setup completed.
pause
```

- [ ] **Step 2: Add PowerShell setup runner**

Create `distributions/lazy-local/setup-lazy-masterapp.ps1` with these responsibilities:

```powershell
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $scriptRoot 'MasterApp.exe'
if (-not (Test-Path $exePath)) {
    throw "MasterApp.exe was not found next to this setup script."
}

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$stateDir = Join-Path $localAppData 'MasterApp\State'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

$driveRoot = Get-PSDrive -PSProvider FileSystem |
    ForEach-Object { Join-Path $_.Root 'My Drive\MasterApp' } |
    Where-Object { Test-Path (Split-Path -Parent $_) } |
    Select-Object -First 1

if (-not $driveRoot) {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $driveRoot = Join-Path $documents 'MasterApp'
}

$incoming = Join-Path $driveRoot 'Incoming'
$processed = Join-Path $driveRoot 'Processed'
$failed = Join-Path $driveRoot 'Failed'
$published = Join-Path $driveRoot 'Published'
@($driveRoot, $incoming, $processed, $failed, $published) | ForEach-Object {
    New-Item -ItemType Directory -Force -Path $_ | Out-Null
}

$settings = [ordered]@{
    runtimeMode = 'lazy-local'
    incomingFolder = $incoming
    processedFolder = $processed
    failedFolder = $failed
    publishedFolder = $published
    autoStartTunnel = $false
    logLevel = 'Info'
    packageScanIntervalSeconds = 5
    preferredLocalPort = 19057
    localPortFallbackCount = 20
    wifiOnly = $true
    sessionQrTtlSeconds = 60
    configBackupRetentionCount = 10
    lifeJournal = [ordered]@{
        maxImagesForAnalysis = 16
        analysisTimeoutSeconds = 240
        autoFinalizeHourLocal = 3
        photoMaxWidth = 900
        jpegQuality = 75
    }
}

$settingsPath = Join-Path $stateDir 'settings.json'
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

$runtimeStatePath = Join-Path $stateDir 'runtime-state.json'
if (-not (Test-Path $runtimeStatePath)) {
    @{ apps = @{} } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runtimeStatePath -Encoding UTF8
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'MasterApp Lazy.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $scriptRoot
$shortcut.Save()

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if ($isAdmin) {
    New-NetFirewallRule -DisplayName 'MasterApp Lazy Local' -Direction Inbound -Program $exePath -Action Allow -Profile Private -ErrorAction SilentlyContinue | Out-Null
}

Write-Host "Settings: $settingsPath"
Write-Host "Drive folder: $driveRoot"
Write-Host "Shortcut: $shortcutPath"
if (-not $isAdmin) {
    Write-Host "Firewall rule was skipped because setup is not elevated. If the phone cannot connect, allow MasterApp on Private networks."
}
```

- [ ] **Step 3: Add lazy-local build script**

Create `distributions/lazy-local/build-lazy-local.bat`:

```bat
@echo off
setlocal
for %%I in ("%~dp0..\..") do set ROOT=%%~fI
set OUT=%ROOT%\artifacts\lazy-local\MasterApp-Lazy
if not exist "%OUT%" mkdir "%OUT%"
dotnet publish "%ROOT%\src\MasterApp\MasterApp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=None -p:NuGetAudit=false -o "%OUT%"
if errorlevel 1 exit /b %errorlevel%
copy /Y "%ROOT%\distributions\lazy-local\Setup Lazy MasterApp.bat" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\setup-lazy-masterapp.ps1" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\README.md" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\GPT_INSTRUCTIONS.md" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\DRIVE_SETUP.md" "%OUT%\" >nul
echo.
echo Lazy-local distribution created in:
echo %OUT%
```

- [ ] **Step 4: Add README**

Create `distributions/lazy-local/README.md` with exactly these sections:

```markdown
# MasterApp Lazy Local

## Installation

1. Extract the `MasterApp-Lazy` folder.
2. Run `Setup Lazy MasterApp.bat`.
3. If Windows asks for network access, allow Private networks.
4. Copy `GPT_INSTRUCTIONS.md` into your ChatGPT Project instructions.

## Usage

1. Run `MasterApp.exe` or the `MasterApp Lazy` desktop shortcut.
2. Ask ChatGPT to create a MasterApp app ZIP.
3. Save the ZIP into the `Incoming` folder from `DRIVE_SETUP.md`.
4. Open `Open Phone QR` from the tray icon.
5. Scan the QR code from a phone on the same Wi-Fi.
```

- [ ] **Step 5: Add Drive setup doc**

Create `distributions/lazy-local/DRIVE_SETUP.md` with a short explanation that setup creates or uses a `MasterApp` folder with `Incoming`, `Processed`, `Failed`, and `Published`, preferring Google Drive if present and falling back to Documents.

- [ ] **Step 6: Add GPT instructions**

Create `distributions/lazy-local/GPT_INSTRUCTIONS.md` by adapting `docs/llm-app-authoring-guide.md` into a concise prompt for creating valid MasterApp ZIP packages. It must not mention Codex, Ollama, Gemma, Cloudflare, or public hosting.

---

### Task 10: End-To-End Verification

**Files:**
- Test: `tests/PolicyChecks/Program.cs`
- Test: `src/MasterApp/MasterApp.csproj`
- Test: `distributions/lazy-local/build-lazy-local.bat`

- [ ] **Step 1: Run policy checks**

Run:

```powershell
dotnet run --project tests\PolicyChecks\PolicyChecks.csproj
```

Expected result: PASS.

- [ ] **Step 2: Run source text audit**

Run:

```powershell
rg -n -i "codex|ollama|gemma" src scripts templates README.md MASTERAPP_CAPABILITIES_AND_FLOW.md docs masterapp.ai.json sample-package -g "!docs/superpowers/**"
```

Expected result: no matches.

- [ ] **Step 3: Build debug app**

Run:

```powershell
dotnet build src\MasterApp\MasterApp.csproj -c Debug
```

Expected result: PASS.

- [ ] **Step 4: Build release app**

Run:

```powershell
scripts\build-release.bat
```

Expected result: `artifacts\release\MasterApp.exe` exists.

- [ ] **Step 5: Build lazy-local distribution**

Run:

```powershell
distributions\lazy-local\build-lazy-local.bat
```

Expected result: `artifacts\lazy-local\MasterApp-Lazy\MasterApp.exe`, setup scripts, and docs exist.

- [ ] **Step 6: Runtime smoke test standard mode**

Start the debug app, then request:

```powershell
Invoke-RestMethod http://127.0.0.1:19057/healthz
Invoke-RestMethod http://127.0.0.1:19057/api/status
Invoke-RestMethod http://127.0.0.1:19057/api/apps
```

Expected result: health is OK, status loads, apps load, `/api/codex` returns 404.

- [ ] **Step 7: Runtime smoke test lazy-local mode**

Run the lazy setup against a temp state folder or temporarily set `%LOCALAPPDATA%\MasterApp\State\settings.json` to lazy-local mode. Start the app and request:

```powershell
Invoke-RestMethod http://127.0.0.1:19057/healthz
Invoke-RestMethod http://127.0.0.1:19057/api/status
```

Expected result: status includes `runtimeMode = lazy-local`, `activeLocalPort`, and no Codex fields.

- [ ] **Step 8: Browser UI verification**

Use the in-app browser to open the app. Verify:

- Bottom navigation contains Dashboard, Library, Store.
- No Codex tab exists.
- No network request to `/api/codex` appears.
- Dashboard and Store still render installed apps.
- Lazy-local QR page renders a QR or Wi-Fi-unavailable message.

- [ ] **Step 9: Package workflow regression**

Drop a known valid app ZIP into the configured `Incoming` folder. Verify:

- ZIP moves to `Processed`.
- App appears in Store/Library.
- App starts through `/api/apps/{appId}/start`.
- Hosted app opens under `/apps/{appId}/`.
- Publish still works for a package that declares `publish`.

---

## Self-Review

**Spec coverage:** The plan covers complete Codex/Ollama/Gemma removal across backend, UI, state, settings, tests, docs, scripts, and LifeJournal. It also covers lazy-local setup, Drive folder creation, same-Wi-Fi QR, dynamic port fallback, and distribution docs.

**Ambiguity check:** The plan intentionally removes Codex entirely instead of adding a disabled mode. Standard flow keeps Cloudflare tunnel behavior; lazy-local flow bypasses Cloudflare. LifeJournal keeps working with metadata-only analysis because Codex visual analysis cannot remain under a complete Codex removal requirement.

**Regression focus:** Verification explicitly checks package install, Store/Library UI, hosted app routing, publishing, standard runtime health, lazy-local runtime health, no `/api/codex`, and no product-facing text references to Codex/Ollama/Gemma.

**Placeholder scan:** The plan has been checked for unresolved placeholder markers and deferred work language. If an implementer finds a new Codex reference during the audit, it must be removed in the same cleanup pass before verification is considered complete.
