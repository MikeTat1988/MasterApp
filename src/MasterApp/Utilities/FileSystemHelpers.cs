using System.IO.Compression;

namespace MasterApp.Utilities;

public static class FileSystemHelpers
{
    public static void CopyDirectory(string sourceDirectory, string destinationDirectory, bool overwrite = true)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    public static void MergeDirectory(string sourceDirectory, string destinationDirectory, bool overwrite = true)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        CopyDirectory(sourceDirectory, destinationDirectory, overwrite);
    }

    public static string CreateZipFromPath(string sourcePath, string destinationZipPath)
    {
        if (File.Exists(destinationZipPath))
        {
            File.Delete(destinationZipPath);
        }

        if (File.Exists(sourcePath))
        {
            var stagingRoot = Path.Combine(Path.GetTempPath(), $"masterapp_zip_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingRoot);
            try
            {
                var stagedFile = Path.Combine(stagingRoot, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, stagedFile, overwrite: true);
                ZipFile.CreateFromDirectory(stagingRoot, destinationZipPath);
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }

            return destinationZipPath;
        }

        ZipFile.CreateFromDirectory(sourcePath, destinationZipPath);
        return destinationZipPath;
    }
}
