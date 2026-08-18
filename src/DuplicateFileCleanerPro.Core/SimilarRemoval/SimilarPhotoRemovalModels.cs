using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.SimilarRemoval;

/// <summary>Immutable explicit human review intent. Similarity is never removal authority.</summary>
public sealed class SimilarPhotoRemovalIntent
{
    public SimilarPhotoRemovalIntent(
        CompletedSimilarPhotoScanResult analyzedResult,
        IEnumerable<PhysicalFileIdentity> explicitlyMarkedForRemoval)
    {
        AnalyzedResult = analyzedResult ?? throw new ArgumentNullException(nameof(analyzedResult));
        ArgumentNullException.ThrowIfNull(explicitlyMarkedForRemoval);
        ExplicitlyMarkedForRemoval = Array.AsReadOnly(explicitlyMarkedForRemoval.ToArray());
    }

    public CompletedSimilarPhotoScanResult AnalyzedResult { get; }
    public IReadOnlyList<PhysicalFileIdentity> ExplicitlyMarkedForRemoval { get; }
}

public enum SimilarPhotoRemovalPlanningIssueReason
{
    CancelledAnalysis,
    EmptyIntent,
    InvalidGroup,
    DuplicateCandidateIntent,
    DuplicatePhysicalIdentity,
    DuplicatePath,
    UnsupportedPath,
    UnknownCandidate,
    AllIndependentMembersSelected,
    ArithmeticOverflow,
}

public sealed record SimilarPhotoRemovalPlanningIssue(
    SimilarPhotoRemovalPlanningIssueReason Reason,
    string? Path = null);

public sealed class SimilarPhotoRemovalPlanningResult
{
    internal SimilarPhotoRemovalPlanningResult(
        SimilarPhotoRemovalPlan? plan,
        IReadOnlyList<SimilarPhotoRemovalPlanningIssue> issues)
    {
        Plan = plan;
        Issues = issues;
    }

    public SimilarPhotoRemovalPlan? Plan { get; }
    public IReadOnlyList<SimilarPhotoRemovalPlanningIssue> Issues { get; }
    public bool Succeeded => Plan is not null && Issues.Count == 0;
}

public sealed class SimilarPhotoRemovalPlan
{
    internal SimilarPhotoRemovalPlan(
        CompletedSimilarPhotoScanResult analyzedResult,
        IReadOnlyList<SimilarPhotoRemovalPlanGroup> groups,
        long selectedBytes)
    {
        AnalyzedResult = analyzedResult;
        Groups = groups;
        SelectedBytes = selectedBytes;
        RequestedPhotoCount = groups.Sum(group => group.Candidates.Count);
    }

    public CompletedSimilarPhotoScanResult AnalyzedResult { get; }
    public IReadOnlyList<SimilarPhotoRemovalPlanGroup> Groups { get; }
    public int RequestedPhotoCount { get; }
    public long SelectedBytes { get; }
}

public sealed class SimilarPhotoRemovalPlanGroup
{
    internal SimilarPhotoRemovalPlanGroup(
        int groupIndex,
        SimilarityTier tier,
        IReadOnlyList<SimilarPhotoRemovalPlanMember> members,
        IReadOnlyList<SimilarPhotoRemovalPlanMember> candidates,
        IReadOnlyList<SimilarPhotoRemovalPlanMember> survivors)
    {
        GroupIndex = groupIndex;
        Tier = tier;
        Members = members;
        Candidates = candidates;
        Survivors = survivors;
    }

    public int GroupIndex { get; }
    public SimilarityTier Tier { get; }
    public IReadOnlyList<SimilarPhotoRemovalPlanMember> Members { get; }
    public IReadOnlyList<SimilarPhotoRemovalPlanMember> Candidates { get; }
    public IReadOnlyList<SimilarPhotoRemovalPlanMember> Survivors { get; }
}

public sealed record SimilarPhotoRemovalPlanMember(DiscoveredFile ExpectedFile);

public enum SimilarPhotoRemovalValidationStatus
{
    Valid,
    Missing,
    IdentityMismatch,
    Changed,
    PolicyRejected,
    AmbiguousHardLinks,
    Unavailable,
}

public sealed record SimilarPhotoRemovalValidation(SimilarPhotoRemovalValidationStatus Status)
{
    public bool IsValid => Status == SimilarPhotoRemovalValidationStatus.Valid;
    public static SimilarPhotoRemovalValidation Valid() => new(SimilarPhotoRemovalValidationStatus.Valid);
}

public enum SimilarPhotoRecycleAttemptStatus
{
    Recycled,
    CandidateMissing,
    CandidateIdentityMismatch,
    CandidateChanged,
    CandidatePolicyRejected,
    CandidateAmbiguousHardLinks,
    CandidateUnavailable,
    SurvivorUnavailable,
    RecycleBinFailed,
}

public sealed record SimilarPhotoRecycleAttempt(
    SimilarPhotoRecycleAttemptStatus Status,
    int? NativeErrorCode = null)
{
    public bool Recycled => Status == SimilarPhotoRecycleAttemptStatus.Recycled;
}

public interface ISimilarPhotoRemovalPlatform
{
    Task<SimilarPhotoRemovalValidation> ValidateAsync(
        SimilarPhotoRemovalPlanMember member,
        CancellationToken cancellationToken = default);

    /// <summary>Revalidates the candidate and at least one independent survivor immediately before Recycle Bin execution.</summary>
    Task<SimilarPhotoRecycleAttempt> RevalidateAndRecycleAsync(
        SimilarPhotoRemovalPlanMember candidate,
        IReadOnlyList<SimilarPhotoRemovalPlanMember> survivors,
        CancellationToken cancellationToken = default);
}

public enum SimilarPhotoRemovalOutcomeStatus
{
    Recycled,
    SkippedMissing,
    SkippedIdentityMismatch,
    SkippedChanged,
    SkippedPolicy,
    SkippedAmbiguousHardLinks,
    SkippedSurvivorUnavailable,
    FailedRecycleBin,
    FailedPlatform,
    Cancelled,
}

public sealed record SimilarPhotoRemovalOutcome(
    SimilarPhotoRemovalPlanMember Candidate,
    SimilarPhotoRemovalOutcomeStatus Status,
    int? NativeErrorCode = null);

public sealed class SimilarPhotoRemovalGroupResult
{
    internal SimilarPhotoRemovalGroupResult(
        int groupIndex,
        IReadOnlyList<SimilarPhotoRemovalOutcome> outcomes,
        int validatedSurvivorCount)
    {
        GroupIndex = groupIndex;
        Outcomes = outcomes;
        ValidatedSurvivorCount = validatedSurvivorCount;
    }

    public int GroupIndex { get; }
    public IReadOnlyList<SimilarPhotoRemovalOutcome> Outcomes { get; }
    public int ValidatedSurvivorCount { get; }
}

public sealed class SimilarPhotoRemovalResult
{
    internal SimilarPhotoRemovalResult(IReadOnlyList<SimilarPhotoRemovalGroupResult> groups, bool wasCancelled)
    {
        Groups = groups;
        WasCancelled = wasCancelled;
        RequestedPhotoCount = groups.Sum(group => group.Outcomes.Count);
        RecycledPhotoCount = groups.Sum(group => group.Outcomes.Count(outcome => outcome.Status == SimilarPhotoRemovalOutcomeStatus.Recycled));
        SkippedPhotoCount = groups.Sum(group => group.Outcomes.Count(outcome => outcome.Status is
            SimilarPhotoRemovalOutcomeStatus.SkippedMissing or
            SimilarPhotoRemovalOutcomeStatus.SkippedIdentityMismatch or
            SimilarPhotoRemovalOutcomeStatus.SkippedChanged or
            SimilarPhotoRemovalOutcomeStatus.SkippedPolicy or
            SimilarPhotoRemovalOutcomeStatus.SkippedAmbiguousHardLinks or
            SimilarPhotoRemovalOutcomeStatus.SkippedSurvivorUnavailable or
            SimilarPhotoRemovalOutcomeStatus.Cancelled));
        FailedPhotoCount = groups.Sum(group => group.Outcomes.Count(outcome => outcome.Status is
            SimilarPhotoRemovalOutcomeStatus.FailedRecycleBin or SimilarPhotoRemovalOutcomeStatus.FailedPlatform));
        RecycledBytes = groups.SelectMany(group => group.Outcomes)
            .Where(outcome => outcome.Status == SimilarPhotoRemovalOutcomeStatus.Recycled)
            .Aggregate(0L, (total, outcome) => checked(total + outcome.Candidate.ExpectedFile.Length));
    }

    public IReadOnlyList<SimilarPhotoRemovalGroupResult> Groups { get; }
    public int RequestedPhotoCount { get; }
    public int RecycledPhotoCount { get; }
    public int SkippedPhotoCount { get; }
    public int FailedPhotoCount { get; }
    public long RecycledBytes { get; }
    public bool WasCancelled { get; }
}

public sealed record SimilarPhotoRemovalProgress(
    int ProcessedPhotos,
    int TotalPhotos,
    int RecycledPhotos,
    int SkippedPhotos,
    int FailedPhotos,
    long RecycledBytes);
