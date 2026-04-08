# Security Notes

This repository is intended to be shared without machine-specific secrets, runtime state, or packaged binaries.

## Do not commit
- Real Cloudflare tunnel tokens
- Machine-specific paths with sensitive user information
- `%LOCALAPPDATA%\MasterApp\State\secrets.json`
- `%LOCALAPPDATA%\MasterApp\State\settings.json`
- `%LOCALAPPDATA%\MasterApp\State\runtime-state.json`
- `%LOCALAPPDATA%\MasterApp\Logs\*`
- Any generated `.zip`, `.exe`, or published artifacts

## Safe placeholders
Placeholder templates live in `templates/state/`:
- `settings.example.json`
- `secrets.example.json`
- `runtime-state.example.json`

The application already auto-creates placeholder `settings.json` and `secrets.json` on first run. For a manual setup, run:

```bat
scripts\init-local-state.bat
```

## Before publishing the repo
1. Run `scripts\make-source-zip.bat` and inspect the archive contents.
2. Confirm there are no real tokens, hostnames, private paths, or customer data in docs or examples.
3. Share only placeholder files and sample manifests.
