using System.ComponentModel;
using System.Runtime.CompilerServices;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.SimilarRemoval;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.App.SimilarPhotos;

public enum SimilarPhotoRemovalWorkflowState { Ready, Reviewing, Executing, Completed, Cancelled, Failed }

public sealed class SimilarPhotoRemovalWorkflowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SimilarPhotoRemovalEngine engine;
    private SimilarPhotoRemovalIntent? intent;
    private SimilarPhotosReviewViewModel? sourceReview;
    private CancellationTokenSource? cancellation;
    private SimilarPhotoRemovalWorkflowState state;
    private SimilarPhotoRemovalResult? result;
    private bool disposed;

    public SimilarPhotoRemovalWorkflowViewModel(SimilarPhotoRemovalEngine engine) =>
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public event PropertyChangedEventHandler? PropertyChanged;
    public SimilarPhotoRemovalWorkflowState State { get => state; private set => SetField(ref state, value); }
    public IReadOnlyList<SimilarPhotoRemovalReviewGroup> ReviewGroups { get; private set; } = [];
    public IReadOnlyList<SimilarPhotoRemovalReviewItem> RemovalItems { get; private set; } = [];
    public IReadOnlyList<SimilarPhotoRemovalReviewItem> RemainingItems { get; private set; } = [];
    public int AffectedGroupCount { get; private set; }
    public int SelectedPhotoCount => RemovalItems.Count;
    public long SelectedBytes { get; private set; }
    public int RemainingPhotoCount { get; private set; }
    public int LocationCount { get; private set; }
    public SimilarPhotoRemovalResult? Result => result;
    public bool IsActive => State == SimilarPhotoRemovalWorkflowState.Executing;

    public bool BeginReview(SimilarPhotosReviewViewModel review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (disposed || IsActive || !review.CanReviewRemoval) return false;
        SimilarPhotoRemovalIntent captured = review.CreateRemovalIntent();
        SimilarPhotoRemovalPlanningResult planning = SimilarPhotoRemovalPlanner.CreatePlan(captured);
        if (!planning.Succeeded || planning.Plan is null) return false;

        intent = captured;
        sourceReview = review;
        SimilarPhotoRemovalPlan plan = planning.Plan;
        ReviewGroups = Array.AsReadOnly(plan.Groups.Select(group => new SimilarPhotoRemovalReviewGroup(
            group.GroupIndex + 1,
            group.Tier,
            Array.AsReadOnly(group.Survivors.Select(member => new SimilarPhotoRemovalReviewItem(member.ExpectedFile, group.GroupIndex + 1, group.Tier, false)).ToArray()),
            Array.AsReadOnly(group.Candidates.Select(member => new SimilarPhotoRemovalReviewItem(member.ExpectedFile, group.GroupIndex + 1, group.Tier, true)).ToArray()))).ToArray());
        RemovalItems = Array.AsReadOnly(ReviewGroups.SelectMany(group => group.Removing).ToArray());
        RemainingItems = Array.AsReadOnly(ReviewGroups.SelectMany(group => group.Remaining).ToArray());
        AffectedGroupCount = ReviewGroups.Count;
        SelectedBytes = plan.SelectedBytes;
        RemainingPhotoCount = ReviewGroups.Sum(group => group.Remaining.Count);
        LocationCount = RemovalItems.Select(item => Path.GetDirectoryName(item.File.NormalizedPath) ?? item.File.NormalizedPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        result = null;
        RaiseSummaryProperties();
        Raise(nameof(Result));
        State = SimilarPhotoRemovalWorkflowState.Reviewing;
        return true;
    }

    public async Task<SimilarPhotoRemovalResult?> ExecuteConfirmedAsync(
        IProgress<SimilarPhotoRemovalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (disposed || State != SimilarPhotoRemovalWorkflowState.Reviewing || intent is null || sourceReview is null) return null;
        SimilarPhotoRemovalPlanningResult planning = SimilarPhotoRemovalPlanner.CreatePlan(intent);
        if (!planning.Succeeded || planning.Plan is null) { State = SimilarPhotoRemovalWorkflowState.Failed; return null; }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation = linked;
        sourceReview.MarkStale();
        State = SimilarPhotoRemovalWorkflowState.Executing;
        try
        {
            result = await engine.ExecuteAsync(planning.Plan, progress, linked.Token);
            Raise(nameof(Result));
            State = result.WasCancelled ? SimilarPhotoRemovalWorkflowState.Cancelled : SimilarPhotoRemovalWorkflowState.Completed;
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { State = SimilarPhotoRemovalWorkflowState.Cancelled; return null; }
        catch { State = SimilarPhotoRemovalWorkflowState.Failed; return null; }
        finally { cancellation = null; }
    }

    public void Cancel() => cancellation?.Cancel();
    public void ReturnToResults() { if (!IsActive) State = SimilarPhotoRemovalWorkflowState.Ready; }
    public void Reset()
    {
        Cancel(); intent = null; sourceReview = null; ReviewGroups = []; RemovalItems = []; RemainingItems = [];
        AffectedGroupCount = 0; SelectedBytes = 0; RemainingPhotoCount = 0; LocationCount = 0; result = null;
        RaiseSummaryProperties(); Raise(nameof(Result)); State = SimilarPhotoRemovalWorkflowState.Ready;
    }
    public void Dispose() { if (disposed) return; disposed = true; Cancel(); }

    private void RaiseSummaryProperties()
    {
        Raise(nameof(ReviewGroups)); Raise(nameof(RemovalItems)); Raise(nameof(RemainingItems)); Raise(nameof(AffectedGroupCount));
        Raise(nameof(SelectedPhotoCount)); Raise(nameof(SelectedBytes)); Raise(nameof(RemainingPhotoCount)); Raise(nameof(LocationCount));
    }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); Raise(nameof(IsActive)); return true;
    }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class SimilarPhotoRemovalReviewItem : INotifyPropertyChanged
{
    private string dimensions = string.Empty;
    public SimilarPhotoRemovalReviewItem(DiscoveredFile file, int groupNumber, SimilarityTier tier, bool isRemoving)
    {
        File = file; GroupNumber = groupNumber; Tier = tier; IsRemoving = isRemoving;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public DiscoveredFile File { get; }
    public int GroupNumber { get; }
    public SimilarityTier Tier { get; }
    public bool IsRemoving { get; }
    public string TierLabel => Tier switch { SimilarityTier.VerySimilar => "Very similar", SimilarityTier.Similar => "Similar", _ => "Loosely similar" };
    public string Location => Path.GetDirectoryName(File.NormalizedPath) ?? File.NormalizedPath;
    public string AccessibleName => $"{File.FileName}, group {GroupNumber}, {TierLabel}, {(IsRemoving ? "removing" : "remaining")}";
    public string Dimensions => dimensions;
    public void SetDimensions(uint width, uint height)
    {
        string value = width > 0 && height > 0 ? $"{width:N0} × {height:N0}" : string.Empty;
        if (dimensions == value) return;
        dimensions = value;
        PropertyChanged?.Invoke(this, new(nameof(Dimensions)));
    }
}

public sealed record SimilarPhotoRemovalReviewGroup(
    int GroupNumber,
    SimilarityTier Tier,
    IReadOnlyList<SimilarPhotoRemovalReviewItem> Remaining,
    IReadOnlyList<SimilarPhotoRemovalReviewItem> Removing);
