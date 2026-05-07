using MasterApp.Diagnostics;
using MasterApp.Models;
using System.Text.Json;

namespace MasterApp.Storage;

public sealed class WatchdogStateStore
{
    private readonly AppPaths _paths;
    private readonly FileLogManager _log;

    public WatchdogStateStore(AppPaths paths, FileLogManager log)
    {
        _paths = paths;
        _log = log;
    }

    public void MarkExplicitQuit()
    {
        Save(_paths.ShutdownIntentFile, new ShutdownIntentRecord
        {
            Reason = "quit",
            SetAtUtc = DateTimeOffset.UtcNow
        });
    }

    public void ClearShutdownIntent()
    {
        try
        {
            if (File.Exists(_paths.ShutdownIntentFile))
            {
                File.Delete(_paths.ShutdownIntentFile);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("WatchdogStateStore", $"Failed to clear shutdown intent: {ex.Message}");
        }
    }

    public void SaveWatchdogState(WatchdogStateRecord state)
    {
        Save(_paths.WatchdogStateFile, state);
    }

    private void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _paths.StateDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions.DefaultIndented));
        }
        catch (Exception ex)
        {
            _log.Warn("WatchdogStateStore", $"Failed to save state file '{path}': {ex.Message}");
        }
    }
}
