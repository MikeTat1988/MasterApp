# MasterApp Package Instructions For ChatGPT

Create a single install-ready `.zip` for MasterApp.

Rules:

1. Put exactly one `app.manifest.json` at the ZIP root.
2. Keep all paths relative to the ZIP root.
3. Choose one `appType`: `static`, `portable`, or `source`.
4. For `static`, include `wwwroot` and an entry file such as `index.html`.
5. For `portable` or `source`, include a runnable local web app and set `launch.kind` to `webApp`.
6. Runnable apps must read `MASTERAPP_PORT` or `ASPNETCORE_URLS`, bind to that port, and expose a health endpoint such as `/api/health`.
7. Include a real bitmap icon when the app should look good in the Store.
8. Use `dataDirectories` for writable data that should survive upgrades.
9. Include `masterapp.ai.json` with short maintenance hints when useful.
10. Return the ZIP as the final artifact.

Minimal static manifest:

```json
{
  "schemaVersion": "2",
  "id": "my-static-app",
  "name": "My Static App",
  "version": "1.0.0",
  "appType": "static",
  "entry": "index.html",
  "icon": "assets/icon.png",
  "store": {
    "visible": true,
    "sortOrder": 100
  }
}
```

Minimal runnable manifest:

```json
{
  "schemaVersion": "2",
  "id": "my-local-app",
  "name": "My Local App",
  "version": "1.0.0",
  "appType": "portable",
  "entry": "index.html",
  "icon": "assets/icon.png",
  "launch": {
    "kind": "webApp",
    "executablePath": "MyLocalApp.exe",
    "workingDirectory": ".",
    "arguments": [],
    "environmentVariables": {},
    "port": 5057,
    "urlTemplate": "http://127.0.0.1:{port}/",
    "healthPath": "/api/health",
    "startupTimeoutSeconds": 20
  },
  "store": {
    "visible": true,
    "sortOrder": 100
  }
}
```
