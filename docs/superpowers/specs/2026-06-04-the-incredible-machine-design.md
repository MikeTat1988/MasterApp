# The Incredible Machine MasterApp Design

## Goal

Make the local DOS copy of The Incredible Machine at `C:\Dev\incredible-machine` playable from MasterApp, including phone access through the normal `/apps/<appId>/` route.

## Approach

Build a standalone static MasterApp package named `the-incredible-machine`. The package contains the local game files, a local js-dos runtime, a `.jsdos` bundle, and a touch-oriented launcher page. MasterApp itself does not need product code changes because static apps are already served under `/apps/<appId>/`.

## Package Shape

- `app.manifest.json` declares a static MasterApp app.
- `masterapp.ai.json` gives future maintenance hints.
- `wwwroot/index.html`, `wwwroot/styles.css`, and `wwwroot/app.js` render the game surface.
- `wwwroot/game/the-incredible-machine.jsdos` contains `TIM.EXE`, `RESOURCE.*`, configs, readme files, and DOSBox startup config.
- `wwwroot/vendor/js-dos/` contains the downloaded js-dos runtime files required for local hosting.
- `assets/app-icon.png` provides the Store icon.

## Runtime Behavior

The launcher loads js-dos from the package-local vendor folder and starts `game/the-incredible-machine.jsdos`. The `.jsdos` bundle must use POSIX-style ZIP entry names such as `.jsdos/dosbox.conf`; js-dos treats `.jsdos\dosbox.conf` as missing. The UI is phone-first: full-screen emulator area, landscape-friendly layout, and a small command strip for keys mentioned by `READ.ME`: `Esc`, `Tab`, `Space`, `Enter`, `+`, and `-`.

## Verification

Verification checks:

- Package structure contains one root `app.manifest.json`.
- The `.jsdos` bundle contains `TIM.EXE`, game resources, and DOSBox config.
- The final ZIP is installable by MasterApp.
- MasterApp reports `the-incredible-machine` in `/api/apps` after the package is copied to the configured Incoming folder.
- The app page responds at `/apps/the-incredible-machine/`.
