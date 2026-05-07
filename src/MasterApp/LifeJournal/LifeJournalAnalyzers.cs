using MasterApp.Hosting;
using MasterApp.Storage;
using System.Diagnostics;
using System.Text;
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

public sealed class FakeLifeJournalAnalyzer : ILifeJournalAnalyzer
{
    private readonly LifeJournalLogger _log;

    public FakeLifeJournalAnalyzer(LifeJournalLogger log)
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
            uncertainties = new[] { "Codex vision analysis was unavailable, so this summary only uses metadata." }
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions.DefaultIndented);
        var markdown = $"""
# Day Summary - {day.Date}

{summary}

_Fallback analyzer used. Visual details were not interpreted._
""";

        _log.Warn("FakeLifeJournalAnalyzer", $"Fallback analyzer used for {day.Date}.");
        return Task.FromResult(new LifeAnalysisOutput
        {
            Date = day.Date,
            ShortSummary = shortSummary,
            Summary = summary,
            Markdown = markdown,
            Json = json,
            RawOutput = json + Environment.NewLine + markdown,
            Tags = new List<string> { "unknown" },
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

public sealed class CodexCliLifeJournalAnalyzer : ILifeJournalAnalyzer
{
    private readonly LifeJournalSettings _settings;
    private readonly LifeJournalLogger _log;
    private readonly ILifeJournalAnalyzer _fallback;

    public CodexCliLifeJournalAnalyzer(
        LifeJournalSettings settings,
        LifeJournalLogger log,
        ILifeJournalAnalyzer fallback)
    {
        _settings = settings;
        _log = log;
        _fallback = fallback;
    }

    public async Task<LifeAnalysisOutput> AnalyzeAsync(
        LifeDay day,
        IReadOnlyList<string> selectedPhotoPaths,
        string? priorContext,
        CancellationToken cancellationToken)
    {
        var photos = SelectEvenlyDistributedPhotos(selectedPhotoPaths, Math.Clamp(_settings.MaxImagesForAnalysis, 1, 64));
        var prompt = BuildPrompt(day, photos, priorContext);
        var configuredExecutable = string.IsNullOrWhiteSpace(_settings.CodexExecutablePath)
            ? "codex"
            : _settings.CodexExecutablePath.Trim();
        var executableResolution = CodexExecutableResolver.Resolve(configuredExecutable);
        var executable = executableResolution.ResolvedExecutablePath;

        if (!executableResolution.IsReady || string.IsNullOrWhiteSpace(executable))
        {
            _log.Warn("CodexCliLifeJournalAnalyzer", $"Codex executable unavailable for {day.Date}: {executableResolution.LastError}");
            return await BuildFallbackAsync(
                day,
                photos,
                priorContext,
                executableResolution.LastError ?? "Codex executable was not found.",
                string.Join(Environment.NewLine, executableResolution.AttemptedPaths),
                cancellationToken);
        }

        try
        {
            _log.Info("CodexCliLifeJournalAnalyzer", $"Analysis started for {day.Date} with {photos.Count} image(s) using {executable}.");
            var raw = await RunCodexAsync(executable, photos, prompt, cancellationToken);
            var parsed = TryParseCodexOutput(day.Date, raw.Stdout, raw.Stderr, raw.ExitCode);
            if (parsed is not null)
            {
                _log.Info("CodexCliLifeJournalAnalyzer", $"Codex analysis parsed for {day.Date}.");
                return parsed;
            }

            _log.Warn("CodexCliLifeJournalAnalyzer", $"Codex output was not valid enough for {day.Date}; using fallback.");
            return await BuildFallbackAsync(day, photos, priorContext, $"Invalid Codex output. ExitCode={raw.ExitCode}", raw.Combined, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("CodexCliLifeJournalAnalyzer", $"Codex command failed for {day.Date}; using fallback.", ex);
            return await BuildFallbackAsync(day, photos, priorContext, ex.Message, ex.ToString(), cancellationToken);
        }
    }

    private async Task<LifeAnalysisOutput> BuildFallbackAsync(
        LifeDay day,
        IReadOnlyList<string> photos,
        string? priorContext,
        string error,
        string raw,
        CancellationToken cancellationToken)
    {
        var fallback = await _fallback.AnalyzeAsync(day, photos, priorContext, cancellationToken);
        fallback.UsedFallback = true;
        fallback.Error = error;
        fallback.RawOutput = raw + Environment.NewLine + Environment.NewLine + fallback.RawOutput;
        return fallback;
    }

    private async Task<CodexProcessOutput> RunCodexAsync(
        string executable,
        IReadOnlyList<string> photos,
        string prompt,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in BuildCodexArguments(photos, prompt))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        _log.Info("CodexCliLifeJournalAnalyzer", $"Codex command started: {executable} exec --image <{photos.Count} image(s)> -- <prompt>");
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Codex executable: {executable}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.AnalysisTimeoutSeconds, 5, 3600)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill failures after timeout.
            }

            throw new TimeoutException($"Codex analysis timed out after {_settings.AnalysisTimeoutSeconds} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        _log.Info("CodexCliLifeJournalAnalyzer", $"Codex stdout/stderr captured. ExitCode={process.ExitCode}, stdout={stdout.Length} chars, stderr={stderr.Length} chars.");
        return new CodexProcessOutput(process.ExitCode, stdout, stderr);
    }

    public static IReadOnlyList<string> BuildCodexArguments(IReadOnlyList<string> photos, string prompt)
    {
        var arguments = new List<string>
        {
            "exec",
            "--skip-git-repo-check",
            "--sandbox",
            "read-only"
        };

        foreach (var photo in photos)
        {
            arguments.Add("--image");
            arguments.Add(photo);
        }

        arguments.Add("--");
        arguments.Add(prompt);
        return arguments;
    }

    private static LifeAnalysisOutput? TryParseCodexOutput(string date, string stdout, string stderr, int exitCode)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        if (!TryExtractFirstJsonObject(stdout, out var jsonText, out var markdownStart))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;
            var shortSummary = TryGetString(root, "shortSummary");
            var summary = TryGetString(root, "summary");
            var markdown = markdownStart >= 0 && markdownStart < stdout.Length
                ? stdout[markdownStart..].Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(shortSummary) || string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }

            var tags = new List<string>();
            if (root.TryGetProperty("tags", out var tagElement) && tagElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagElement.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString()))
                    {
                        tags.Add(tag.GetString()!.Trim());
                    }
                }
            }

            return new LifeAnalysisOutput
            {
                Date = TryGetString(root, "date") ?? date,
                ShortSummary = shortSummary!,
                Summary = summary!,
                Json = jsonText,
                Markdown = string.IsNullOrWhiteSpace(markdown) ? $"# Day Summary - {date}\n\n{summary}" : markdown,
                RawOutput = stdout + Environment.NewLine + stderr,
                Tags = tags
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryExtractFirstJsonObject(string text, out string json, out int markdownStart)
    {
        json = string.Empty;
        markdownStart = -1;
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var index = start; index < text.Length; index++)
        {
            var ch = text[index];
            if (escape)
            {
                escape = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text[start..(index + 1)].Trim();
                    markdownStart = index + 1;
                    return true;
                }
            }
        }

        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    public static IReadOnlyList<string> SelectEvenlyDistributedPhotos(IReadOnlyList<string> photoPaths, int maxCount)
    {
        if (photoPaths.Count <= maxCount)
        {
            return photoPaths.ToArray();
        }

        var selected = new List<string>(maxCount);
        for (var index = 0; index < maxCount; index++)
        {
            var sourceIndex = (int)Math.Round(index * (photoPaths.Count - 1) / (double)(maxCount - 1));
            selected.Add(photoPaths[sourceIndex]);
        }

        return selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildPrompt(LifeDay day, IReadOnlyList<string> photos, string? priorContext)
    {
        var photoList = new StringBuilder();
        foreach (var photo in day.Photos.OrderBy(item => item.LocalTimestamp))
        {
            photoList.AppendLine($"- {photo.LocalTimestamp:HH:mm}: {photo.FileName}");
        }

        if (photoList.Length == 0)
        {
            photoList.AppendLine("- No photos.");
        }

        var events = new StringBuilder();
        foreach (var item in day.Events.OrderBy(item => item.LocalTimestamp))
        {
            events.AppendLine($"- {item.LocalTimestamp:HH:mm}: {item.Type}");
        }

        if (events.Length == 0)
        {
            events.AppendLine("- No manual events.");
        }

        var context = string.IsNullOrWhiteSpace(priorContext) ? "None." : priorContext.Trim();
        var prompt = new StringBuilder();
        prompt.AppendLine("You are analyzing a private personal photo diary day.");
        prompt.AppendLine();
        prompt.AppendLine("Input:");
        prompt.AppendLine($"- Date: {day.Date}");
        prompt.AppendLine("- Photos with timestamps:");
        prompt.Append(photoList);
        prompt.AppendLine("- Events:");
        prompt.Append(events);
        prompt.AppendLine("- Optional prior context:");
        prompt.AppendLine(context);
        prompt.AppendLine();
        prompt.AppendLine("Task:");
        prompt.AppendLine("Analyze the day from visible evidence, timestamps, and events.");
        prompt.AppendLine();
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- Do not invent facts.");
        prompt.AppendLine("- Be humble when uncertain.");
        prompt.AppendLine("- Do not diagnose mental or medical state.");
        prompt.AppendLine("- Mood is only a soft estimate.");
        prompt.AppendLine("- Do not identify private people by name.");
        prompt.AppendLine("- Mention uncertainty explicitly.");
        prompt.AppendLine("- Return valid JSON first.");
        prompt.AppendLine("- After JSON, write a readable markdown summary.");
        prompt.AppendLine();
        prompt.AppendLine("JSON schema:");
        prompt.AppendLine("{");
        prompt.AppendLine("  \"date\": \"yyyy-MM-dd\",");
        prompt.AppendLine("  \"shortSummary\": \"one sentence\",");
        prompt.AppendLine("  \"summary\": \"paragraph\",");
        prompt.AppendLine("  \"timeline\": [");
        prompt.AppendLine("    {");
        prompt.AppendLine("      \"time\": \"HH:mm\",");
        prompt.AppendLine("      \"type\": \"photo|event|inferred\",");
        prompt.AppendLine("      \"description\": \"short description\",");
        prompt.AppendLine("      \"confidence\": \"low|medium|high\"");
        prompt.AppendLine("    }");
        prompt.AppendLine("  ],");
        prompt.AppendLine("  \"tags\": [\"home\", \"work\", \"family\", \"outside\", \"food\", \"night\"],");
        prompt.AppendLine("  \"moodEstimate\": \"calm|busy|tired|social|quiet|unknown\",");
        prompt.AppendLine("  \"patterns\": [\"...\"],");
        prompt.AppendLine("  \"uncertainties\": [\"...\"]");
        prompt.AppendLine("}");
        prompt.AppendLine();
        prompt.AppendLine("Markdown:");
        prompt.AppendLine($"# Day Summary - {day.Date}");
        prompt.AppendLine("...");
        return prompt.ToString();
    }

    private sealed record CodexProcessOutput(int ExitCode, string Stdout, string Stderr)
    {
        public string Combined => Stdout + Environment.NewLine + Stderr;
    }
}
