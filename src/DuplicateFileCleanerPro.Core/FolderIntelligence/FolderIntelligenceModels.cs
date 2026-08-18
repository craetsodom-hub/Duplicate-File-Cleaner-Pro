using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.FolderIntelligence;

public enum FolderIntelligenceStage
{
    DiscoveringFolders,
    BuildingCandidates,
    VerifyingFileTrees,
    BuildingDuplicateFolderGroups,
    ReadingMaster,
    ReadingComparisonFolder,
    MatchingPaths,
    VerifyingContent,
    FindingMovedRenamedMatches,
    BuildingComparison,
    Completed,
}

public sealed record FolderIntelligenceProgress(
    FolderIntelligenceStage Stage,
    string CurrentPath,
    int ItemsProcessed,
    int TotalItems,
    int ResultCount,
    bool IsVerifying);

public sealed record FolderFileEntry(
    string FolderRoot,
    string RelativePath,
    DiscoveredFile File);

public sealed record FolderTreeSnapshot(
    string RootPath,
    IReadOnlyList<FolderFileEntry> Files,
    int LogicalFileCount,
    int IndependentPhysicalFileCount,
    long TotalLogicalBytes,
    long PhysicalBytes)
{
    public bool IsEmpty => Files.Count == 0;
}

public sealed record VerifiedDuplicateFolderGroup(
    IReadOnlyList<FolderTreeSnapshot> MemberFolders,
    int LogicalFileCount,
    int IndependentPhysicalFileCount,
    long TotalLogicalBytes,
    long PotentialReclaimableBytes);

public sealed record DuplicateFolderScanResult(
    IReadOnlyList<VerifiedDuplicateFolderGroup> Groups,
    IReadOnlyList<SkippedDiscoveryItem> SkippedItems,
    bool WasCancelled,
    IReadOnlyList<string> ScannedRoots);

public enum FolderComparisonStatus
{
    Identical,
    Different,
    OnlyInMaster,
    OnlyInTarget,
    MovedRenamedExactMatch,
}

public sealed record FolderComparisonRow(
    string TargetRoot,
    string RelativePath,
    FolderComparisonStatus Status,
    FolderFileEntry? MasterFile,
    IReadOnlyList<FolderFileEntry> ComparedFiles,
    long? MasterSize,
    long? ComparedSize,
    DateTimeOffset? MasterModifiedUtc,
    DateTimeOffset? ComparedModifiedUtc);

public sealed record FolderComparisonSummary(
    int MasterFiles,
    int ComparedFiles,
    int Identical,
    int Different,
    int OnlyInMaster,
    int OnlyInCompared,
    int MovedRenamedExactMatches,
    long ExactDuplicateBytes);

public sealed record FolderComparisonTargetResult(
    string MasterRoot,
    string TargetRoot,
    FolderComparisonSummary Summary,
    IReadOnlyList<FolderComparisonRow> Rows,
    IReadOnlyList<SkippedDiscoveryItem> SkippedItems);

public sealed record MasterFolderComparisonResult(
    string MasterRoot,
    IReadOnlyList<FolderComparisonTargetResult> Targets,
    bool WasCancelled);
