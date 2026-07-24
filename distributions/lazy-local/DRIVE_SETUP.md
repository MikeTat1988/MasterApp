# Drive Setup

`Setup Lazy MasterApp.bat` creates a `MasterApp` folder with these subfolders:

- `Incoming`
- `Processed`
- `Failed`
- `Published`

If Google Drive for Desktop is available, setup prefers a `My Drive\MasterApp` folder on that drive. If it cannot find Google Drive, it falls back to `Documents\MasterApp`.

Use `Incoming` for new app ZIPs. MasterApp moves successful installs to `Processed`, failed installs to `Failed`, and exported builds to `Published`.
