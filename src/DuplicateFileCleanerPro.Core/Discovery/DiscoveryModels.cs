namespace DuplicateFileCleanerPro.Core.Discovery;

public sealed record ScanRoot(string NormalizedPath);

public readonly record struct PhysicalFileIdentity(uint VolumeSerialNumber, ulong FileId);

public sealed record DiscoveredFile(
    string NormalizedPath,
    string FileName,
    string Extension,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    PhysicalFileIdentity PhysicalIdentity,
    FileAttributes Attributes);

public enum DiscoverySkipReason
{
    InvalidRoot,
    NetworkLocation,
    UnsupportedLocation,
    Inaccessible,
    ReparsePoint,
    Offline,
    HiddenByPolicy,
    SystemByPolicy,
    Encrypted,
    AlternateDataStream,
    IdentityUnavailable,
    UnstableOrDisappeared,
    UnsupportedObject,
}

public sealed record SkippedDiscoveryItem(string Path, DiscoverySkipReason Reason);

public sealed record DiscoveryPolicy(
    bool IncludeHiddenFiles = false,
    bool IncludeSystemFiles = false,
    bool IncludeEncryptedFiles = false);

public sealed record RootNormalizationResult(
    IReadOnlyList<ScanRoot> Roots,
    IReadOnlyList<SkippedDiscoveryItem> RejectedRoots);

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredFile> Files,
    IReadOnlyList<SkippedDiscoveryItem> SkippedItems,
    bool WasCancelled);

public sealed record DiscoveryProgress(
    string CurrentPath,
    int FilesDiscovered,
    int SkippedItemCount);

public interface IScanRootNormalizer
{
    RootNormalizationResult Normalize(IEnumerable<string> selectedPaths);
}

public interface IFileDiscoveryService
{
    Task<DiscoveryResult> DiscoverAsync(
        IEnumerable<ScanRoot> roots,
        DiscoveryPolicy policy,
        IProgress<DiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
