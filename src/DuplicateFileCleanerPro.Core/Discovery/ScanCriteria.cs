namespace DuplicateFileCleanerPro.Core.Discovery;

[Flags]
public enum ScanFileType
{
    None = 0,
    Documents = 1 << 0,
    Images = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
    Archives = 1 << 4,
    Other = 1 << 5,
    All = Documents | Images | Audio | Video | Archives | Other,
}

public enum ScanCriteriaRejection
{
    None,
    ExtensionExcluded,
    FileTypeExcluded,
    BelowMinimumSize,
    AboveMaximumSize,
}

/// <summary>Immutable, UI-neutral inclusion criteria applied before content analysis.</summary>
public sealed record ScanCriteria
{
    private static readonly IReadOnlyDictionary<ScanFileType, HashSet<string>> KnownExtensions =
        new Dictionary<ScanFileType, HashSet<string>>
        {
            [ScanFileType.Documents] = Set(".csv", ".doc", ".docm", ".docx", ".epub", ".log", ".md", ".ods", ".odt", ".pdf", ".ppt", ".pptm", ".pptx", ".rtf", ".tex", ".txt", ".xls", ".xlsm", ".xlsx"),
            [ScanFileType.Images] = Set(".avif", ".bmp", ".cr2", ".dng", ".gif", ".heic", ".heif", ".ico", ".jpeg", ".jpg", ".nef", ".png", ".raw", ".svg", ".tif", ".tiff", ".webp"),
            [ScanFileType.Audio] = Set(".aac", ".aiff", ".alac", ".flac", ".m4a", ".mid", ".midi", ".mp3", ".ogg", ".opus", ".wav", ".wma"),
            [ScanFileType.Video] = Set(".3gp", ".avi", ".flv", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".webm", ".wmv"),
            [ScanFileType.Archives] = Set(".7z", ".bz2", ".cab", ".gz", ".iso", ".rar", ".tar", ".tgz", ".xz", ".zip"),
        };

    public static ScanCriteria AllFiles { get; } = new(ScanFileType.All);

    public ScanCriteria(
        ScanFileType fileTypes,
        IEnumerable<string>? customExtensions = null,
        long minimumSizeBytes = 0,
        long? maximumSizeBytes = null)
    {
        if ((fileTypes & ~ScanFileType.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileTypes));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minimumSizeBytes);

        if (maximumSizeBytes is < 0 || maximumSizeBytes < minimumSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSizeBytes));
        }

        FileTypes = fileTypes;
        CustomExtensions = Array.AsReadOnly((customExtensions ?? [])
            .Select(NormalizeExtension)
            .Where(extension => extension is not null)
            .Select(extension => extension!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        MinimumSizeBytes = minimumSizeBytes;
        MaximumSizeBytes = maximumSizeBytes;
    }

    public ScanFileType FileTypes { get; }
    public IReadOnlyList<string> CustomExtensions { get; }
    public long MinimumSizeBytes { get; }
    public long? MaximumSizeBytes { get; }

    public ScanCriteriaRejection Evaluate(string? extension, long length, IReadOnlyCollection<string>? excludedExtensions = null)
    {
        string normalized = NormalizeExtension(extension) ?? string.Empty;
        if (excludedExtensions?.Contains(normalized, StringComparer.OrdinalIgnoreCase) == true)
        {
            return ScanCriteriaRejection.ExtensionExcluded;
        }

        if (length < MinimumSizeBytes)
        {
            return ScanCriteriaRejection.BelowMinimumSize;
        }

        if (MaximumSizeBytes is long maximum && length > maximum)
        {
            return ScanCriteriaRejection.AboveMaximumSize;
        }

        if (CustomExtensions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return ScanCriteriaRejection.None;
        }

        ScanFileType category = Classify(normalized);
        return (FileTypes & category) != 0 ? ScanCriteriaRejection.None : ScanCriteriaRejection.FileTypeExcluded;
    }

    public static ScanFileType Classify(string? extension)
    {
        string normalized = NormalizeExtension(extension) ?? string.Empty;
        foreach ((ScanFileType type, HashSet<string> extensions) in KnownExtensions)
        {
            if (extensions.Contains(normalized))
            {
                return type;
            }
        }

        return ScanFileType.Other;
    }

    public static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string extension = value.Trim();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        if (extension.Length is < 2 or > 32
            || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || extension.Contains('*')
            || extension.Contains('?')
            || extension.AsSpan(1).Contains('.')
            || extension.Contains(Path.DirectorySeparatorChar)
            || extension.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        return extension.ToLowerInvariant();
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);
}
