# The Incredible Machine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and install a MasterApp static package that runs the local The Incredible Machine DOS files through js-dos.

**Architecture:** The package is a self-contained static app. MasterApp serves it via the existing `/apps/the-incredible-machine/` static app route; js-dos runs the bundled DOS game in the browser.

**Tech Stack:** MasterApp static package, js-dos 8 runtime, DOSBox config, PowerShell packaging, browser smoke checks.

---

### Task 1: Prepare Package Workspace

**Files:**
- Create: `artifacts/packages/the-incredible-machine-1.0.0/`
- Read: `C:\Dev\incredible-machine\TIM.EXE`

- [ ] **Step 1: Create package directories**

Run:

```powershell
New-Item -ItemType Directory -Force -Path 'artifacts\packages\the-incredible-machine-1.0.0\wwwroot\game\bundle\.jsdos' | Out-Null
New-Item -ItemType Directory -Force -Path 'artifacts\packages\the-incredible-machine-1.0.0\wwwroot\vendor\js-dos' | Out-Null
New-Item -ItemType Directory -Force -Path 'artifacts\packages\the-incredible-machine-1.0.0\assets' | Out-Null
```

Expected: directories exist.

- [ ] **Step 2: Copy local game files**

Run:

```powershell
Copy-Item -LiteralPath 'C:\Dev\incredible-machine\*' -Destination 'artifacts\packages\the-incredible-machine-1.0.0\wwwroot\game\bundle' -Force
```

Expected: `TIM.EXE` exists in the bundle folder.

### Task 2: Download js-dos Runtime

**Files:**
- Create: `artifacts/packages/the-incredible-machine-1.0.0/wwwroot/vendor/js-dos/`

- [ ] **Step 1: Download official runtime archive**

Run:

```powershell
$zip = Join-Path $env:TEMP 'js-dos-release.zip'
Invoke-WebRequest 'https://cdn.jsdelivr.net/npm/js-dos@8.3.20/release.zip' -OutFile $zip
Expand-Archive -LiteralPath $zip -DestinationPath 'artifacts\packages\the-incredible-machine-1.0.0\wwwroot\vendor\js-dos' -Force
```

Expected: `js-dos.js` and `js-dos.css` are present somewhere under the vendor folder.

### Task 3: Add Launcher and Manifest

**Files:**
- Create: `artifacts/packages/the-incredible-machine-1.0.0/app.manifest.json`
- Create: `artifacts/packages/the-incredible-machine-1.0.0/masterapp.ai.json`
- Create: `artifacts/packages/the-incredible-machine-1.0.0/wwwroot/index.html`
- Create: `artifacts/packages/the-incredible-machine-1.0.0/wwwroot/styles.css`
- Create: `artifacts/packages/the-incredible-machine-1.0.0/wwwroot/app.js`
- Create: `artifacts/packages/the-incredible-machine-1.0.0/wwwroot/game/bundle/.jsdos/dosbox.conf`

- [ ] **Step 1: Add DOSBox config**

Use this config:

```ini
[sdl]
autolock=false
mouse_emulation=integration

[render]
scaler=none

[cpu]
core=auto
cycles=auto

[autoexec]
echo off
mount c .
c:
TIM.EXE
```

- [ ] **Step 2: Add static launcher files**

The launcher must reference package-local js-dos files and load `game/the-incredible-machine.jsdos`.

### Task 4: Build and Install Package

**Files:**
- Create: `artifacts/packages/the-incredible-machine-1.0.0/wwwroot/game/the-incredible-machine.jsdos`
- Create: `artifacts/packages/TheIncredibleMachine-1.0.0.zip`
- Copy to: configured MasterApp Incoming folder from `%LOCALAPPDATA%\MasterApp\State\settings.json`

- [ ] **Step 1: Compress js-dos bundle**

Run a ZipArchive-based command that writes `/` separators inside the bundle:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$source = Resolve-Path 'artifacts\packages\the-incredible-machine-1.0.0\wwwroot\game\bundle'
$bundle = Resolve-Path 'artifacts\packages\the-incredible-machine-1.0.0\wwwroot\game'
$target = Join-Path $bundle 'the-incredible-machine.jsdos'
if (Test-Path $target) { Remove-Item -LiteralPath $target -Force }
$zip = [System.IO.Compression.ZipFile]::Open($target, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  Get-ChildItem -LiteralPath $source -Recurse -File -Force | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($source, $_.FullName).Replace('\', '/')
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $relative) | Out-Null
  }
} finally {
  $zip.Dispose()
}
```

Expected: `.jsdos` file exists and includes `TIM.EXE` plus `.jsdos/dosbox.conf`.

- [ ] **Step 2: Compress MasterApp package**

Run:

```powershell
Compress-Archive -Path 'artifacts\packages\the-incredible-machine-1.0.0\*' -DestinationPath 'artifacts\packages\TheIncredibleMachine-1.0.0.zip' -Force
```

Expected: package ZIP has root `app.manifest.json`.

- [ ] **Step 3: Copy package to Incoming**

Run:

```powershell
$settings = Get-Content -Raw "$env:LOCALAPPDATA\MasterApp\State\settings.json" | ConvertFrom-Json
Copy-Item -LiteralPath 'artifacts\packages\TheIncredibleMachine-1.0.0.zip' -Destination $settings.incomingFolder -Force
```

Expected: MasterApp package watcher installs it.

### Task 5: Verify

**Files:**
- Read: `%LOCALAPPDATA%\MasterApp\State\runtime-state.json`

- [ ] **Step 1: Validate package ZIP**

Run:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path 'artifacts\packages\TheIncredibleMachine-1.0.0.zip'))
$entries = $zip.Entries.FullName
$zip.Dispose()
$entries -contains 'app.manifest.json'
```

Expected: `True`.

- [ ] **Step 2: Verify MasterApp API**

Run:

```powershell
Invoke-RestMethod 'http://127.0.0.1:19057/api/apps' | ConvertTo-Json -Depth 6
```

Expected: response includes `the-incredible-machine`.

- [ ] **Step 3: Verify app page**

Run:

```powershell
Invoke-WebRequest 'http://127.0.0.1:19057/apps/the-incredible-machine/' -UseBasicParsing
```

Expected: HTTP 200 and launcher HTML.
