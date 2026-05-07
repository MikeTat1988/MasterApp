using System.Globalization;

namespace MasterApp.LifeJournal;

public static class LifeJournalPathSafety
{
    public static bool TryParseDate(string? value, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryResolvePhotoPath(string photosRoot, string date, string filename, out string path)
    {
        path = string.Empty;

        if (!TryParseDate(date, out var parsedDate) ||
            string.IsNullOrWhiteSpace(filename) ||
            filename.Contains("..", StringComparison.Ordinal) ||
            filename.Contains('/', StringComparison.Ordinal) ||
            filename.Contains('\\', StringComparison.Ordinal) ||
            !string.Equals(filename, Path.GetFileName(filename), StringComparison.Ordinal))
        {
            return false;
        }

        var extension = Path.GetExtension(filename);
        if (!string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var root = Path.GetFullPath(photosRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dateDirectory = Path.GetFullPath(Path.Combine(root, FormatDate(parsedDate)))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(dateDirectory, filename));

        if (!dateDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dateDirectory, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!candidate.StartsWith(dateDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = candidate;
        return true;
    }
}
