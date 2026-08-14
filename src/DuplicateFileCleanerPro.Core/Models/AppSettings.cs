namespace DuplicateFileCleanerPro.Core.Models;

public sealed record AppSettings(
    int SchemaVersion,
    string Theme,
    string Language,
    IReadOnlyList<string> ExcludedFolders,
    IReadOnlyList<string> ExcludedExtensions,
    ScanOptions ScanOptions)
{
    public static AppSettings Default { get; } = new(1, "system", "system", Array.Empty<string>(), Array.Empty<string>(), ScanOptions.Default);
}
