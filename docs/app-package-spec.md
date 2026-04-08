# MasterApp App Package Spec

MasterApp now supports three package types:
- `static`: a `wwwroot` site served directly by MasterApp.
- `portable`: a prebuilt app folder or executable that MasterApp can launch and proxy.
- `source`: a source package that builds during install and can also publish a shareable artifact later.

## Required files
- `app.manifest.json` at package root.
- For `static` apps: a `wwwroot` folder and an `entry` file inside it.
- For `portable` and `source` apps: a runnable `launch.executablePath`.

## Install layout
Installed apps are stored under:
- `%LocalAppData%\MasterApp\Apps\<appId>\<version>\`

MasterApp keeps only the newest installed version by default and mirrors declared persistent data directories from:
- `%LocalAppData%\MasterApp\Apps\<appId>\_shared\`

## Manifest fields
Minimal shape:
```json
{
  "schemaVersion": "2",
  "id": "my-app",
  "name": "My App",
  "version": "1.0.0",
  "appType": "static|portable|source",
  "entry": "index.html",
  "icon": "optional/relative/path.png",
  "launch": {
    "kind": "static|webApp",
    "executablePath": "relative/path/to/app.exe",
    "workingDirectory": "optional/relative/path",
    "arguments": [],
    "environmentVariables": {},
    "port": 5057,
    "urlTemplate": "http://127.0.0.1:{port}/",
    "healthPath": "/",
    "startupTimeoutSeconds": 20
  },
  "build": {
    "installCommand": "optional command for source apps",
    "workingDirectory": "."
  },
  "publish": {
    "command": "optional publish command",
    "workingDirectory": ".",
    "outputPath": "relative/path/to/publish/output",
    "preferSingleFile": true,
    "createZip": true
  },
  "dataDirectories": ["relative/data/path"],
  "display": {
    "shortName": "App",
    "storeVisible": true,
    "showInLibrary": true
  },
  "pwa": {
    "name": "My App",
    "shortName": "App",
    "display": "standalone",
    "backgroundColor": "#111723",
    "themeColor": "#4f7cff"
  }
}
```

## Install behavior
1. MasterApp copies the zip into a temp folder.
2. It extracts and validates `app.manifest.json`.
3. For `source` apps, it runs `build.installCommand`.
4. It installs the package into the app version folder.
5. It validates `entry` for static apps or `launch.executablePath` for runnable apps.
6. It preserves declared `dataDirectories` across upgrades.
7. It moves the source zip into the configured `Processed` folder.

## Publish behavior
- Publish is separate from install.
- Publish is enabled only when the manifest has a `publish` block with both `command` and `outputPath`.
- Published artifacts are copied to the configured `Published` folder under:
  - `Published\<appId>\<version>\`
- If `createZip` is true, MasterApp also creates a zip beside the published output for easy sharing.

## Phone and Store mode
- Apps open from MasterApp at `/apps/<appId>/`.
- Static apps are served directly.
- Runnable apps are started on demand and proxied through MasterApp.
- iPhone home-screen support is web-based: open the app through the HTTPS tunnel in Safari and use Add to Home Screen.
- MasterApp does not create native iOS app packages.

## Recommendations for app authors and LLMs
- Make runnable apps bind to `MASTERAPP_PORT` or `ASPNETCORE_URLS` instead of hard-coding their own ports.
- Treat `launch.port` as an optional preferred port only. MasterApp may assign a different free port at runtime.
- Keep all manifest paths relative.
- Do not rely on guessing build commands; declare them in the manifest.
- Add an icon for any app intended for Store mode.
- Add a `publish` block for apps that should produce a shareable self-contained `.exe` or portable folder.
- Keep user data in declared `dataDirectories` so upgrades do not wipe it.

## Sample manifests
See:
- `sample-package/app.manifest.json`
- `sample-package/portable-app.manifest.sample.json`
- `sample-package/source-app.manifest.sample.json`
