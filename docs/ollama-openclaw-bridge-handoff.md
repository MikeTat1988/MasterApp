# Ollama and OpenClaw Bridge Handoff

## Purpose

This document carries the project context needed to continue the Ollama/OpenClaw integration from either development computer without rediscovering the intended architecture.

The integration must be delivered as a separate MasterApp-compatible app package. Do not add Ollama or OpenClaw client code back into the MasterApp host unless a later design decision explicitly requires a host capability that cannot be implemented by an installed app.

## Machine roles

- **MasterApp computer**: runs the MasterApp tray host, serves the phone-facing UI, starts the bridge package, and proxies it under `/apps/ollama-openclaw-bridge/`.
- **AI computer**: runs Ollama and OpenClaw and performs model inference and agent work.
- The normal path between the computers is the trusted home LAN.
- Cloudflare, when enabled, exposes MasterApp. It must not expose the Ollama API directly.

The computers may be the same machine during development. Keep the remote endpoint configurable so the package works in both same-machine and two-machine layouts.

## Intended package boundary

Create a separate package with an ID such as `ollama-openclaw-bridge`.

The bridge should:

- provide a phone-friendly web UI;
- bind its local server to `MASTERAPP_PORT`;
- expose `/api/health`;
- work correctly behind the MasterApp `/apps/<appId>/` proxy prefix;
- connect to a configurable Ollama/OpenClaw endpoint on the AI computer;
- keep non-secret configuration in a declared persistent `dataDirectories` folder;
- keep credentials outside source control and avoid logging them;
- report useful connection health without returning sensitive OpenClaw workspace or conversation data.

Use a `source` package while the bridge is being developed and a `portable` package for the final inbox-ready artifact when practical. Follow `docs/app-package-spec.md` and `docs/llm-app-authoring-guide.md`.

## Network and security boundary

- Never publish Ollama port `11434` directly to the public Internet.
- Never forward the OpenClaw Gateway directly through the MasterApp Cloudflare hostname without its supported authentication and pairing controls.
- Prefer an authenticated OpenClaw Gateway/node connection where it covers the use case.
- If the bridge must call Ollama directly across the LAN, configure Ollama to listen on the LAN and restrict inbound access with the AI computer's firewall to the MasterApp computer's IP.
- For OpenClaw-to-Ollama configuration, use the Ollama native base URL such as `http://<ai-host>:11434`, without `/v1`.
- Treat prompts, responses, agent sessions, OpenClaw workspace files, API keys, pairing tokens, and tunnel credentials as private data.

## What Git contains

The repository should contain:

- MasterApp source and package contract;
- bridge source once implementation begins;
- manifest and build/publish instructions;
- redacted configuration examples;
- automated tests and non-secret diagnostics;
- this handoff document.

Git must not contain real Cloudflare tokens, OpenClaw credentials, Ollama cloud keys, pairing tokens, or copied private agent workspaces.

## Machine-local MasterApp state

MasterApp creates its machine-local state under:

```text
%LOCALAPPDATA%\MasterApp\State\
  settings.json
  secrets.json
  runtime-state.json
```

On a new development computer:

1. Run `scripts\setup-local-state.bat`.
2. Set the working folders in `settings.json`.
3. For public access, install `cloudflared` and enter that computer/account's tunnel values in `secrets.json`.
4. Do not copy or commit `secrets.json` merely to make another checkout work.

Each computer may use its own Cloudflare tunnel. If both computers must serve the same public hostname, coordinate the tunnel configuration deliberately; do not run two independently configured hosts against that hostname by accident.

## Run and verify MasterApp

From the repository root:

```bat
scripts\setup-local-state.bat
scripts\run-masterapp.bat
```

Build checks:

```powershell
dotnet build .\src\MasterApp\MasterApp.csproj -c Debug
dotnet run --project .\tests\PolicyChecks\PolicyChecks.csproj
```

Create the normal self-contained release with:

```bat
scripts\build-release.bat
```

Use the local Wi-Fi/lazy-local setup when Cloudflare is unnecessary. Use standard mode only when public Cloudflare access is intended.

## AI-computer inventory handoff

Before implementation, inspect the AI computer and record a redacted snapshot containing:

- Windows version and stable LAN hostname/IP strategy;
- Ollama version, service/startup mode, bind address, installed model names, and a successful `/api/tags` test;
- OpenClaw version, Gateway status, bind address, port, authentication/pairing mode, and whether it already uses Ollama;
- required firewall rules described without secrets;
- exact start, stop, health-check, and rollback commands;
- any constraints discovered on that installation.

Suggested files:

```text
handoff/
  HANDOFF.md
  SYSTEM_INVENTORY.json
  SECURITY_NOTES.md
  REDACTED_CONFIG_EXAMPLES.md
```

Do not include secrets or private conversation/workspace contents in these files.

## Cross-computer development workflow

1. Finish a coherent change on one computer.
2. Run the relevant build and tests.
3. Commit and push the source and redacted documentation.
4. On the other computer, start from a clean checkout and pull the same branch.
5. Recreate machine-local state and credentials locally.
6. Never use Git to overwrite an uncommitted working tree on either computer.

An uncommitted working tree is not transferable through the main repository. Before switching computers, confirm `git status`, the current branch, and that the intended commits exist on the remote.

## First instruction for an agent on either computer

```text
Read README.md, masterapp.ai.json, docs/app-package-spec.md,
docs/llm-app-authoring-guide.md, and
docs/ollama-openclaw-bridge-handoff.md before making changes.

Keep the Ollama/OpenClaw integration as a separate MasterApp package. Do not
restore the removed built-in Ollama/Codex implementation and do not modify
MasterApp core unless you first demonstrate that the package boundary cannot
satisfy a specific requirement. Inspect the local machine configuration without
copying secrets into Git, summarize your plan, then implement only the scoped
package changes.
```
