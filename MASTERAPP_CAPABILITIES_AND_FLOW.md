# MasterApp Capabilities And Full Flow

## What MasterApp is

MasterApp is a Windows tray-hosted app runtime and packaging hub for local or phone-accessible apps.

At a high level it does five jobs:

1. Watches an inbox folder for app package ZIPs.
2. Installs those apps locally into `%LOCALAPPDATA%\MasterApp\Apps\...`.
3. Serves or proxies installed apps through one local web host.
4. Optionally exposes the same surface through a Cloudflare tunnel for phone access.
5. Hosts a built-in Codex panel so the machine can inspect, debug, and modify allowed workspaces through a brokered flow.

This document describes both the product capabilities and the end-to-end flow as the current repository implements them.

## Live folders and state on this machine

Current live settings are read from:

- `%LOCALAPPDATA%\MasterApp\State\settings.json`

Current configured inbox path on this machine:

- `G:\My Drive\MasterApp\Incoming`

Current configured archive/output paths on this machine:

- `processedFolder`: `C:\MasterApp\Processed`
- `failedFolder`: `C:\MasterApp\Failed`
- `publishedFolder`: `C:\MasterApp\Published`

Main local state area:

- `%LOCALAPPDATA%\MasterApp\State\settings.json`
- `%LOCALAPPDATA%\MasterApp\State\secrets.json`
- `%LOCALAPPDATA%\MasterApp\State\runtime-state.json`
- `%LOCALAPPDATA%\MasterApp\State\Backups\...`

Main installed app area:

- `%LOCALAPPDATA%\MasterApp\Apps\<appId>\<version>\`
- `%LOCALAPPDATA%\MasterApp\Apps\<appId>\_shared\` for persistent app data that survives upgrades

Main logs area:

- `%LOCALAPPDATA%\MasterApp\Logs\`

## Core capabilities

### 1. Package inbox and automatic install

MasterApp treats `incomingFolder` as a live inbox. It scans only top-level `*.zip` files there.

What it can install:

- `static` apps: HTML/CSS/JS served directly from `wwwroot`
- `portable` apps: prebuilt runnable apps that MasterApp starts and proxies
- `source` apps: source packages that MasterApp builds during install, then runs and proxies like portable apps

What happens during install:

1. MasterApp detects a ZIP in `Incoming`.
2. It copies the ZIP into a temp area first.
3. It extracts the archive safely.
4. It finds and validates exactly one root `app.manifest.json`.
5. For `source` apps it runs `build.installCommand`.
6. If the same app is currently running, MasterApp stops that app before reinstall.
7. It installs the package into `%LOCALAPPDATA%\MasterApp\Apps\<appId>\<version>\`.
8. It preserves declared `dataDirectories` through `%LOCALAPPDATA%\MasterApp\Apps\<appId>\_shared\`.
9. It validates the installed layout:
   - `static`: `wwwroot` plus the declared entry file must exist
   - runnable apps: `launch.kind` must be `webApp` and `launch.executablePath` must exist
10. On success it moves the original ZIP to `Processed`.
11. On failure it moves the ZIP to `Failed` and writes a sibling `.error.txt`.

### 2. Hosted app serving and proxying

Installed apps are available through MasterApp under:

- `/apps/<appId>/`

Behavior by app type:

- `static`: MasterApp serves files directly from the installed `wwwroot`
- `portable` and `source`: MasterApp launches the app on demand and proxies HTTP traffic to it

Important hosted behavior:

- MasterApp is the stable outer host
- runnable apps do not need to expose themselves directly to the user
- phone-facing access can stay under the same outer URL shape
- absolute-path apps are normalized by the host/proxy layer so hosted apps can live under `/apps/<appId>/...`

### 3. App process orchestration

For runnable apps MasterApp manages process lifecycle:

- starts apps on demand
- assigns or manages runtime port usage
- tracks run state in runtime state
- can stop a running app from the dashboard/API
- restarts running apps when needed to pick up a newly installed version

EXE-based apps are expected to cooperate with MasterApp:

- bind to `MASTERAPP_PORT` or `ASPNETCORE_URLS`
- expose a health endpoint such as `/api/health`
- keep running until MasterApp stops them
- avoid hard-coded absolute paths

### 4. Publish flow

Installed apps can optionally expose a publish capability through the manifest `publish` block.

When publish is available, MasterApp can:

1. Run `publish.command` inside the installed app.
2. Collect the published output from `publish.outputPath`.
3. Copy the artifact into `publishedFolder\<appId>\<version>\`.
4. If `createZip` is true, build a new installable ZIP beside the publish output.
5. When publishing a `source` app, repackage the published payload as an installable `portable` package.

This means MasterApp supports two distinct lanes:

- install from inbox ZIP
- publish shareable output from an already installed app

### 5. Tray application and operator controls

MasterApp is primarily a Windows tray app.

Current tray actions include:

- Open Dashboard
- Open Public
- Open Phone QR
- Open Logs Folder
- Restart Tunnel
- Stop Tunnel
- Quit

Operational meaning:

- the tray is the operator/admin surface
- the hosted app pages are the app/user surface
- phone access and admin controls are intentionally different concerns

### 6. Local web host and dashboard/API surface

MasterApp starts a local web host on:

- `http://localhost:<localPort>`

Important local endpoints include:

- `/healthz`
- `/api/status`
- `/api/settings`
- `/api/apps`
- `/api/apps/{appId}/start`
- `/api/apps/{appId}/stop`
- `/api/apps/{appId}/publish`
- `/api/apps/{appId}/delete`
- `/api/logs/{name}`
- `/api/packages/rescan`
- `/api/tunnel/start`
- `/api/tunnel/stop`
- `/api/tunnel/restart`
- `/api/codex`
- `/api/codex/events`
- `/api/codex/messages`
- `/api/codex/model`
- `/api/codex/stop`
- `/api/codex/session/new`
- `/api/codex/approval`
- `/api/phone-qr.svg`

Built-in UI pages include:

- `dashboard.html`
- `store.html`
- `qr.html`

### 7. Cloudflare tunnel and phone access

MasterApp can expose its hosted surface through a Cloudflare tunnel.

Capabilities:

- start tunnel
- stop tunnel
- restart tunnel
- auto-start tunnel at runtime start if configured
- expose a public hostname
- generate a QR code that points to the public URL

This is what makes the phone-facing flow possible without shipping a native iOS app package.

Phone model:

- apps are still web apps
- users access them through HTTPS
- iPhone installation is browser-based via Safari "Add to Home Screen"
- MasterApp does not generate native iOS binaries

### 8. Codex panel inside MasterApp

MasterApp includes a built-in Codex tab that brokers local Codex CLI usage.

Current capabilities:

- reads recent chats and model state from the local Codex environment
- exposes a chat UI in the dashboard
- can start, stream, stop, and reset Codex runs
- can switch model through the UI/API
- keeps recent operation history in runtime state
- persists relaunch/build-related run metadata
- supports approval-driven execution instead of silent machine control
- injects workspace policy and optional `masterapp.ai.json` hints

Operational constraints of the panel:

- allowed workspaces come from `settings.json -> workspacePaths`
- build and restart actions stay under MasterApp control
- approval decisions are explicit API events
- restart scheduling is handled by MasterApp with backup state, relaunch marker, and helper flow

### 9. Runtime state, logs, and diagnostics

MasterApp keeps product-state and debugging state on disk.

Important persisted runtime information:

- installed apps
- active versions
- run state
- last package result
- last publish result
- last scan timestamp and reason
- tunnel process state
- Codex runtime state and recent operations
- relaunch status

Logs are separated by kind:

- app
- tunnel
- packages
- ui
- codex

This gives MasterApp a real operational/debugging surface, not just a launcher.

### 10. Safety and upgrade behavior

Important safety behaviors implemented in the current code:

- ZIPs are copied to temp before extraction
- scans are top-level `*.zip` only
- manifest paths are validated
- app installs use relative package contracts
- data directories can survive upgrades through `_shared`
- old versions are cleaned up after successful upgrade
- source installs fail hard if build output is invalid
- failed packages are archived with error text
- web proxy does not auto-follow redirects blindly
- restart scheduling backs up key state before relaunch

## The full flow from zero to a running app

### Flow A: MasterApp startup

1. MasterApp boots from the Windows tray app.
2. It loads bootstrap paths, settings, secrets, and runtime state.
3. It ensures configured directories exist.
4. It starts the local web host.
5. It starts the package watcher.
6. If `autoStartTunnel` is enabled, it starts the Cloudflare tunnel.
7. The tray icon becomes the operator entry point.

Result:

- local dashboard is reachable
- apps can be scanned/served
- phone/public access can come online

### Flow B: User drops a ZIP into Incoming

1. A package ZIP is copied into `G:\My Drive\MasterApp\Incoming`.
2. The watcher scan sees the top-level ZIP.
3. MasterApp copies it to temp and extracts it.
4. `app.manifest.json` is validated.
5. If it is a `source` package, MasterApp runs the install build command.
6. If the same app is running, MasterApp stops that app first.
7. The version is installed into `%LOCALAPPDATA%\MasterApp\Apps\<appId>\<version>\`.
8. Shared data folders are synchronized from `_shared`.
9. The runtime state is updated to mark the app installed.
10. The ZIP is moved to `Processed`, or to `Failed` plus `.error.txt` if anything breaks.

Result:

- the app is now installed and visible in the MasterApp app list

### Flow C: User opens an installed app

1. The dashboard or store opens `/apps/<appId>/`.
2. For a `static` app, MasterApp serves files directly.
3. For a runnable app, MasterApp ensures the app process is running.
4. The runtime assigns/uses the proper port and proxies requests to the app.
5. The user sees a stable app URL through MasterApp rather than talking to the app process directly.

Result:

- static apps feel like hosted sites
- runnable apps feel like hosted sites, even though they are local processes behind the proxy

### Flow D: Phone/public use

1. Tunnel is configured with a public hostname.
2. MasterApp exposes its surface through Cloudflare.
3. The QR page or tray action points the phone to the public URL.
4. The phone opens the hosted app under the same `/apps/<appId>/` structure.
5. The user can optionally add the web app to the iPhone home screen.

Result:

- phone access works as a web-delivered app experience

### Flow E: Publish an installed app

1. The app must already be installed and must declare a `publish` block.
2. User triggers publish from the UI/API.
3. MasterApp runs the publish command.
4. MasterApp copies the publish output into `publishedFolder\<appId>\<version>\`.
5. If requested, MasterApp also builds an installable ZIP from the published result.
6. Runtime state stores the last publish result and artifact paths.

Result:

- MasterApp can act as both installer and exporter for app packages

### Flow F: Codex-assisted maintenance

1. User opens the Codex tab in MasterApp.
2. The UI fetches `/api/codex` and subscribes to `/api/codex/events`.
3. User sends a prompt to `/api/codex/messages`.
4. MasterApp resolves workspace policy and permitted workspace access.
5. The broker starts the local Codex CLI with the selected model/workspace.
6. Events stream back into the UI.
7. If approval is needed, MasterApp pauses and waits for an explicit approval decision.
8. Build or relaunch requests stay under MasterApp's controlled relaunch path.

Result:

- MasterApp is not only the app host but also the maintenance shell for the allowed workspaces

## Package contract for apps that want to run in MasterApp

Every installable app package should be built around these truths:

- exactly one root `app.manifest.json`
- use relative paths only
- choose the right `appType`
- include `masterapp.ai.json` whenever brokered inspection/fixes matter
- include a real bitmap app icon for Store-visible apps
- use `dataDirectories` for persistent writable content

Decision that should be made first for any new app:

### Option 1: HTML-based app

Use `static` when the app is pure frontend and does not need a local executable.

Best for:

- lightweight browser apps
- fast packaging
- low install complexity

### Option 2: EXE-based app

Use `portable` or `source` when the app needs C#, ASP.NET, local filesystem access, device integration, or richer Windows-native behavior.

Best for:

- local web apps backed by a local server
- apps that need Windows integration
- apps that should later publish a standalone/shareable build

## Repository files that best explain MasterApp

Best first-stop docs:

- `README.md`
- `docs/app-package-spec.md`
- `docs/llm-app-authoring-guide.md`

Best first-stop code areas:

- `src/MasterApp/Hosting/MasterAppRuntime.cs`
- `src/MasterApp/Packages/PackageManager.cs`
- `src/MasterApp/Packages/AppPublisher.cs`
- `src/MasterApp/Hosting/AppProcessManager.cs`
- `src/MasterApp/Hosting/CodexBrokerService.cs`
- `src/MasterApp/Tray/MasterAppApplicationContext.cs`
- `src/MasterApp/Storage/RuntimeStateStore.cs`

Main web assets:

- `src/MasterApp/wwwroot/dashboard.html`
- `src/MasterApp/wwwroot/store.html`
- `src/MasterApp/wwwroot/qr.html`
- `src/MasterApp/wwwroot/masterapp-ui.js`
- `src/MasterApp/wwwroot/masterapp-ui.css`

## Short summary

MasterApp is a Windows tray runtime that turns app ZIPs into locally installed, web-served, optionally phone-accessible apps. It manages inbox install flow, hosted serving/proxying, process lifecycle, publish/export, Cloudflare-based public access, operator tray controls, runtime/log state, and a built-in Codex maintenance panel. The central contract is simple: package apps as a valid MasterApp ZIP, drop them into `Incoming`, and MasterApp handles install, hosting, and optional publish from there.
