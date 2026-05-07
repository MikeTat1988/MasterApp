using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Storage;
using MasterApp.Utilities;
using System.Text.Json;

namespace MasterApp.Packages;

public sealed class AppPublisher
{
    private readonly BootstrapContext _context;

    public AppPublisher(BootstrapContext context)
    {
        _context = context;
    }

    public PublishResult Publish(string appId)
    {
        var installed = _context.RuntimeStateStore.GetApp(appId)
                       ?? throw new InvalidOperationException($"APP_NOT_FOUND: {appId}");
        var manifest = installed.Manifest;
        var publish = manifest.Publish;
        if (publish is null || string.IsNullOrWhiteSpace(publish.Command) || string.IsNullOrWhiteSpace(publish.OutputPath))
        {
            throw new InvalidOperationException($"APP_PUBLISH_NOT_SUPPORTED: {appId}");
        }

        var installRoot = Path.Combine(_context.Paths.AppsDirectory, installed.Id, installed.ActiveVersion);
        var workingDirectory = ResolvePath(installRoot, publish.WorkingDirectory);

        _context.Log.Packages("AppPublisher", $"Publishing {appId} using command: {publish.Command}");
        var execution = CommandRunner.Run(publish.Command, workingDirectory);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException($"APP_PUBLISH_FAILED: {execution.StandardError}{execution.StandardOutput}");
        }

        var outputPath = ResolvePath(installRoot, publish.OutputPath);
        if (!File.Exists(outputPath) && !Directory.Exists(outputPath))
        {
            throw new InvalidOperationException($"APP_PUBLISH_OUTPUT_MISSING: {publish.OutputPath}");
        }

        var destinationRoot = Path.Combine(_context.Settings.PublishedFolder, installed.Id, installed.ActiveVersion);
        if (Directory.Exists(destinationRoot))
        {
            Directory.Delete(destinationRoot, recursive: true);
        }

        Directory.CreateDirectory(destinationRoot);

        string finalOutputPath;
        string artifactKind;

        if (File.Exists(outputPath))
        {
            finalOutputPath = Path.Combine(destinationRoot, Path.GetFileName(outputPath));
            File.Copy(outputPath, finalOutputPath, overwrite: true);
            artifactKind = "single-exe";
        }
        else
        {
            var folderName = Path.GetFileName(outputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            finalOutputPath = Path.Combine(destinationRoot, folderName);
            FileSystemHelpers.CopyDirectory(outputPath, finalOutputPath);
            artifactKind = "published-folder";
        }

        string? zipPath = null;
        if (publish.CreateZip)
        {
            zipPath = Path.Combine(destinationRoot, $"{installed.Id}-{installed.ActiveVersion}.zip");
            CreateInstallableZip(installed, installRoot, finalOutputPath, zipPath);
        }

        var artifact = new PublishArtifactInfo
        {
            ArtifactKind = artifactKind,
            OutputPath = finalOutputPath,
            ZipPath = zipPath
        };

        _context.RuntimeStateStore.UpdatePublishedArtifact(appId, artifact);

        var result = new PublishResult
        {
            Success = true,
            AppId = appId,
            Version = installed.ActiveVersion,
            Message = $"Published {installed.Name} to {destinationRoot}",
            Artifact = artifact,
            TimestampUtc = DateTimeOffset.UtcNow
        };
        _context.RuntimeStateStore.SetLastPublishResult(result);
        return result;
    }

    private void CreateInstallableZip(InstalledAppState installed, string installRoot, string publishedOutputPath, string destinationZipPath)
    {
        var stagingRoot = Path.Combine(_context.Paths.TempDirectory, $"publish_pkg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            StagePublishedPayload(publishedOutputPath, stagingRoot);

            var packagedManifest = BuildPublishedPackageManifest(installed, installRoot, publishedOutputPath, stagingRoot);
            var manifestPath = Path.Combine(stagingRoot, "app.manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(packagedManifest, JsonOptions.DefaultIndented));

            var aiHintsPath = Path.Combine(installRoot, "masterapp.ai.json");
            if (File.Exists(aiHintsPath))
            {
                File.Copy(aiHintsPath, Path.Combine(stagingRoot, "masterapp.ai.json"), overwrite: true);
            }

            FileSystemHelpers.CreateZipFromPath(stagingRoot, destinationZipPath);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static void StagePublishedPayload(string publishedOutputPath, string stagingRoot)
    {
        if (File.Exists(publishedOutputPath))
        {
            File.Copy(publishedOutputPath, Path.Combine(stagingRoot, Path.GetFileName(publishedOutputPath)), overwrite: true);
            return;
        }

        FileSystemHelpers.CopyDirectory(publishedOutputPath, stagingRoot);
    }

    private static AppManifest BuildPublishedPackageManifest(
        InstalledAppState installed,
        string installRoot,
        string publishedOutputPath,
        string stagingRoot)
    {
        var source = installed.Manifest;
        var packagedExecutable = ResolvePackagedExecutablePath(source, installRoot, publishedOutputPath, stagingRoot);
        var packagedIcon = RebaseIfContained(installRoot, publishedOutputPath, source.Icon);
        var packagedDataDirectories = source.DataDirectories
            .Select(path => RebaseIfContained(installRoot, publishedOutputPath, path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AppManifest
        {
            SchemaVersion = source.SchemaVersion,
            Id = source.Id,
            Name = source.Name,
            Version = source.Version,
            AppType = string.Equals(source.AppType, AppTypes.Static, StringComparison.OrdinalIgnoreCase)
                ? AppTypes.Static
                : AppTypes.Portable,
            Entry = source.Entry,
            Icon = packagedIcon,
            Launch = new AppLaunchManifest
            {
                Kind = source.Launch.Kind,
                ExecutablePath = packagedExecutable,
                WorkingDirectory = GetWorkingDirectory(packagedExecutable),
                Arguments = source.Launch.Arguments.ToList(),
                EnvironmentVariables = new Dictionary<string, string>(source.Launch.EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
                Port = source.Launch.Port,
                UrlTemplate = source.Launch.UrlTemplate,
                HealthPath = source.Launch.HealthPath,
                StartupTimeoutSeconds = source.Launch.StartupTimeoutSeconds
            },
            Build = null,
            Publish = null,
            DataDirectories = packagedDataDirectories,
            Display = new AppDisplayManifest
            {
                ShortName = source.Display.ShortName,
                StoreVisible = source.Display.StoreVisible,
                ShowInLibrary = source.Display.ShowInLibrary
            },
            Pwa = source.Pwa is null
                ? null
                : new AppPwaManifest
                {
                    Name = source.Pwa.Name,
                    ShortName = source.Pwa.ShortName,
                    Display = source.Pwa.Display,
                    BackgroundColor = source.Pwa.BackgroundColor,
                    ThemeColor = source.Pwa.ThemeColor
                }
        };
    }

    private static string ResolvePackagedExecutablePath(
        AppManifest source,
        string installRoot,
        string publishedOutputPath,
        string stagingRoot)
    {
        if (string.Equals(source.AppType, AppTypes.Static, StringComparison.OrdinalIgnoreCase))
        {
            return source.Entry;
        }

        var rebased = RebaseIfContained(installRoot, publishedOutputPath, source.Launch.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(rebased) && File.Exists(Path.Combine(stagingRoot, rebased.Replace('/', Path.DirectorySeparatorChar))))
        {
            return rebased;
        }

        var originalFileName = Path.GetFileName(source.Launch.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            var exactMatch = Directory.GetFiles(stagingRoot, originalFileName, SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(exactMatch))
            {
                return Path.GetRelativePath(stagingRoot, exactMatch).Replace('\\', '/');
            }
        }

        var anyExecutable = Directory.GetFiles(stagingRoot, "*.exe", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(stagingRoot, "*.dll", SearchOption.AllDirectories))
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(anyExecutable))
        {
            return Path.GetRelativePath(stagingRoot, anyExecutable).Replace('\\', '/');
        }

        throw new InvalidOperationException("APP_PUBLISH_OUTPUT_INVALID: published output does not contain a runnable executable.");
    }

    private static string? RebaseIfContained(string installRoot, string publishedOutputPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var sourceFullPath = Path.GetFullPath(Path.Combine(installRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var outputRoot = File.Exists(publishedOutputPath)
            ? Path.GetDirectoryName(Path.GetFullPath(publishedOutputPath))!
            : Path.GetFullPath(publishedOutputPath);

        if (!sourceFullPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetRelativePath(outputRoot, sourceFullPath).Replace('\\', '/');
    }

    private static string? GetWorkingDirectory(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(executablePath.Replace('/', Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(directory) ? "." : directory.Replace('\\', '/');
    }

    private static string ResolvePath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("APP_PATH_INVALID");
        }

        return candidate;
    }
}
