# MasterApp Capabilities And Full Flow

## What MasterApp Is

MasterApp is a Windows tray-hosted app runtime and packaging hub for local or phone-accessible web apps.

At a high level it does five jobs:

1. Watches an inbox folder for app package ZIPs.
2. Installs those apps locally into `%LOCALAPPDATA%\MasterApp\Apps\...`.
3. Serves static apps or proxies runnable apps through one stable host.
4. Provides phone access either through local Wi-Fi lazy mode or through an optional Cloudflare tunnel.
5. Keeps install, runtime, publish, and log state on disk for predictable operation.

## Live Folders And State

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

## Core Capabilities

### 1. Package Inbox And Automatic Install

MasterApp treats `incomingFolder` as a live inbox and scans only top-level `*.zip` files.

What it can install:

- `static` apps: HTML/CSS/JS served directly from `wwwroot`
- `portable` apps: prebuilt runnable apps that MasterApp starts and proxies
- `source` apps: source packages that MasterApp builds during install

What happens during install:

1. MasterApp detects a ZIP in `Incoming`.
2. It copies the ZIP into a temp area first.
3. It extracts the archive safely.
4. It finds and validates exactly one root `app.manifest.json`.
5. For `source` apps it runs `build.installCommand`.
6. If the same app is currently running, MasterApp stops that app before reinstall.
7. It installs the package into `%LOCALAPPDATA%\MasterApp\Apps\<appId>\<version>\`.
8. It preserves declared `dataDirectories` through `%LOCALAPPDATA%\MasterApp\Apps\<appId>\_shared\`.
9. It validates the installed layout.
10. On success it moves the original ZIP to `Processed`.
11. On failure it moves the ZIP to `Failed` and writes a sibling `.error.txt`.

### 2. Hosted App Serving And Proxying

Installed apps are available through MasterApp under:

- `/apps/<appId>/`

Behavior by app type:

- `static`: MasterApp serves files directly from the installed `wwwroot`
- `portable` and `source`: MasterApp launches the app on demand and proxies HTTP traffic to it

Important hosted behavior:

- MasterApp is the stable outer host.
- Runnable apps do not need to expose themselves directly to the user.
- Phone-facing access uses the same outer URL shape.
- Absolute-path apps are normalized by the host/proxy layer so hosted apps can live under `/apps/<appId>/...`.

### 3. App Process Orchestration

For runnable apps MasterApp:

- starts apps on demand
- assigns or manages runtime port usage
- tracks run state in runtime state
- can stop a running app from the dashboard/API
- restarts running apps when needed to pick up a newly installed version

EXE-based apps are expected to:

- bind to `MASTERAPP_PORT` or `ASPNETCORE_URLS`
- expose a health endpoint such as `/api/health`
- keep running until MasterApp stops them
- avoid hard-coded absolute paths

### 4. Publish Flow

Installed apps can optionally expose a publish capability through the manifest `publish` block.

When publish is available, MasterApp can:

1. Run `publish.command` inside the installed app.
2. Collect the published output from `publish.outputPath`.
3. Copy the artifact into `publishedFolder\<appId>\<version>\`.
4. If `createZip` is true, build a new installable ZIP beside the publish output.
5. When publishing a `source` app, repackage the published payload as an installable `portable` package.

### 5. Tray Application And Operator Controls

Current tray actions include:

- Open Dashboard
- Open Public
- Open Phone QR
- Open Logs Folder
- Restart Tunnel
- Stop Tunnel
- Quit

In lazy local mode, Phone QR points to a local Wi-Fi ticket. In standard mode, it points to the configured public URL.

### 6. Local Web Host And API Surface

MasterApp starts a local web host on:

- standard mode: `http://localhost:<localPort>`
- lazy local mode: `http://0.0.0.0:<selectedPort>` with loopback and LAN URLs reported through `/api/status`

Important endpoints include:

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
- `/api/phone-qr.svg`
- `/phone/scan`
- `/phone/connect`

Tunnel endpoints are mapped only outside lazy local mode.

### 7. Phone Access Modes

Standard mode can expose MasterApp through Cloudflare:

- start tunnel
- stop tunnel
- restart tunnel
- auto-start tunnel if configured
- generate a QR code that points to the public URL

Lazy local mode exposes MasterApp only on the current machine's LAN:

- chooses `preferredLocalPort` or a nearby free fallback port
- binds the web host for LAN access
- generates a short-lived QR ticket
- grants the phone a session cookie after scanning
- keeps the same `/apps/<appId>/` app URLs

This keeps the simple home Wi-Fi workflow separate from the public-hostname workflow.

### 8. Runtime State, Logs, And Diagnostics

Important persisted runtime information:

- installed apps
- active versions
- run state
- active local port
- last package result
- last publish result
- last scan timestamp and reason
- tunnel process state

Logs are separated by kind:

- app
- tunnel
- packages
- ui

### 9. Safety And Upgrade Behavior

Important safety behaviors:

- ZIPs are copied to temp before extraction.
- Scans are top-level `*.zip` only.
- Manifest paths are validated.
- App installs use relative package contracts.
- Data directories can survive upgrades through `_shared`.
- Old versions are cleaned up after successful upgrade.
- Source installs fail hard if build output is invalid.
- Failed packages are archived with error text.
- Web proxy does not auto-follow redirects blindly.

## End-To-End Flows

### Flow A: MasterApp Startup

1. MasterApp boots from the Windows tray app.
2. It loads bootstrap paths, settings, secrets, and runtime state.
3. It ensures configured directories exist.
4. It selects a port.
5. It starts the local web host.
6. It starts the package watcher.
7. In standard mode, it starts the Cloudflare tunnel when `autoStartTunnel` is enabled.
8. The tray icon becomes the operator entry point.

### Flow B: User Drops A ZIP Into Incoming

1. A package ZIP is copied into `incomingFolder`.
2. The watcher scan sees the top-level ZIP.
3. MasterApp copies it to temp and extracts it.
4. `app.manifest.json` is validated.
5. If it is a `source` package, MasterApp runs the install build command.
6. If the same app is running, MasterApp stops that app first.
7. The version is installed into `%LOCALAPPDATA%\MasterApp\Apps\<appId>\<version>\`.
8. Shared data folders are synchronized from `_shared`.
9. Runtime state is updated.
10. The ZIP is moved to `Processed`, or to `Failed` plus `.error.txt` if anything breaks.

### Flow C: User Opens An Installed App

1. The dashboard or store opens `/apps/<appId>/`.
2. For a `static` app, MasterApp serves files directly.
3. For a runnable app, MasterApp ensures the app process is running.
4. The runtime assigns or uses the proper port and proxies requests to the app.
5. The user sees a stable app URL through MasterApp.

### Flow D: Lazy Local Phone Use

1. User runs the lazy local starter.
2. MasterApp chooses a local port and reports loopback/LAN URLs.
3. User opens Phone QR from tray or dashboard.
4. The phone scans the QR while on the same Wi-Fi.
5. MasterApp consumes the short-lived ticket and sets a session cookie.
6. The phone opens the dashboard or any installed app under `/apps/<appId>/`.

### Flow E: Public Phone Use

1. Cloudflare is configured with a public hostname.
2. MasterApp exposes its surface through the tunnel.
3. The QR page or tray action points the phone to the public URL.
4. The phone opens the hosted app under the same `/apps/<appId>/` structure.
5. The user can optionally add the web app to the home screen.

### Flow F: Publish An Installed App

1. The app must already be installed and must declare a `publish` block.
2. User triggers publish from the UI/API.
3. MasterApp runs the publish command.
4. MasterApp copies the publish output into `publishedFolder\<appId>\<version>\`.
5. If requested, MasterApp also builds an installable ZIP from the published result.
6. Runtime state stores the last publish result and artifact paths.

## Package Contract For Apps

Every installable app package should:

- include exactly one root `app.manifest.json`
- use relative paths only
- choose the right `appType`
- include `masterapp.ai.json` whenever maintenance hints matter
- include a real bitmap app icon for Store-visible apps
- use `dataDirectories` for persistent writable content

## Repository Files That Best Explain MasterApp

Best first-stop docs:

- `README.md`
- `docs/app-package-spec.md`
- `docs/llm-app-authoring-guide.md`

Best first-stop code areas:

- `src/MasterApp/Hosting/MasterAppRuntime.cs`
- `src/MasterApp/Packages/PackageManager.cs`
- `src/MasterApp/Packages/AppPublisher.cs`
- `src/MasterApp/Hosting/AppProcessManager.cs`
- `src/MasterApp/Tray/MasterAppApplicationContext.cs`
- `src/MasterApp/Storage/RuntimeStateStore.cs`

Main web assets:

- `src/MasterApp/wwwroot/dashboard.html`
- `src/MasterApp/wwwroot/store.html`
- `src/MasterApp/wwwroot/qr.html`
- `src/MasterApp/wwwroot/masterapp-ui.js`
- `src/MasterApp/wwwroot/masterapp-ui.css`

## Short Summary

MasterApp is a Windows tray runtime that turns app ZIPs into locally installed, web-served, optionally phone-accessible apps. It manages inbox install flow, hosted serving/proxying, process lifecycle, publish/export, Cloudflare public access, local Wi-Fi access, operator tray controls, runtime state, and logs.
