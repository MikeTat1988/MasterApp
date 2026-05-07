using MasterApp.Storage;
using System.Text.Json;

namespace MasterApp.LifeJournal;

public sealed class LifeJournalFinalizer : IDisposable
{
    private readonly LifeJournalStore _store;
    private readonly ILifeJournalAnalyzer _analyzer;
    private readonly LifeJournalLogger _log;
    private readonly LifeJournalSettings _settings;
    private readonly string _stateFilePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public LifeJournalFinalizer(
        LifeJournalStore store,
        ILifeJournalAnalyzer analyzer,
        LifeJournalLogger log,
        LifeJournalSettings settings,
        string stateFilePath,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _analyzer = analyzer;
        _log = log;
        _settings = settings;
        _stateFilePath = stateFilePath;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
    }

    public void Start()
    {
        if (_loopCts is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
        _log.Info("LifeJournalFinalizer", "Finalizer background loop started.");
    }

    public void RequestBackgroundCheck()
    {
        _ = Task.Run(() => RunOnceAsync(CancellationToken.None));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            _log.Info("LifeJournalFinalizer", "Finalizer skipped because another run is active.");
            return;
        }

        try
        {
            var now = _clock();
            var today = DateOnly.FromDateTime(now.DateTime);
            var state = LoadState();
            state.LastCheckedDate = LifeJournalPathSafety.FormatDate(today);
            state.LastRunAtUtc = DateTime.UtcNow;

            if (now.Hour < Math.Clamp(_settings.AutoFinalizeHourLocal, 0, 23))
            {
                SaveState(state);
                _log.Info("LifeJournalFinalizer", "Finalizer skipped before auto-finalize hour.");
                return;
            }

            var targetDate = today.AddDays(-1);
            var targetText = LifeJournalPathSafety.FormatDate(targetDate);
            var day = await _store.LoadDayAsync(targetDate, cancellationToken);
            if (day.Photos.Count == 0)
            {
                SaveState(state);
                _log.Info("LifeJournalFinalizer", $"Finalizer skipped {targetText}: no photos.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(day.AnalysisMarkdown) ||
                !string.IsNullOrWhiteSpace(day.AnalysisJson) ||
                string.Equals(state.LastFinalizedDate, targetText, StringComparison.Ordinal))
            {
                state.LastFinalizedDate = targetText;
                SaveState(state);
                _log.Info("LifeJournalFinalizer", $"Finalizer skipped {targetText}: already analyzed.");
                return;
            }

            _log.Info("LifeJournalFinalizer", $"Finalizer running analysis for {targetText}.");
            var photoPaths = _store.GetPhotoPaths(day);
            var output = await _analyzer.AnalyzeAsync(day, photoPaths, day.AnalysisMarkdown, cancellationToken);
            await _store.SaveAnalysisAsync(targetDate, output, markFinalized: true, cancellationToken);
            state.LastFinalizedDate = targetText;
            SaveState(state);
            _log.Info("LifeJournalFinalizer", $"Finalizer completed {targetText}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("LifeJournalFinalizer", "Finalizer failed.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            _loopCts?.Cancel();
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown races.
        }
        finally
        {
            _loopCts?.Dispose();
            _gate.Dispose();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await RunOnceAsync(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RunOnceAsync(cancellationToken);
        }
    }

    private LifeJournalFinalizerState LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return new LifeJournalFinalizerState();
            }

            var json = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<LifeJournalFinalizerState>(json, JsonOptions.Default) ?? new LifeJournalFinalizerState();
        }
        catch (Exception ex)
        {
            _log.Error("LifeJournalFinalizer", "Failed to load finalizer state.", ex);
            return new LifeJournalFinalizerState();
        }
    }

    private void SaveState(LifeJournalFinalizerState state)
    {
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, JsonOptions.DefaultIndented));
    }
}
