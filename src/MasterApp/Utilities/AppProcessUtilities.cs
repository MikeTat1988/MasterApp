using System.Diagnostics;

namespace MasterApp.Utilities;

public static class AppProcessUtilities
{
    public static IReadOnlyList<string> StopProcessesFromDirectory(
        string appId,
        string directory,
        Action<string> logInfo,
        Action<string> logWarning,
        int waitForExitMilliseconds = 10000)
    {
        var normalizedRoot = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var failures = new List<string>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? processPath;
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    processPath = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (!IsPathUnderDirectory(processPath, normalizedRoot))
                {
                    continue;
                }

                try
                {
                    logInfo($"Stopping process {process.Id} from app '{appId}': {processPath}");
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(waitForExitMilliseconds) || !process.HasExited)
                    {
                        throw new TimeoutException($"Process {process.Id} did not exit within {waitForExitMilliseconds} ms.");
                    }
                }
                catch (Exception ex)
                {
                    var message = $"Could not stop process {process.Id} for app '{appId}': {ex.Message}";
                    failures.Add(message);
                    logWarning(message);
                }
            }
        }

        return failures;
    }

    public static bool IsPathUnderDirectory(string? path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
