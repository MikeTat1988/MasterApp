# MasterApp

MasterApp is a Windows tray app that watches an inbox folder for app packages, installs them locally, serves them through a local web host, and can publish shareable builds to a synced folder such as Google Drive.

## Clean project layout

- `src/MasterApp/` - the .NET 8 Windows application
- `scripts/` - the only maintenance scripts you should need
- `templates/state/` - starter `settings.json`, `secrets.json`, and `runtime-state.json`
- `docs/` - package format and LLM authoring references
- `sample-package/` - a minimal sample package for testing

Generated files are kept out of the repository. Release builds and share zips are written to `artifacts/` when you run the scripts.

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK
- `cloudflared`
- Google Drive for Desktop if you want published builds to sync through Google Drive

## First-time setup

1. Clone the repository.
2. Run `scripts\setup-local-state.bat`.
3. Open `%LOCALAPPDATA%\MasterApp\State\settings.json`.
4. Set `cloudflaredPath` to your local `cloudflared.exe`.
5. Choose your working folders for `incomingFolder`, `processedFolder`, `failedFolder`, and `publishedFolder`.
6. Open `%LOCALAPPDATA%\MasterApp\State\secrets.json`.
7. Add your own Cloudflare tunnel token, hostname, and local port.

## Cloudflare setup for another user

Every user should use their own Cloudflare account and tunnel.

1. Install `cloudflared` on that machine.
2. Create a tunnel in that user’s Cloudflare account.
3. Create a public hostname for the tunnel.
4. Copy the tunnel token into `%LOCALAPPDATA%\MasterApp\State\secrets.json`.
5. Put the matching hostname into `publicHostname`.

MasterApp does not ship account-specific Cloudflare credentials in this repository.

## Google Drive folder sync

If you want published builds to land in Google Drive, point the folder paths in `settings.json` to a Google Drive for Desktop folder, for example:

```json
{
  "incomingFolder": "G:\\My Drive\\MasterApp\\Incoming",
  "processedFolder": "G:\\My Drive\\MasterApp\\Processed",
  "failedFolder": "G:\\My Drive\\MasterApp\\Failed",
  "publishedFolder": "G:\\My Drive\\MasterApp\\Published"
}
```

That way:

- new package zips can be dropped into the synced `Incoming` folder
- processed and failed packages are easy to review
- published builds are automatically shared through the synced `Published` folder

## Scripts

- `scripts\run-masterapp.bat` - run the app from source
- `scripts\build-release.bat` - create a self-contained release build in `artifacts\release`
- `scripts\setup-local-state.bat` - create the local state files from templates
- `scripts\create-share-zip.bat` - build a clean source zip for another developer
- `scripts\collect-logs.bat` - create `Desktop\MasterApp_Logs.zip` without exposing `secrets.json`

## Share the project

Run:

```bat
scripts\create-share-zip.bat
```

This produces `artifacts\share\MasterApp-source.zip` with the source code, scripts, templates, sample package, and docs, but without local build outputs, temporary files, archives, or machine-specific state.

## Build and run

Run from source:

```bat
scripts\run-masterapp.bat
```

Create a release build:

```bat
scripts\build-release.bat
```

## Documentation

- `docs/app-package-spec.md`
- `docs/llm-app-authoring-guide.md`

## Notes

- Local runtime data lives in `%LOCALAPPDATA%\MasterApp\`
- The repository intentionally excludes build output and local state
- The sample package can be used as a quick install test once you zip it yourself
