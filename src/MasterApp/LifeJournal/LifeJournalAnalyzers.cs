using MasterApp.Storage;
using System.Text.Json;

namespace MasterApp.LifeJournal;

public interface ILifeJournalAnalyzer
{
    Task<LifeAnalysisOutput> AnalyzeAsync(
        LifeDay day,
        IReadOnlyList<string> selectedPhotoPaths,
        string? priorContext,
        CancellationToken cancellationToken);
}

public sealed class MetadataLifeJournalAnalyzer : ILifeJournalAnalyzer
{
    private readonly LifeJournalLogger _log;

    public MetadataLifeJournalAnalyzer(LifeJournalLogger log)
    {
        _log = log;
    }

    public Task<LifeAnalysisOutput> AnalyzeAsync(
        LifeDay day,
        IReadOnlyList<string> selectedPhotoPaths,
        string? priorContext,
        CancellationToken cancellationToken)
    {
        var photoWord = day.Photos.Count == 1 ? "photo" : "photos";
        var eventWord = day.Events.Count == 1 ? "event" : "events";
        var shortSummary = day.Photos.Count == 0
            ? "No photos were captured for this day."
            : $"A private photo diary captured {day.Photos.Count} {photoWord} and {day.Events.Count} {eventWord}.";
        var summary = day.Photos.Count == 0
            ? "There is not enough visual evidence to summarize the day yet."
            : "The day contains saved photo moments and any manually recorded wake/sleep events. This fallback summary avoids guessing about people, places, or mood.";

        var payload = new
        {
            date = day.Date,
            shortSummary,
            summary,
            timeline = BuildFallbackTimeline(day),
            tags = new[] { "unknown" },
            moodEstimate = "unknown",
            patterns = Array.Empty<string>(),
            uncertainties = new[] { "Visual interpretation is not enabled, so this summary only uses saved metadata." }
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions.DefaultIndented);
        var markdown = $"""
# Day Summary - {day.Date}

{summary}

_Metadata-only analyzer used. Visual details were not interpreted._
""";

        _log.Info("MetadataLifeJournalAnalyzer", $"Metadata-only analysis used for {day.Date}.");
        return Task.FromResult(new LifeAnalysisOutput
        {
            Date = day.Date,
            ShortSummary = shortSummary,
            Summary = summary,
            Markdown = markdown,
            Json = json,
            RawOutput = json + Environment.NewLine + markdown,
            Tags = new List<string> { "unknown" },
            Uncertainties = new List<string> { "Visual interpretation is not enabled, so this summary only uses saved metadata." },
            UsedFallback = true
        });
    }

    private static IReadOnlyList<object> BuildFallbackTimeline(LifeDay day)
    {
        var entries = new List<object>();
        foreach (var photo in day.Photos.OrderBy(item => item.LocalTimestamp))
        {
            entries.Add(new
            {
                time = photo.LocalTimestamp.ToString("HH:mm"),
                type = "photo",
                description = "Photo captured.",
                confidence = "high"
            });
        }

        foreach (var lifeEvent in day.Events.OrderBy(item => item.LocalTimestamp))
        {
            entries.Add(new
            {
                time = lifeEvent.LocalTimestamp.ToString("HH:mm"),
                type = "event",
                description = lifeEvent.Type,
                confidence = "high"
            });
        }

        return entries;
    }
}
