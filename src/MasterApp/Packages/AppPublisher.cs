using MasterApp.Bootstrap;
using MasterApp.Models;
using MasterApp.Utilities;

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
            FileSystemHelpers.CreateZipFromPath(finalOutputPath, zipPath);
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
