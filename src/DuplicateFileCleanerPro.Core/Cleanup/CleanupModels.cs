using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;

namespace DuplicateFileCleanerPro.Core.Cleanup;

/// <summary>Immutable user intent derived from one completed, verified scan. It is never cleanup authorization.</summary>
public sealed class CleanupSelectionIntent
{
    public CleanupSelectionIntent(CompletedScanResult verifiedResult, IEnumerable<PhysicalFileIdentity> selectedPhysicalMembers)
    {
        VerifiedResult = verifiedResult ?? throw new ArgumentNullException(nameof(verifiedResult));
        ArgumentNullException.ThrowIfNull(selectedPhysicalMembers);
        SelectedPhysicalMembers = Array.AsReadOnly(selectedPhysicalMembers.Distinct().ToArray());
    }

    public CompletedScanResult VerifiedResult { get; }
    public IReadOnlyList<PhysicalFileIdentity> SelectedPhysicalMembers { get; }
}

public enum CleanupPlanningIssueReason
{
    CancelledScanSnapshot,
    InvalidVerifiedGroup,
    DuplicatePhysicalIdentity,
    DuplicatePath,
    UnsupportedPath,
    UnknownSelectedIdentity,
    AllMembersSelected,
    SnapshotArithmeticMismatch,
}

public sealed record CleanupPlanningIssue(CleanupPlanningIssueReason Reason, string? Path = null);

public sealed class CleanupPlanningResult
{
    internal CleanupPlanningResult(CleanupPlan? plan, IReadOnlyList<CleanupPlanningIssue> issues)
    {
        Plan = plan;
        Issues = issues;
    }

    public CleanupPlan? Plan { get; }
    public IReadOnlyList<CleanupPlanningIssue> Issues { get; }
    public bool Succeeded => Plan is not null && Issues.Count == 0;
}

/// <summary>An immutable validated plan. Execution still revalidates immediately before every Recycle Bin operation.</summary>
public sealed class CleanupPlan
{
    internal CleanupPlan(CompletedScanResult verifiedResult, IReadOnlyList<CleanupPlanGroup> groups)
    {
        VerifiedResult = verifiedResult;
        Groups = groups;
        RequestedCandidateCount = groups.Sum(group => group.Candidates.Count);
    }

    public CompletedScanResult VerifiedResult { get; }
    public IReadOnlyList<CleanupPlanGroup> Groups { get; }
    public int RequestedCandidateCount { get; }
}

public sealed class CleanupPlanGroup
{
    internal CleanupPlanGroup(
        int groupIndex,
        IReadOnlyList<CleanupPlanMember> members,
        IReadOnlyList<CleanupPlanMember> candidates,
        IReadOnlyList<CleanupPlanMember> keepers)
    {
        GroupIndex = groupIndex;
        Members = members;
        Candidates = candidates;
        Keepers = keepers;
    }

    public int GroupIndex { get; }
    public IReadOnlyList<CleanupPlanMember> Members { get; }
    public IReadOnlyList<CleanupPlanMember> Candidates { get; }
    public IReadOnlyList<CleanupPlanMember> Keepers { get; }
}

public sealed record CleanupPlanMember(DiscoveredFile ExpectedFile);

public enum CleanupFileValidationStatus
{
    Valid,
    Missing,
    IdentityMismatch,
    Changed,
    PolicyRejected,
    Unavailable,
}

public sealed record CleanupFileValidation(CleanupFileValidationStatus Status)
{
    public bool IsValid => Status == CleanupFileValidationStatus.Valid;
    public static CleanupFileValidation Valid() => new(CleanupFileValidationStatus.Valid);
}

public enum CleanupRecycleAttemptStatus
{
    Recycled,
    CandidateMissing,
    CandidateIdentityMismatch,
    CandidateChanged,
    CandidatePolicyRejected,
    CandidateUnavailable,
    KeeperMissing,
    KeeperIdentityMismatch,
    KeeperChanged,
    KeeperPolicyRejected,
    KeeperUnavailable,
    ContentMismatch,
    VerificationFailed,
    RecycleBinFailed,
}

public sealed record CleanupRecycleAttempt(CleanupRecycleAttemptStatus Status, int? NativeErrorCode = null)
{
    public bool Recycled => Status == CleanupRecycleAttemptStatus.Recycled;
}

public interface ICleanupPlatformService
{
    Task<CleanupFileValidation> ValidateAsync(CleanupPlanMember member, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revalidates both physical members, performs a current mandatory byte comparison, then invokes
    /// the Recycle Bin with the smallest practical pathname race window.
    /// </summary>
    Task<CleanupRecycleAttempt> RevalidateAndRecycleAsync(
        CleanupPlanMember candidate,
        CleanupPlanMember keeper,
        CancellationToken cancellationToken = default);
}

public enum CleanupCandidateOutcomeStatus
{
    Recycled,
    SkippedMissing,
    SkippedIdentityMismatch,
    SkippedChanged,
    SkippedKeeperUnavailable,
    SkippedVerificationFailed,
    SkippedPolicy,
    FailedRecycleBin,
    FailedPlatform,
    Cancelled,
}

public sealed record CleanupCandidateOutcome(
    CleanupPlanMember Candidate,
    CleanupCandidateOutcomeStatus Status,
    int? NativeErrorCode = null);

public sealed class CleanupGroupResult
{
    internal CleanupGroupResult(int groupIndex, IReadOnlyList<CleanupCandidateOutcome> outcomes, int survivingVerifiedMembers)
    {
        GroupIndex = groupIndex;
        Outcomes = outcomes;
        SurvivingVerifiedMembers = survivingVerifiedMembers;
        RequestedCandidateCount = outcomes.Count;
        RecycledFileCount = outcomes.Count(outcome => outcome.Status == CleanupCandidateOutcomeStatus.Recycled);
        SkippedFileCount = outcomes.Count(outcome => outcome.Status is
            CleanupCandidateOutcomeStatus.SkippedMissing or
            CleanupCandidateOutcomeStatus.SkippedIdentityMismatch or
            CleanupCandidateOutcomeStatus.SkippedChanged or
            CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable or
            CleanupCandidateOutcomeStatus.SkippedVerificationFailed or
            CleanupCandidateOutcomeStatus.SkippedPolicy or
            CleanupCandidateOutcomeStatus.Cancelled);
        FailedFileCount = outcomes.Count(outcome => outcome.Status is CleanupCandidateOutcomeStatus.FailedRecycleBin or CleanupCandidateOutcomeStatus.FailedPlatform);
        ActuallyReclaimedBytes = outcomes
            .Where(outcome => outcome.Status == CleanupCandidateOutcomeStatus.Recycled)
            .Aggregate(0L, (total, outcome) => checked(total + outcome.Candidate.ExpectedFile.Length));
    }

    public int GroupIndex { get; }
    public IReadOnlyList<CleanupCandidateOutcome> Outcomes { get; }
    public int RequestedCandidateCount { get; }
    public int RecycledFileCount { get; }
    public int SkippedFileCount { get; }
    public int FailedFileCount { get; }
    public int SurvivingVerifiedMembers { get; }
    public long ActuallyReclaimedBytes { get; }
}

public sealed class CleanupResult
{
    internal CleanupResult(IReadOnlyList<CleanupGroupResult> groups, bool wasCancelled)
    {
        Groups = groups;
        WasCancelled = wasCancelled;
        RequestedCandidateCount = groups.Sum(group => group.RequestedCandidateCount);
        RecycledFileCount = groups.Sum(group => group.RecycledFileCount);
        SkippedFileCount = groups.Sum(group => group.SkippedFileCount);
        FailedFileCount = groups.Sum(group => group.FailedFileCount);
        ActuallyReclaimedBytes = groups.Aggregate(0L, (total, group) => checked(total + group.ActuallyReclaimedBytes));
    }

    public IReadOnlyList<CleanupGroupResult> Groups { get; }
    public int RequestedCandidateCount { get; }
    public int RecycledFileCount { get; }
    public int SkippedFileCount { get; }
    public int FailedFileCount { get; }
    public long ActuallyReclaimedBytes { get; }
    public bool WasCancelled { get; }
}

public sealed record CleanupProgress(
    int CandidatesProcessed,
    int CandidatesTotal,
    int RecycledCount,
    int SkippedCount,
    int FailedCount,
    long ActuallyReclaimedBytes);
