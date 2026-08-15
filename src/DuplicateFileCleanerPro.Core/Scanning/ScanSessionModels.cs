using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Scanning;

public enum ScanSessionState
{
    Idle,
    Preparing,
    Discovering,
    Analyzing,
    Completed,
    Cancelled,
    Failed,
}

public sealed record ScanSessionProgress(
    ScanSessionState State,
    string CurrentPath,
    int FilesDiscovered,
    int FilesAnalyzed,
    long BytesProcessed,
    long TotalCandidateBytes,
    int VerifiedGroupCount,
    int SkippedItemCount,
    bool IsVerifying);

public sealed record CompletedScanResult(
    DiscoveryResult Discovery,
    ExactDuplicateDetectionResult Detection,
    IReadOnlyList<string>? ScanRoots = null);

public sealed record ScanSessionResult(
    ScanSessionState State,
    CompletedScanResult? CompletedResult,
    Exception? Failure)
{
    public static ScanSessionResult Completed(CompletedScanResult result) => new(ScanSessionState.Completed, result, null);
    public static ScanSessionResult Cancelled() => new(ScanSessionState.Cancelled, null, null);
    public static ScanSessionResult Failed(Exception failure) => new(ScanSessionState.Failed, null, failure);
}
