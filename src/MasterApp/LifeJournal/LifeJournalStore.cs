using MasterApp.Storage;
using System.Text.Json;

namespace MasterApp.LifeJournal;

public sealed class LifeJournalStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LifeJournalLogger _log;

    public LifeJournalStore(string rootDirectory, LifeJournalLogger log)
    {
        RootDirectory = rootDirectory;
        PhotosDirectory = Path.Combine(rootDirectory, "photos");
        DaysDirectory = Path.Combine(rootDirectory, "days");
        AnalysisDirectory = Path.Combine(rootDirectory, "analysis");
        AssetsDirectory = Path.Combine(rootDirectory, "assets");
        _log = log;

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(PhotosDirectory);
        Directory.CreateDirectory(DaysDirectory);
        Directory.CreateDirectory(AnalysisDirectory);
        Directory.CreateDirectory(AssetsDirectory);
    }

    public string RootDirectory { get; }
    public string PhotosDirectory { get; }
    public string DaysDirectory { get; }
    public string AnalysisDirectory { get; }
    public string AssetsDirectory { get; }

    public Task<LifeDay> LoadTodayAsync(DateTimeOffset localNow, CancellationToken cancellationToken)
    {
        return LoadDayAsync(DateOnly.FromDateTime(localNow.DateTime), cancellationToken);
    }

    public async Task<LifeDay> LoadDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return LoadDayUnsafe(date);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LifeDay> AddPhotoAsync(DateOnly date, LifePhoto photo, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var day = LoadDayUnsafe(date);
            day.Photos.RemoveAll(item => string.Equals(item.Id, photo.Id, StringComparison.OrdinalIgnoreCase));
            day.Photos.Add(photo);
            day.Photos = day.Photos
                .OrderBy(item => item.LocalTimestamp)
                .ToList();
            day.UpdatedAtUtc = DateTime.UtcNow;
            SaveDayUnsafe(day);
            _log.Info("LifeJournalStore", $"Photo metadata saved for {day.Date}: {photo.FileName}");
            return day;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LifeDay> AddEventAsync(DateOnly date, string eventType, DateTimeOffset localTimestamp, CancellationToken cancellationToken)
    {
        if (!IsAllowedEventType(eventType))
        {
            throw new InvalidOperationException("Unsupported LifeJournal event type.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var day = LoadDayUnsafe(date);
            day.Events.Add(new LifeEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = eventType,
                LocalTimestamp = localTimestamp,
                TimestampUtc = localTimestamp.UtcDateTime
            });
            day.Events = day.Events.OrderBy(item => item.LocalTimestamp).ToList();
            day.UpdatedAtUtc = DateTime.UtcNow;
            SaveDayUnsafe(day);
            _log.Info("LifeJournalStore", $"Event saved for {day.Date}: {eventType}");
            return day;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LifeDay> SaveAnalysisAsync(
        DateOnly date,
        LifeAnalysisOutput output,
        bool markFinalized,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var day = LoadDayUnsafe(date);
            var dateText = LifeJournalPathSafety.FormatDate(date);

            var markdownPath = Path.Combine(AnalysisDirectory, $"{dateText}.md");
            var rawPath = Path.Combine(AnalysisDirectory, $"{dateText}.raw.txt");
            var jsonPath = Path.Combine(AnalysisDirectory, $"{dateText}.analysis.json");

            await File.WriteAllTextAsync(markdownPath, output.Markdown ?? string.Empty, cancellationToken);
            await File.WriteAllTextAsync(rawPath, output.RawOutput ?? string.Empty, cancellationToken);
            await File.WriteAllTextAsync(jsonPath, output.Json ?? string.Empty, cancellationToken);

            day.AnalysisMarkdown = output.Markdown;
            day.AnalysisJson = output.Json;
            day.GeneratedSummary = string.IsNullOrWhiteSpace(output.Summary) ? output.ShortSummary : output.Summary;
            day.ShortSummary = output.ShortSummary;
            day.Tags = output.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            if (markFinalized)
            {
                day.FinalizedAtUtc = DateTime.UtcNow;
            }

            day.UpdatedAtUtc = DateTime.UtcNow;
            SaveDayUnsafe(day);
            _log.Info("LifeJournalStore", $"Analysis parsed/saved for {day.Date}; fallback={output.UsedFallback}.");
            return day;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LifeHistoryItem>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(DaysDirectory))
            {
                return Array.Empty<LifeHistoryItem>();
            }

            var items = new List<LifeHistoryItem>();
            foreach (var file in Directory.GetFiles(DaysDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dateText = Path.GetFileNameWithoutExtension(file);
                if (!LifeJournalPathSafety.TryParseDate(dateText, out var date))
                {
                    continue;
                }

                var day = LoadDayUnsafe(date);
                items.Add(new LifeHistoryItem
                {
                    Date = day.Date,
                    PhotoCount = day.Photos.Count,
                    EventCount = day.Events.Count,
                    HasAnalysis = !string.IsNullOrWhiteSpace(day.AnalysisMarkdown) ||
                                  !string.IsNullOrWhiteSpace(day.AnalysisJson),
                    ShortSummary = day.ShortSummary,
                    Tags = day.Tags.ToList()
                });
            }

            return items
                .OrderByDescending(item => item.Date, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<string> GetPhotoPaths(LifeDay day)
    {
        var paths = new List<string>();
        foreach (var photo in day.Photos)
        {
            if (!LifeJournalPathSafety.TryResolvePhotoPath(PhotosDirectory, day.Date, photo.FileName, out var path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    public string GetPhotoDirectory(DateOnly date)
    {
        var directory = Path.Combine(PhotosDirectory, LifeJournalPathSafety.FormatDate(date));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public bool TryResolvePhotoPath(string date, string filename, out string path)
    {
        return LifeJournalPathSafety.TryResolvePhotoPath(PhotosDirectory, date, filename, out path) &&
               File.Exists(path);
    }

    private LifeDay LoadDayUnsafe(DateOnly date)
    {
        var dateText = LifeJournalPathSafety.FormatDate(date);
        var path = GetDayPath(date);
        if (!File.Exists(path))
        {
            return new LifeDay
            {
                Date = dateText,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        try
        {
            var json = File.ReadAllText(path);
            var day = JsonSerializer.Deserialize<LifeDay>(json, JsonOptions.Default);
            if (day is null)
            {
                return new LifeDay { Date = dateText, UpdatedAtUtc = DateTime.UtcNow };
            }

            day.Date = string.IsNullOrWhiteSpace(day.Date) ? dateText : day.Date;
            day.Photos ??= new List<LifePhoto>();
            day.Events ??= new List<LifeEvent>();
            day.Tags ??= new List<string>();
            return day;
        }
        catch (Exception ex)
        {
            _log.Error("LifeJournalStore", $"Failed to load day file for {dateText}.", ex);
            return new LifeDay { Date = dateText, UpdatedAtUtc = DateTime.UtcNow };
        }
    }

    private void SaveDayUnsafe(LifeDay day)
    {
        if (!LifeJournalPathSafety.TryParseDate(day.Date, out var date))
        {
            throw new InvalidOperationException($"Invalid LifeJournal date: {day.Date}");
        }

        var path = GetDayPath(date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(day, JsonOptions.DefaultIndented));
    }

    private string GetDayPath(DateOnly date) => Path.Combine(DaysDirectory, $"{LifeJournalPathSafety.FormatDate(date)}.json");

    private static bool IsAllowedEventType(string value)
    {
        return string.Equals(value, "woke_up", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "went_to_sleep", StringComparison.OrdinalIgnoreCase);
    }
}
