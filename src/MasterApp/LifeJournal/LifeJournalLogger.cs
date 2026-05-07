using System.Text;

namespace MasterApp.LifeJournal;

public sealed class LifeJournalLogger
{
    private readonly object _gate = new();

    public LifeJournalLogger(string lifeJournalRoot)
    {
        LogsDirectory = Path.Combine(lifeJournalRoot, "logs");
        Directory.CreateDirectory(LogsDirectory);
        LogFilePath = Path.Combine(LogsDirectory, "lifejournal.log");
    }

    public string LogsDirectory { get; }
    public string LogFilePath { get; }

    public void Info(string source, string message) => Write("INFO", source, message, null);
    public void Warn(string source, string message) => Write("WARN", source, message, null);
    public void Error(string source, string message, Exception? ex = null) => Write("ERROR", source, message, ex);

    private void Write(string level, string source, string message, Exception? ex)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] [{source}] {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + ex;
        }

        lock (_gate)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
