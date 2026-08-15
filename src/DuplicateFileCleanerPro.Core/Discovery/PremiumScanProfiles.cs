namespace DuplicateFileCleanerPro.Core.Discovery;

public sealed record PremiumScanProfile(
    string Id,
    ScanFileType FileTypes,
    long MinimumSizeBytes = 0,
    long? MaximumSizeBytes = null,
    IReadOnlyList<string>? CustomExtensions = null)
{
    public ScanCriteria CreateCriteria() => new(FileTypes, CustomExtensions, MinimumSizeBytes, MaximumSizeBytes);
}

public static class PremiumScanProfiles
{
    public const string AllFilesId = "all-files";
    public const string LargeFilesId = "large-files";
    public const string PhotosAndVideosId = "photos-videos";
    public const string DocumentsId = "documents";
    public const string MusicId = "music";
    public const string CustomId = "custom";

    public static IReadOnlyList<PremiumScanProfile> BuiltIn { get; } = Array.AsReadOnly(
    new PremiumScanProfile[]
    {
        new(AllFilesId, ScanFileType.All),
        new(LargeFilesId, ScanFileType.All, 100L * 1024 * 1024),
        new(PhotosAndVideosId, ScanFileType.Images | ScanFileType.Video),
        new(DocumentsId, ScanFileType.Documents),
        new(MusicId, ScanFileType.Audio),
    });

    public static PremiumScanProfile? Find(string? id) =>
        BuiltIn.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal));
}
