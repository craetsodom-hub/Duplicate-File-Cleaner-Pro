namespace DuplicateFileCleanerPro.Core.Discovery;

public sealed record ScanRoot(string NormalizedPath);

/// <summary>Stable Windows physical identity: volume serial plus the complete 128-bit file ID.</summary>
public readonly record struct PhysicalFileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

public sealed record DiscoveredFile(
    string NormalizedPath,
    string FileName,
    string Extension,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    DateTimeOffset ChangeTimeUtc,
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
    SubfolderExcluded,
    FolderExcluded,
    ExtensionExcluded,
    FileTypeExcluded,
    BelowMinimumSize,
    AboveMaximumSize,
}

public sealed record SkippedDiscoveryItem(string Path, DiscoverySkipReason Reason);

public sealed record DiscoveryPolicy
{
    public DiscoveryPolicy(
        bool IncludeHiddenFiles = false,
        bool IncludeSystemFiles = false,
        bool IncludeEncryptedFiles = false,
        bool IncludeSubfolders = true,
        ScanCriteria? Criteria = null,
        IReadOnlyList<string>? ExcludedFolders = null,
        IReadOnlyList<string>? ExcludedExtensions = null)
    {
        this.IncludeHiddenFiles = IncludeHiddenFiles;
        this.IncludeSystemFiles = IncludeSystemFiles;
        this.IncludeEncryptedFiles = IncludeEncryptedFiles;
        this.IncludeSubfolders = IncludeSubfolders;
        this.Criteria = Criteria ?? ScanCriteria.AllFiles;
        this.ExcludedFolders = Array.AsReadOnly((ExcludedFolders ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        this.ExcludedExtensions = Array.AsReadOnly((ExcludedExtensions ?? []).Select(ScanCriteria.NormalizeExtension).Where(extension => extension is not null).Select(extension => extension!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public bool IncludeHiddenFiles { get; }
    public bool IncludeSystemFiles { get; }
    public bool IncludeEncryptedFiles { get; }
    public bool IncludeSubfolders { get; }
    public ScanCriteria Criteria { get; }
    public IReadOnlyList<string> ExcludedFolders { get; }
    public IReadOnlyList<string> ExcludedExtensions { get; }
}

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
