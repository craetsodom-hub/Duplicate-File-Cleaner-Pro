namespace DuplicateFileCleanerPro.Core.Models;

public sealed record ScanOptions(
    long MinimumFileSizeBytes,
    long? MaximumFileSizeBytes,
    bool SkipHiddenFiles,
    bool SkipSystemFiles)
{
    public static ScanOptions Default { get; } = new(1, null, true, true);
}
