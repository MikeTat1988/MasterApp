using System.Text;

namespace MasterApp.Diagnostics;

public sealed class FileLogManager
{
    private readonly object _gate = new();
    private readonly string _logDirectory;

    public FileLogManager(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public string GetPath(LogKind kind) => Path.Combine(_logDirectory, $"{kind.ToString().ToLowerInvariant()}.log");

    public void Debug(string source, string message) => Write(LogKind.App, "DEBUG", source, message, null);
    public void Info(string source, string message) => Write(LogKind.App, "INFO", source, message, null);
    public void Warn(string source, string message) => Write(LogKind.App, "WARN", source, message, null);
    public void Error(string source, string message, Exception? ex = null) => Write(LogKind.App, "ERROR", source, message, ex);

    public void Tunnel(string source, string message, Exception? ex = null) => Write(LogKind.Tunnel, "INFO", source, message, ex);
    public void Packages(string source, string message, Exception? ex = null) => Write(LogKind.Packages, "INFO", source, message, ex);
    public void Ui(string source, string message, Exception? ex = null) => Write(LogKind.Ui, "INFO", source, message, ex);

    private void Write(LogKind kind, string level, string source, string message, Exception? ex)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] [{source}] {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + ex;
        }

        var path = GetPath(kind);

        lock (_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public IReadOnlyList<string> ReadTail(LogKind kind, int maxLines)
    {
        var path = GetPath(kind);
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length <= maxLines)
        {
            return lines;
        }

        return lines.Skip(lines.Length - maxLines).ToArray();
    }
}
