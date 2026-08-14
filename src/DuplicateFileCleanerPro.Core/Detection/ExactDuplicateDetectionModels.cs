using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Detection;

public enum ContentAnalysisFailureReason
{
    Unavailable,
    ChangedDuringAnalysis,
    ReadFailed,
    HashFailed,
    ComparisonFailed,
}

public sealed record ContentDigest
{
    public ContentDigest(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Bytes = bytes.ToArray();
    }

    public byte[] Bytes { get; }
}

public sealed record ContentHashOutcome(
    ContentDigest? Digest,
    ContentAnalysisFailureReason? FailureReason)
{
    public bool Succeeded => Digest is not null && FailureReason is null;

    public static ContentHashOutcome Success(ContentDigest digest) => new(digest, null);

    public static ContentHashOutcome Failure(ContentAnalysisFailureReason reason) => new(null, reason);
}

public sealed record ContentComparisonOutcome(
    bool? AreEqual,
    ContentAnalysisFailureReason? FailureReason)
{
    public bool Succeeded => AreEqual is not null && FailureReason is null;

    public static ContentComparisonOutcome Equal() => new(true, null);

    public static ContentComparisonOutcome Different() => new(false, null);

    public static ContentComparisonOutcome Failure(ContentAnalysisFailureReason reason) => new(null, reason);
}

/// <summary>Read-only content analysis boundary. Implementations must verify the discovery snapshot before and after each operation.</summary>
public interface IContentAnalysisService
{
    Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default);

    Task<ContentComparisonOutcome> CompareAsync(
        DiscoveredFile left,
        DiscoveredFile right,
        CancellationToken cancellationToken = default);
}

public sealed record DuplicateFileGroup(
    IReadOnlyList<DiscoveredFile> Files,
    long ReclaimableBytes);

public sealed record DuplicateDetectionSkippedItem(
    DiscoveredFile File,
    ContentAnalysisFailureReason Reason);

public sealed record DuplicateDetectionProgress(
    string CurrentPath,
    int CandidatesProcessed,
    long BytesProcessed,
    long TotalCandidateBytes,
    int VerifiedGroupCount,
    int SkippedItemCount,
    bool IsVerifying);

public sealed record ExactDuplicateDetectionResult(
    IReadOnlyList<DuplicateFileGroup> Groups,
    IReadOnlyList<DuplicateDetectionSkippedItem> SkippedItems,
    long TotalReclaimableBytes,
    bool WasCancelled);
