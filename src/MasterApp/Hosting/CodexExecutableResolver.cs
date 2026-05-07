using System.Diagnostics;

namespace MasterApp.Hosting;

public sealed record CodexExecutableResolution
{
    public string Status { get; init; } = "idle";
    public bool IsReady => string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrWhiteSpace(ResolvedExecutablePath);
    public string? ResolvedExecutablePath { get; init; }
    public IReadOnlyList<string> AttemptedPaths { get; init; } = Array.Empty<string>();
    public string? LastError { get; init; }
    public DateTimeOffset ResolvedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public static class CodexExecutableResolver
{
    private const string DefaultCommand = "codex";
    private const string StorePackagePrefix = "OpenAI.Codex_";
    private const string StorePackageId = "OpenAI.Codex_2p2nqsd0c76g0";

    public static CodexExecutableResolution Resolve(
        string? configuredCommand,
        string? userProfile = null,
        IEnumerable<string>? commandPathMatches = null)
    {
        var attempted = new List<string>();
        var requested = configuredCommand?.Trim();
        var profile = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile.Trim();
        var localAppData = GetLocalAppData(profile);

        string? resolvedPath = null;
        var isDefaultCodexCommand = IsDefaultCodexCommand(requested);
        var configuredLooksLikePath = !string.IsNullOrWhiteSpace(requested) &&
                                      (Path.IsPathRooted(requested) ||
                                       requested.Contains(Path.DirectorySeparatorChar) ||
                                       requested.Contains(Path.AltDirectorySeparatorChar));
        var configuredIsWindowsApps = !string.IsNullOrWhiteSpace(requested) && IsWindowsAppsPath(requested);
        var allowBuiltInCodexFallback = string.IsNullOrWhiteSpace(requested) ||
                                        isDefaultCodexCommand ||
                                        configuredIsWindowsApps;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            TryUseCandidate(requested, attempted, ref resolvedPath);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return Ready(resolvedPath, attempted);
            }

            if (configuredLooksLikePath && !Path.IsPathRooted(requested))
            {
                TryUseCandidate(Path.GetFullPath(requested), attempted, ref resolvedPath);
                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    return Ready(resolvedPath, attempted);
                }
            }
        }

        var pathMatches = configuredLooksLikePath && !configuredIsWindowsApps
            ? Array.Empty<string>()
            : (commandPathMatches?.ToArray() ?? FindCommandOnPath(string.IsNullOrWhiteSpace(requested) ? DefaultCommand : requested!));
        foreach (var match in pathMatches)
        {
            AddAttempt(attempted, match);
        }

        if (allowBuiltInCodexFallback)
        {
            foreach (var candidate in GetBuiltInCodexCandidates(profile, localAppData))
            {
                TryUseCandidate(candidate, attempted, ref resolvedPath);
                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    return Ready(resolvedPath, attempted);
                }
            }
        }

        foreach (var match in pathMatches)
        {
            TryUseCandidate(match, attempted, ref resolvedPath);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return Ready(resolvedPath, attempted);
            }
        }

        return new CodexExecutableResolution
        {
            Status = "missing",
            ResolvedExecutablePath = null,
            AttemptedPaths = DistinctAttempts(attempted),
            LastError = BuildResolutionError(attempted),
            ResolvedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public static bool IsWindowsAppsPath(string path)
    {
        return path.Contains($"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{Path.AltDirectorySeparatorChar}WindowsApps{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultCodexCommand(string? command)
    {
        return string.Equals(command, DefaultCommand, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(command, $"{DefaultCommand}.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryUseCandidate(string candidate, List<string> attempted, ref string? resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var normalized = candidate.Trim();
        AddAttempt(attempted, normalized);

        if (IsWindowsAppsPath(normalized))
        {
            return;
        }

        if (File.Exists(normalized))
        {
            resolvedPath = normalized;
        }
    }

    private static IEnumerable<string> GetBuiltInCodexCandidates(string userProfile, string localAppData)
    {
        var packageRoot = Path.Combine(localAppData, "Packages");
        yield return Path.Combine(packageRoot, StorePackageId, "LocalCache", "Local", "OpenAI", "Codex", "bin", "codex.exe");

        if (Directory.Exists(packageRoot))
        {
            string[] packageDirectories;
            try
            {
                packageDirectories = Directory.GetDirectories(packageRoot, $"{StorePackagePrefix}*");
            }
            catch
            {
                packageDirectories = Array.Empty<string>();
            }

            foreach (var packageDirectory in packageDirectories.OrderByDescending(GetDirectoryWriteTimeUtc))
            {
                yield return Path.Combine(packageDirectory, "LocalCache", "Local", "OpenAI", "Codex", "bin", "codex.exe");
            }
        }

        yield return Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe");
    }

    private static DateTime GetDirectoryWriteTimeUtc(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string GetLocalAppData(string userProfile)
    {
        var currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(Path.GetFullPath(userProfile), Path.GetFullPath(currentProfile), StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return localAppData;
            }
        }

        return Path.Combine(userProfile, "AppData", "Local");
    }

    private static IReadOnlyList<string> FindCommandOnPath(string commandName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add(commandName);
            if (!process.Start())
            {
                return Array.Empty<string>();
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore cleanup failures after a short resolver probe.
                }

                return Array.Empty<string>();
            }

            return (stdout + Environment.NewLine + stderr)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AddAttempt(List<string> attempted, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            !attempted.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            attempted.Add(path);
        }
    }

    private static IReadOnlyList<string> DistinctAttempts(List<string> attempted)
    {
        return attempted
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CodexExecutableResolution Ready(string resolvedPath, List<string> attempted)
    {
        return new CodexExecutableResolution
        {
            Status = "ready",
            ResolvedExecutablePath = resolvedPath,
            AttemptedPaths = DistinctAttempts(attempted),
            LastError = null,
            ResolvedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string BuildResolutionError(IReadOnlyList<string> attemptedPaths)
    {
        if (attemptedPaths.Count == 0)
        {
            return "Codex executable was not found.";
        }

        return "Codex executable was not found or only WindowsApps aliases were available. Tried: " +
               string.Join(" | ", attemptedPaths.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
