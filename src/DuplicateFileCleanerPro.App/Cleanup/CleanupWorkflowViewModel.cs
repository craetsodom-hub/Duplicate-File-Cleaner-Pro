using System.ComponentModel;
using System.Runtime.CompilerServices;
using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.App.Cleanup;

public enum CleanupWorkflowState
{
    ReadyToReview,
    Reviewing,
    Preparing,
    Cleaning,
    Completed,
    Cancelled,
    Failed,
}

public sealed class CleanupWorkflowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CleanupEngine engine;
    private ReviewSelectionHandoff? handoff;
    private CancellationTokenSource? cancellation;
    private CleanupWorkflowState state = CleanupWorkflowState.ReadyToReview;
    private CleanupResult? result;
    private string? planningFailureKey;
    private bool requiresRescan;
    private bool disposed;

    public CleanupWorkflowViewModel(CleanupEngine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CleanupWorkflowState State
    {
        get => state;
        private set => SetField(ref state, value);
    }

    public IReadOnlyList<CleanupReviewCandidate> ReviewCandidates { get; private set; } = [];
    public int SelectedCandidateCount => ReviewCandidates.Count;
    public long SelectedCandidateBytes { get; private set; }
    public int AffectedGroupCount { get; private set; }
    public CleanupResult? Result => result;
    public string? PlanningFailureKey => planningFailureKey;
    public bool RequiresRescan => requiresRescan;
    public bool IsActive => State is CleanupWorkflowState.Preparing or CleanupWorkflowState.Cleaning;
    public bool IsReviewing => State == CleanupWorkflowState.Reviewing;
    public bool HasCompletedResult => Result is not null && State is (CleanupWorkflowState.Completed or CleanupWorkflowState.Cancelled);

    /// <summary>Captures Phase 5's immutable physical-identity selection for a single review attempt.</summary>
    public bool BeginReview(ResultsReviewViewModel review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (IsActive || requiresRescan || review.SelectedCandidateCount == 0)
        {
            return false;
        }

        handoff = review.CreateSelectionHandoff();
        HashSet<PhysicalFileIdentity> selected = handoff.SelectedPhysicalMembers.ToHashSet();
        var candidates = new List<CleanupReviewCandidate>();
        for (int groupIndex = 0; groupIndex < handoff.VerifiedResult.Detection.Groups.Count; groupIndex++)
        {
            foreach (var file in handoff.VerifiedResult.Detection.Groups[groupIndex].Files)
            {
                if (selected.Contains(file.PhysicalIdentity))
                {
                    candidates.Add(new CleanupReviewCandidate(file, groupIndex + 1));
                }
            }
        }

        ReviewCandidates = Array.AsReadOnly(candidates
            .OrderBy(candidate => candidate.GroupNumber)
            .ThenBy(candidate => candidate.File.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        SelectedCandidateBytes = candidates.Aggregate(0L, (total, candidate) => checked(total + candidate.File.Length));
        AffectedGroupCount = candidates.Select(candidate => candidate.GroupNumber).Distinct().Count();
        result = null;
        planningFailureKey = null;
        RaiseReviewProperties();
        State = CleanupWorkflowState.Reviewing;
        return true;
    }

    /// <summary>Creates a Core plan from the captured handoff and delegates all safety decisions to Phase 6.</summary>
    public async Task<CleanupResult?> ExecuteConfirmedAsync(
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (State != CleanupWorkflowState.Reviewing || handoff is null || disposed)
        {
            return null;
        }

        State = CleanupWorkflowState.Preparing;
        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(handoff.CreateCleanupIntent());
        if (!planning.Succeeded || planning.Plan is null)
        {
            planningFailureKey = "CleanupPlanningRejected";
            RaisePropertyChanged(nameof(PlanningFailureKey));
            State = CleanupWorkflowState.Failed;
            return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation = linkedCancellation;
        RaisePropertyChanged(nameof(IsActive));
        try
        {
            State = CleanupWorkflowState.Cleaning;
            result = await engine.ExecuteAsync(planning.Plan, progress, linkedCancellation.Token);
            requiresRescan = true;
            RaisePropertyChanged(nameof(Result));
            RaisePropertyChanged(nameof(RequiresRescan));
            State = result.WasCancelled ? CleanupWorkflowState.Cancelled : CleanupWorkflowState.Completed;
            return result;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            requiresRescan = true;
            RaisePropertyChanged(nameof(RequiresRescan));
            State = CleanupWorkflowState.Cancelled;
            return null;
        }
        catch
        {
            planningFailureKey = "CleanupUnexpectedFailure";
            RaisePropertyChanged(nameof(PlanningFailureKey));
            State = CleanupWorkflowState.Failed;
            return null;
        }
        finally
        {
            cancellation = null;
            RaisePropertyChanged(nameof(IsActive));
        }
    }

    public void Cancel() => cancellation?.Cancel();

    public void ReturnToResults()
    {
        if (IsActive)
        {
            return;
        }

        State = CleanupWorkflowState.ReadyToReview;
    }

    public void ResetForNewScan()
    {
        Cancel();
        handoff = null;
        ReviewCandidates = [];
        SelectedCandidateBytes = 0;
        AffectedGroupCount = 0;
        result = null;
        planningFailureKey = null;
        requiresRescan = false;
        RaiseReviewProperties();
        RaisePropertyChanged(nameof(Result));
        RaisePropertyChanged(nameof(PlanningFailureKey));
        RaisePropertyChanged(nameof(RequiresRescan));
        State = CleanupWorkflowState.ReadyToReview;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Cancel();
    }

    private void RaiseReviewProperties()
    {
        RaisePropertyChanged(nameof(ReviewCandidates));
        RaisePropertyChanged(nameof(SelectedCandidateCount));
        RaisePropertyChanged(nameof(SelectedCandidateBytes));
        RaisePropertyChanged(nameof(AffectedGroupCount));
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        if (propertyName == nameof(State))
        {
            RaisePropertyChanged(nameof(IsActive));
            RaisePropertyChanged(nameof(IsReviewing));
            RaisePropertyChanged(nameof(HasCompletedResult));
        }

        return true;
    }
}

public sealed record CleanupReviewCandidate(DiscoveredFile File, int GroupNumber);

public enum CleanupOutcomeTone
{
    Success,
    Skipped,
    Failed,
    Cancelled,
}

public sealed record CleanupOutcomePresentation(string MessageKey, CleanupOutcomeTone Tone);

/// <summary>Centralized user-facing category mapping; App displays it but does not make safety decisions.</summary>
public static class CleanupOutcomePresentationMapper
{
    public static CleanupOutcomePresentation Map(CleanupCandidateOutcomeStatus status) => status switch
    {
        CleanupCandidateOutcomeStatus.Recycled => new("CleanupOutcomeMoved", CleanupOutcomeTone.Success),
        CleanupCandidateOutcomeStatus.SkippedMissing => new("CleanupOutcomeMissing", CleanupOutcomeTone.Skipped),
        CleanupCandidateOutcomeStatus.SkippedIdentityMismatch or CleanupCandidateOutcomeStatus.SkippedChanged => new("CleanupOutcomeChanged", CleanupOutcomeTone.Skipped),
        CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable => new("CleanupOutcomeKeeper", CleanupOutcomeTone.Skipped),
        CleanupCandidateOutcomeStatus.SkippedVerificationFailed => new("CleanupOutcomeVerification", CleanupOutcomeTone.Skipped),
        CleanupCandidateOutcomeStatus.SkippedPolicy => new("CleanupOutcomePolicy", CleanupOutcomeTone.Skipped),
        CleanupCandidateOutcomeStatus.FailedRecycleBin => new("CleanupOutcomeRecycleBinFailed", CleanupOutcomeTone.Failed),
        CleanupCandidateOutcomeStatus.Cancelled => new("CleanupOutcomeCancelled", CleanupOutcomeTone.Cancelled),
        _ => new("CleanupOutcomePlatformFailed", CleanupOutcomeTone.Failed),
    };
}
