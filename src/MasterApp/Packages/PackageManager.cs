using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Storage;
using MasterApp.Utilities;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MasterApp.Packages;

public sealed class PackageManager
{
    private static readonly Regex IdRegex = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$", RegexOptions.Compiled);
    private static readonly Regex VersionRegex = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,49}$", RegexOptions.Compiled);

    private readonly BootstrapContext _context;

    public PackageManager(BootstrapContext context)
    {
        _context = context;
    }

    public OperationResult ScanIncoming(string reason)
    {
        try
        {
            Directory.CreateDirectory(_context.Settings.IncomingFolder);
            Directory.CreateDirectory(_context.Settings.ProcessedFolder);
            Directory.CreateDirectory(_context.Settings.FailedFolder);

            var now = DateTimeOffset.UtcNow;
            _context.RuntimeStateStore.MarkScan(now, reason);

            var zipFiles = Directory.GetFiles(_context.Settings.IncomingFolder, "*.zip", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _context.Log.Packages("PackageManager", $"Scan started. Reason={reason}. Zip count={zipFiles.Length}");

            if (zipFiles.Length == 0)
            {
                return OperationResult.Success("No zip packages found.");
            }

            foreach (var zipFile in zipFiles)
            {
                ProcessZip(zipFile);
            }

            return OperationResult.Success($"Processed {zipFiles.Length} zip package(s).");
        }
        catch (Exception ex)
        {
            _context.Log.Packages("PackageManager", $"Scan failed. Reason={reason}", ex);
            return OperationResult.Failure(ex.Message);
        }
    }

    private void ProcessZip(string zipFile)
    {
        var sourceName = Path.GetFileName(zipFile);
        _context.Log.Packages("PackageManager", $"Detected package: {sourceName}");

        var result = new PackageInstallResult
        {
            Success = false,
            SourceFileName = sourceName,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var tempRoot = Path.Combine(_context.Paths.TempDirectory, $"pkg_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        var copiedZip = Path.Combine(tempRoot, sourceName);
        var extractedFolder = Path.Combine(tempRoot, "expanded");

        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(extractedFolder);

            File.Copy(zipFile, copiedZip, overwrite: true);

            using (var archive = ZipFile.OpenRead(copiedZip))
            {
                ExtractArchiveSafely(archive, extractedFolder);
            }

            var packageRoot = LocatePackageRoot(extractedFolder);
            var manifest = LoadManifest(packageRoot);

            ValidateManifest(manifest);
            BuildSourcePackageIfNeeded(manifest, packageRoot);

            var installPath = Path.Combine(_context.Paths.AppsDirectory, manifest.Id, manifest.Version);
            if (Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
            Directory.Move(packageRoot, installPath);

            SyncPersistentData(manifest, installPath);
            ValidateInstalledLayout(manifest, installPath);
            CleanupOldVersions(manifest.Id, manifest.Version);

            var appState = new InstalledAppState
            {
                Id = manifest.Id,
                Name = manifest.Name,
                ActiveVersion = manifest.Version,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                Versions = new List<string> { manifest.Version },
                Manifest = manifest,
                RunState = new AppRunState
                {
                    Status = "installed",
                    Message = "App installed successfully."
                }
            };

            _context.RuntimeStateStore.UpsertInstalledApp(appState);

            var processedPath = MoveWithUniqueName(zipFile, _context.Settings.ProcessedFolder);
            _context.Log.Packages("PackageManager", $"Moved source zip to Processed: {processedPath}");

            result.Success = true;
            result.AppId = manifest.Id;
            result.Version = manifest.Version;
            result.Message = $"Installed {manifest.Id} {manifest.Version}";
            result.InstalledPath = installPath;
            _context.RuntimeStateStore.SetLastPackageResult(result);
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            _context.RuntimeStateStore.SetLastPackageResult(result);
            _context.Log.Packages("PackageManager", $"Install failed for {sourceName}: {ex.Message}", ex);

            try
            {
                var failedZip = MoveWithUniqueName(zipFile, _context.Settings.FailedFolder);
                File.WriteAllText(failedZip + ".error.txt", ex.ToString());
            }
            catch (Exception moveEx)
            {
                _context.Log.Packages("PackageManager", "Failed to move broken zip to Failed folder.", moveEx);
            }
        }
        finally
        {
            TryCleanupDirectory(tempRoot);
        }
    }

    private void BuildSourcePackageIfNeeded(AppManifest manifest, string packageRoot)
    {
        if (!string.Equals(manifest.AppType, AppTypes.Source, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var command = manifest.Build?.InstallCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: source app is missing build.installCommand.");
        }

        var workingDirectory = ResolveManifestPath(packageRoot, manifest.Build?.WorkingDirectory);
        _context.Log.Packages("PackageManager", $"Running install build for {manifest.Id}: {command}");
        var execution = CommandRunner.Run(command, workingDirectory);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException($"PACKAGE_BUILD_FAILED: {execution.StandardError}{execution.StandardOutput}");
        }
    }

    private static AppManifest LoadManifest(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "app.manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: app.manifest.json is missing.");
        }

        return JsonSerializer.Deserialize<AppManifest>(
                   File.ReadAllText(manifestPath),
                   JsonOptions.Default)
               ?? throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: app.manifest.json could not be parsed.");
    }

    private void ValidateInstalledLayout(AppManifest manifest, string installPath)
    {
        if (string.Equals(manifest.AppType, AppTypes.Static, StringComparison.OrdinalIgnoreCase))
        {
            var wwwrootPath = Path.Combine(installPath, "wwwroot");
            if (!Directory.Exists(wwwrootPath))
            {
                throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: wwwroot folder is missing.");
            }

            var entryFullPath = GetSafeChildPath(wwwrootPath, manifest.Entry);
            if (!File.Exists(entryFullPath))
            {
                throw new InvalidOperationException($"PACKAGE_ENTRY_NOT_FOUND: entry '{manifest.Entry}' was not found.");
            }
        }
        else
        {
            if (manifest.Launch.Kind != LaunchKinds.WebApp)
            {
                throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: runnable apps currently require launch.kind='webApp'.");
            }

            if (string.IsNullOrWhiteSpace(manifest.Launch.ExecutablePath))
            {
                throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: runnable apps require launch.executablePath.");
            }

            var executablePath = GetSafeChildPath(installPath, manifest.Launch.ExecutablePath);
            if (!File.Exists(executablePath))
            {
                throw new InvalidOperationException($"PACKAGE_LAUNCH_NOT_FOUND: {manifest.Launch.ExecutablePath}");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            var iconPath = GetSafeChildPath(installPath, manifest.Icon);
            if (!File.Exists(iconPath))
            {
                throw new InvalidOperationException($"PACKAGE_ICON_NOT_FOUND: {manifest.Icon}");
            }
        }
    }

    private void SyncPersistentData(AppManifest manifest, string installPath)
    {
        if (manifest.DataDirectories.Count == 0)
        {
            return;
        }

        var appRoot = Path.GetDirectoryName(installPath)!;
        var sharedRoot = Path.Combine(appRoot, "_shared");
        Directory.CreateDirectory(sharedRoot);

        foreach (var relative in manifest.DataDirectories)
        {
            var normalized = NormalizeRelativePath(relative, "dataDirectories");
            var sharedPath = Path.Combine(sharedRoot, normalized);
            var installedPath = Path.Combine(installPath, normalized);

            if (Directory.Exists(installedPath))
            {
                FileSystemHelpers.MergeDirectory(installedPath, sharedPath);
            }
            else
            {
                Directory.CreateDirectory(sharedPath);
            }

            FileSystemHelpers.MergeDirectory(sharedPath, installedPath);
        }
    }

    private void CleanupOldVersions(string appId, string keepVersion)
    {
        var appRoot = Path.Combine(_context.Paths.AppsDirectory, appId);
        if (!Directory.Exists(appRoot))
        {
            return;
        }

        foreach (var versionDirectory in Directory.GetDirectories(appRoot))
        {
            var versionName = Path.GetFileName(versionDirectory);
            if (string.Equals(versionName, keepVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(versionName, "_shared", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.Delete(versionDirectory, recursive: true);
        }
    }

    private static void ValidateManifest(AppManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdRegex.IsMatch(manifest.Id))
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: id is missing or contains invalid characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: name is missing.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || !VersionRegex.IsMatch(manifest.Version))
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: version is missing or contains invalid characters.");
        }

        if (!new[] { AppTypes.Static, AppTypes.Portable, AppTypes.Source }.Contains(manifest.AppType, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: appType must be static, portable, or source.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Entry))
        {
            NormalizeRelativePath(manifest.Entry, "entry");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            NormalizeRelativePath(manifest.Icon, "icon");
        }

        foreach (var path in manifest.DataDirectories)
        {
            NormalizeRelativePath(path, "dataDirectories");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Launch.ExecutablePath))
        {
            NormalizeRelativePath(manifest.Launch.ExecutablePath, "launch.executablePath");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Launch.WorkingDirectory))
        {
            NormalizeRelativePath(manifest.Launch.WorkingDirectory, "launch.workingDirectory");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Publish?.OutputPath))
        {
            NormalizeRelativePath(manifest.Publish.OutputPath, "publish.outputPath");
        }
    }

    private static string LocatePackageRoot(string extractedFolder)
    {
        var manifestPaths = Directory.GetFiles(extractedFolder, "app.manifest.json", SearchOption.AllDirectories);
        if (manifestPaths.Length == 0)
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: app.manifest.json is missing.");
        }

        if (manifestPaths.Length > 1)
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: multiple app.manifest.json files found.");
        }

        return Path.GetDirectoryName(manifestPaths[0])!;
    }

    private static void ExtractArchiveSafely(ZipArchive archive, string destinationFolder)
    {
        var rootFullPath = Path.GetFullPath(destinationFolder);

        foreach (var entry in archive.Entries)
        {
            var outputPath = Path.GetFullPath(Path.Combine(destinationFolder, entry.FullName));

            if (!outputPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"PACKAGE_INVALID_ARCHIVE: invalid path '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            entry.ExtractToFile(outputPath, overwrite: true);
        }
    }

    private static string MoveWithUniqueName(string sourceFilePath, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);

        var fileName = Path.GetFileName(sourceFilePath);
        var destinationPath = Path.Combine(destinationFolder, fileName);

        if (!File.Exists(destinationPath))
        {
            File.Move(sourceFilePath, destinationPath);
            return destinationPath;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var uniquePath = Path.Combine(destinationFolder, $"{name}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{extension}");
        File.Move(sourceFilePath, uniquePath);
        return uniquePath;
    }

    private static string ResolveManifestPath(string installRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return installRoot;
        }

        return GetSafeChildPath(installRoot, relativePath);
    }

    private static string GetSafeChildPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PACKAGE_INVALID_MANIFEST: path escaped package root.");
        }

        return combined;
    }

    private static string NormalizeRelativePath(string relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException($"PACKAGE_INVALID_MANIFEST: {label} is missing.");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"PACKAGE_INVALID_MANIFEST: {label} contains an invalid path.");
        }

        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    private void TryCleanupDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _context.Log.Packages("PackageManager", $"Temp cleanup failed: {directory}", ex);
        }
    }
}
