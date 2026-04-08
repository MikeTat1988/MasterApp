# MasterApp LLM App Authoring Guide

Use this document as the exact packaging contract when asking an LLM to create an app for MasterApp.

## Goal

Produce a Windows app package that MasterApp can install from a single `.zip` dropped into the configured incoming folder.

MasterApp requires:

- Exactly one `app.manifest.json` at the package root.
- All manifest paths to be relative.
- A valid `id`, `name`, `version`, and `appType`.
- A package layout that matches the declared app type.

## Supported package types

### 1. `static`

Use this for pure HTML/CSS/JavaScript apps.

Required structure:

```text
package-root/
  app.manifest.json
  wwwroot/
    index.html
    ...
```

Required manifest rules:

- `appType` must be `static`.
- `entry` must point to a file inside `wwwroot`.
- `launch.kind` should be `static`.

Best when:

- The app is fully client-side.
- No local executable is needed.
- No local filesystem or background Windows integration is needed.

### 2. `portable`

Use this when the app is already compiled and ready to run.

Required structure:

```text
package-root/
  app.manifest.json
  dist/
    MyApp.exe
    ...
```

Required manifest rules:

- `appType` must be `portable`.
- `launch.kind` must be `webApp`.
- `launch.executablePath` must point to the packaged EXE.
- The app must answer the health endpoint declared by `launch.healthPath`.

Best when:

- You already have a built EXE.
- You want the fastest install experience.
- You want to avoid building during install.

### 3. `source`

Use this when the ZIP contains source code and MasterApp should build it during install.

Required structure:

```text
package-root/
  app.manifest.json
  src-or-project-files...
```

Required manifest rules:

- `appType` must be `source`.
- `build.installCommand` must fully build or publish the runnable app.
- `launch.executablePath` must point to the built EXE produced by the install command.
- The app must answer the health endpoint declared by `launch.healthPath`.

Best when:

- The app is authored in C# and should be compiled during install.
- You want the package to stay editable and reproducible.
- You also want optional publish support later.

## Important runtime rules for EXE-based apps

For `portable` and `source` apps, the app itself must cooperate with MasterApp.

The app should:

- Run an HTTP server on the port provided by MasterApp.
- Expose a health endpoint such as `/api/health`.
- Keep serving until MasterApp stops the process.
- Avoid choosing a random port at runtime unless it can read the port from an environment variable or argument.
- Avoid relying on absolute paths.
- Keep writable user content in declared `dataDirectories`.

Recommended environment variable support:

- `MASTERAPP_PORT`: the port the app must bind to.
- `ASPNETCORE_URLS`: MasterApp also sets this for ASP.NET-based apps.
- Optional hosted-mode flag such as `MASTERAPP_HOSTED=1` if you want to suppress standalone behaviors like auto-opening a browser.

## How MasterApp installs packages

MasterApp will:

1. Detect a `.zip` in the configured incoming folder.
2. Extract the ZIP to a temp area.
3. Find and validate exactly one `app.manifest.json`.
4. For `source` apps, run `build.installCommand`.
5. Install the app under `%LocalAppData%\\MasterApp\\Apps\\<appId>\\<version>\\`.
6. Validate `wwwroot` plus `entry` for `static` apps, or validate `launch.executablePath` for runnable apps.
7. Preserve declared `dataDirectories` across upgrades.
8. Move the source ZIP into the processed folder.

## Manifest template

```json
{
  "schemaVersion": "2",
  "id": "my-app",
  "name": "My App",
  "version": "1.0.0",
  "appType": "static",
  "entry": "index.html",
  "launch": {
    "kind": "static"
  },
  "display": {
    "shortName": "MyApp",
    "storeVisible": true,
    "showInLibrary": true
  }
}
```

Runnable app template:

```json
{
  "schemaVersion": "2",
  "id": "my-app",
  "name": "My App",
  "version": "1.0.0",
  "appType": "portable",
  "launch": {
    "kind": "webApp",
    "executablePath": "dist/MyApp.exe",
    "workingDirectory": "dist",
    "arguments": [],
    "environmentVariables": {},
    "urlTemplate": "http://127.0.0.1:{port}/",
    "healthPath": "/api/health",
    "startupTimeoutSeconds": 30
  },
  "dataDirectories": [
    "dist/data"
  ],
  "display": {
    "shortName": "MyApp",
    "storeVisible": true,
    "showInLibrary": true
  }
}
```

## Decision the user should make first

Before asking the LLM to build the app, decide this:

### Option A: HTML-based app

Choose this if the app can live entirely in browser code.

Pros:

- Simplest package shape.
- No build-on-install dependency on .NET.
- Easiest to test and iterate.

Tradeoffs:

- No native Windows EXE.
- No tray icon or richer Windows-only integration.
- Local file access and OS integration are more limited.

### Option B: EXE-based app compiled from C#

Choose this if the app needs a local ASP.NET server, tray icon, filesystem access, device integration, or Windows-native behavior.

Pros:

- Strong Windows integration.
- Can host a local web UI behind MasterApp.
- Works well for local tools and richer desktop behaviors.

Tradeoffs:

- More moving parts.
- Must compile cleanly.
- Must honor the `MASTERAPP_PORT` assigned by MasterApp and expose a health endpoint.

## Exact instructions to give an LLM

Tell the LLM:

- Build a MasterApp-compatible package, not just an app.
- Put `app.manifest.json` at the ZIP root.
- Use only relative paths in the manifest.
- If the app is HTML-only, package it as `static` with a `wwwroot` folder.
- If the app is C#-based, package it as `portable` or `source` and make sure it binds to `MASTERAPP_PORT`.
- Add a health endpoint.
- Declare any persistent folders in `dataDirectories`.
- Produce a final ZIP that can be dropped into MasterApp incoming folder as-is.

## Short prompt for generating a test app

Use this prompt with an LLM:

```text
Create a very small MasterApp-compatible test app package. Follow the MasterApp packaging rules in this document exactly. First decide whether the app should be HTML-based (`static`) or C# EXE-based (`portable` or `source`), and explain that choice briefly. Then generate the full package structure, a valid app.manifest.json at the ZIP root, and all required app files so the result can be zipped and dropped directly into MasterApp's incoming folder without manual fixes. If you choose an EXE-based app, make it bind to the port from MASTERAPP_PORT and expose /api/health.
```

## Short one-line ask

If you just want a compact request, use:

```text
Build me a MasterApp-ready test app package, with a valid root app.manifest.json and an inbox-ready ZIP, following the attached MasterApp authoring guide exactly.
```
