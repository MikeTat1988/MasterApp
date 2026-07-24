namespace MasterApp.LifeJournal;

public sealed class LifePhoto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public DateTimeOffset LocalTimestamp { get; set; } = DateTimeOffset.Now;
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/jpeg";
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class LifeEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public DateTimeOffset LocalTimestamp { get; set; } = DateTimeOffset.Now;
}

public sealed class LifeDay
{
    public string Date { get; set; } = string.Empty;
    public List<LifePhoto> Photos { get; set; } = new();
    public List<LifeEvent> Events { get; set; } = new();
    public string? AnalysisMarkdown { get; set; }
    public string? AnalysisJson { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? GeneratedSummary { get; set; }
    public string? ShortSummary { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class LifeJournalSettings
{
    public int MaxImagesForAnalysis { get; set; } = 16;
    public int AnalysisTimeoutSeconds { get; set; } = 240;
    public int AutoFinalizeHourLocal { get; set; } = 3;
    public int PhotoMaxWidth { get; set; } = 900;
    public int JpegQuality { get; set; } = 75;
}

public sealed class LifeAnalysisOutput
{
    public string Date { get; set; } = string.Empty;
    public string ShortSummary { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;
    public string RawOutput { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<string> Uncertainties { get; set; } = new();
    public bool UsedFallback { get; set; }
    public string? Error { get; set; }
}

public sealed class LifeHistoryItem
{
    public string Date { get; set; } = string.Empty;
    public int PhotoCount { get; set; }
    public int EventCount { get; set; }
    public bool HasAnalysis { get; set; }
    public string? ShortSummary { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed class LifeAnalyzeRequest
{
    public string? Date { get; set; }
}

internal sealed class LifeJournalFinalizerState
{
    public string? LastCheckedDate { get; set; }
    public string? LastFinalizedDate { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
}
