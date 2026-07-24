using MasterApp.Bootstrap;
using Microsoft.AspNetCore.Http;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MasterApp.LifeJournal;

public sealed class LifeJournalService
{
    private const long MaxUploadBytes = 12 * 1024 * 1024;
    private readonly LifeJournalSettings _settings;
    private readonly LifeJournalLogger _log;
    private readonly LifeJournalStore _store;
    private readonly ILifeJournalAnalyzer _analyzer;

    public LifeJournalService(BootstrapContext context)
    {
        _settings = context.Settings.LifeJournal;
        RootDirectory = Path.Combine(context.Paths.StateDirectory, "LifeJournal");
        _log = new LifeJournalLogger(RootDirectory);
        _store = new LifeJournalStore(RootDirectory, _log);
        _analyzer = new MetadataLifeJournalAnalyzer(_log);
        EnsureRuntimeAssets();
        _log.Info("LifeJournalService", $"LifeJournal initialized at {RootDirectory}.");
    }

    public string RootDirectory { get; }
    public string LogsPath => _log.LogFilePath;
    public LifeJournalStore Store => _store;
    public ILifeJournalAnalyzer Analyzer => _analyzer;
    public LifeJournalSettings Settings => _settings;

    public Task<LifeDay> GetTodayAsync(CancellationToken cancellationToken)
    {
        _log.Info("LifeJournalService", "Today loaded.");
        return _store.LoadTodayAsync(DateTimeOffset.Now, cancellationToken);
    }

    public Task<LifeDay> GetDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        _log.Info("LifeJournalService", $"Day loaded: {LifeJournalPathSafety.FormatDate(date)}.");
        return _store.LoadDayAsync(date, cancellationToken);
    }

    public Task<IReadOnlyList<LifeHistoryItem>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        return _store.GetHistoryAsync(cancellationToken);
    }

    public async Task<LifeDay> SavePhotoAsync(IFormFile file, DateOnly? requestedDate, CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Uploaded file is empty.");
        }

        if (file.Length > MaxUploadBytes)
        {
            throw new InvalidOperationException($"Uploaded image is too large. Limit is {MaxUploadBytes / 1024 / 1024} MB.");
        }

        if (string.IsNullOrWhiteSpace(file.ContentType) ||
            !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only image uploads are accepted.");
        }

        var localNow = DateTimeOffset.Now;
        var date = requestedDate ?? DateOnly.FromDateTime(localNow.DateTime);
        var directory = _store.GetPhotoDirectory(date);
        var fileName = $"{localNow:HH-mm-ss}-{Guid.NewGuid():N}.jpg";
        var targetPath = Path.Combine(directory, fileName);

        _log.Info("LifeJournalService", $"Photo upload start: {file.FileName}, {file.Length} bytes.");
        try
        {
            var dimensions = await SaveCompressedJpegAsync(file, targetPath, cancellationToken);
            var relativePath = Path.Combine("photos", LifeJournalPathSafety.FormatDate(date), fileName).Replace('\\', '/');
            var savedFile = new FileInfo(targetPath);
            var photo = new LifePhoto
            {
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = localNow.UtcDateTime,
                LocalTimestamp = localNow,
                FileName = fileName,
                RelativePath = relativePath,
                MimeType = "image/jpeg",
                SizeBytes = savedFile.Length,
                Width = dimensions.Width,
                Height = dimensions.Height
            };

            var day = await _store.AddPhotoAsync(date, photo, cancellationToken);
            _log.Info("LifeJournalService", $"Photo upload success: {fileName}, {savedFile.Length} bytes.");
            return day;
        }
        catch (Exception ex)
        {
            _log.Error("LifeJournalService", $"Photo upload failure: {file.FileName}", ex);
            TryDelete(targetPath);
            throw;
        }
    }

    public Task<LifeDay> SaveEventAsync(string eventType, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var date = DateOnly.FromDateTime(now.DateTime);
        return _store.AddEventAsync(date, eventType, now, cancellationToken);
    }

    public async Task<LifeDay> AnalyzeDayAsync(DateOnly date, bool markFinalized, CancellationToken cancellationToken)
    {
        var day = await _store.LoadDayAsync(date, cancellationToken);
        var photoPaths = _store.GetPhotoPaths(day);
        var priorContext = day.AnalysisMarkdown;
        _log.Info("LifeJournalService", $"Analysis started for {day.Date}; photos={photoPaths.Count}, markFinalized={markFinalized}.");
        var output = await _analyzer.AnalyzeAsync(day, photoPaths, priorContext, cancellationToken);
        return await _store.SaveAnalysisAsync(date, output, markFinalized, cancellationToken);
    }

    public bool TryResolvePhotoPath(string date, string filename, out string path)
    {
        return _store.TryResolvePhotoPath(date, filename, out path);
    }

    private async Task<Size> SaveCompressedJpegAsync(IFormFile file, string targetPath, CancellationToken cancellationToken)
    {
        await using var uploaded = file.OpenReadStream();
        using var memory = new MemoryStream();
        await uploaded.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;

        using var source = Image.FromStream(memory, useEmbeddedColorManagement: false, validateImageData: true);
        var maxWidth = Math.Clamp(_settings.PhotoMaxWidth, 320, 2400);
        var scale = source.Width > maxWidth ? maxWidth / (double)source.Width : 1.0;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var bitmap = new Bitmap(width, height);
        bitmap.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(item => string.Equals(item.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
        if (encoder is null)
        {
            throw new InvalidOperationException("JPEG encoder is not available.");
        }

        var quality = Math.Clamp(_settings.JpegQuality, 35, 95);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bitmap.Save(targetPath, encoder, encoderParameters);
        return new Size(width, height);
    }

    private void EnsureRuntimeAssets()
    {
        try
        {
            var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "life", "assets");
            if (!Directory.Exists(sourceDirectory))
            {
                _log.Warn("LifeJournalService", $"LifeJournal web assets not found at {sourceDirectory}.");
                return;
            }

            foreach (var file in Directory.GetFiles(sourceDirectory, "*.png"))
            {
                var destination = Path.Combine(_store.AssetsDirectory, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: true);
            }

            _log.Info("LifeJournalService", $"Runtime assets copied to {_store.AssetsDirectory}.");
        }
        catch (Exception ex)
        {
            _log.Error("LifeJournalService", "Failed to copy runtime assets.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failure after a failed upload.
        }
    }
}
