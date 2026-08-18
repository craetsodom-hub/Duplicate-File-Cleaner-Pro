using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Core.SimilarRemoval;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.App.SimilarPhotos;

/// <summary>Session-only, non-destructive presentation state for visual similarity review.</summary>
public sealed class SimilarPhotosReviewViewModel : INotifyPropertyChanged
{
    private readonly List<SimilarPhotoGroupViewModel> allGroups;
    private string searchText = string.Empty;
    private SimilarityTier? tierFilter;
    private SimilarPhotoSortOption sortOption = SimilarPhotoSortOption.Tier;
    private bool descending = true;
    private SimilarPhotoGroupViewModel? activeGroup;
    private SimilarPhotoItemViewModel? leftPhoto;
    private SimilarPhotoItemViewModel? rightPhoto;
    private bool isStale;

    public SimilarPhotosReviewViewModel(CompletedSimilarPhotoScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
        allGroups = result.Analysis.Groups.Select((group, index) => new SimilarPhotoGroupViewModel(this, group, index)).ToList();
        VisibleGroups = [];
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public CompletedSimilarPhotoScanResult Result { get; }
    public ObservableCollection<SimilarPhotoGroupViewModel> VisibleGroups { get; }
    public IReadOnlyList<SimilarPhotoGroupViewModel> AllGroups => allGroups;
    public int GroupCount => allGroups.Count;
    public int PhotoCount => allGroups.Sum(group => group.Photos.Count);
    public int AnalyzedPhotoCount => Result.Analysis.EligiblePhotoCount;
    public bool HasResults => allGroups.Count > 0;
    public bool HasVisibleGroups => VisibleGroups.Count > 0;
    public int VerySimilarCount => allGroups.Count(group => group.Tier == SimilarityTier.VerySimilar);
    public SimilarPhotoGroupViewModel? ActiveGroup { get => activeGroup; private set { if (activeGroup == value) return; activeGroup = value; OnChanged(); } }
    public SimilarPhotoItemViewModel? LeftPhoto { get => leftPhoto; private set { if (leftPhoto == value) return; leftPhoto = value; OnChanged(); OnChanged(nameof(CanCompare)); } }
    public SimilarPhotoItemViewModel? RightPhoto { get => rightPhoto; private set { if (rightPhoto == value) return; rightPhoto = value; OnChanged(); OnChanged(nameof(CanCompare)); } }
    public bool CanCompare => LeftPhoto is not null && RightPhoto is not null && LeftPhoto != RightPhoto;
    public int MarkedForRemovalCount => allGroups.Sum(group => group.Photos.Count(photo => photo.Mark == SimilarPhotoReviewMark.ConsiderRemoving));
    public long MarkedForRemovalBytes => allGroups.SelectMany(group => group.Photos).Where(photo => photo.Mark == SimilarPhotoReviewMark.ConsiderRemoving).Sum(photo => photo.File.Length);
    public bool IsStale => isStale;
    public bool CanReviewRemoval => !isStale && MarkedForRemovalCount > 0;
    public string SearchText { get => searchText; set { if (searchText == value) return; searchText = value ?? string.Empty; Refresh(); OnChanged(); } }
    public SimilarityTier? TierFilter { get => tierFilter; set { if (tierFilter == value) return; tierFilter = value; Refresh(); OnChanged(); } }
    public SimilarPhotoSortOption SortOption { get => sortOption; set { if (sortOption == value) return; sortOption = value; Refresh(); OnChanged(); } }
    public bool Descending { get => descending; set { if (descending == value) return; descending = value; Refresh(); OnChanged(); } }

    public void SelectGroup(SimilarPhotoGroupViewModel group)
    {
        if (!allGroups.Contains(group)) return;
        ActiveGroup = group;
        if (LeftPhoto is not null && !group.Photos.Contains(LeftPhoto)) { LeftPhoto = null; RightPhoto = null; }
    }

    public void ChooseLeft(SimilarPhotoItemViewModel photo) { if (IsInActiveGroup(photo)) LeftPhoto = photo; }
    public void ChooseRight(SimilarPhotoItemViewModel photo) { if (IsInActiveGroup(photo) && photo != LeftPhoto) RightPhoto = photo; }
    public void Swap() { if (!CanCompare) return; (LeftPhoto, RightPhoto) = (RightPhoto, LeftPhoto); }
    public void ClearMarks() { foreach (SimilarPhotoGroupViewModel group in allGroups) foreach (SimilarPhotoItemViewModel photo in group.Photos) photo.SetMarkCore(SimilarPhotoReviewMark.None); NotifyMarksChanged(); }
    public void ClearFilters() { SearchText = string.Empty; TierFilter = null; }
    public SimilarPhotoRemovalIntent CreateRemovalIntent() => new(
        Result,
        allGroups.SelectMany(group => group.Photos)
            .Where(photo => photo.Mark == SimilarPhotoReviewMark.ConsiderRemoving)
            .Select(photo => photo.File.PhysicalIdentity));

    public void MarkStale()
    {
        if (isStale) return;
        foreach (SimilarPhotoGroupViewModel group in allGroups)
        foreach (SimilarPhotoItemViewModel photo in group.Photos)
            photo.SetMarkCore(SimilarPhotoReviewMark.None);
        isStale = true;
        OnChanged(nameof(IsStale));
        NotifyMarksChanged();
    }

    internal void NotifyMarksChanged()
    {
        OnChanged(nameof(MarkedForRemovalCount));
        OnChanged(nameof(MarkedForRemovalBytes));
        OnChanged(nameof(CanReviewRemoval));
    }

    private bool IsInActiveGroup(SimilarPhotoItemViewModel photo) => ActiveGroup is not null && ActiveGroup.Photos.Contains(photo);
    private void Refresh()
    {
        IEnumerable<SimilarPhotoGroupViewModel> query = allGroups.Where(group =>
            (tierFilter is null || group.Tier == tierFilter) &&
            (string.IsNullOrWhiteSpace(searchText) || group.Photos.Any(photo => photo.Matches(searchText))));
        IOrderedEnumerable<SimilarPhotoGroupViewModel> ordered = sortOption switch
        {
            SimilarPhotoSortOption.PhotoCount => descending ? query.OrderByDescending(group => group.Photos.Count) : query.OrderBy(group => group.Photos.Count),
            SimilarPhotoSortOption.Activity => descending ? query.OrderByDescending(group => group.NewestModified) : query.OrderBy(group => group.NewestModified),
            SimilarPhotoSortOption.TotalSize => descending ? query.OrderByDescending(group => group.TotalSize) : query.OrderBy(group => group.TotalSize),
            _ => descending ? query.OrderByDescending(group => group.Tier).ThenBy(group => group.DisplayName) : query.OrderBy(group => group.Tier).ThenBy(group => group.DisplayName),
        };
        VisibleGroups.Clear(); foreach (SimilarPhotoGroupViewModel group in ordered) VisibleGroups.Add(group);
        OnChanged(nameof(HasVisibleGroups));
    }
    private void OnChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum SimilarPhotoSortOption { Tier, PhotoCount, Activity, TotalSize }
public enum SimilarPhotoReviewMark { None, Keep, ConsiderRemoving }

public sealed class SimilarPhotoGroupViewModel
{
    internal SimilarPhotoGroupViewModel(SimilarPhotosReviewViewModel owner, SimilarPhotoGroup group, int index)
    {
        Owner = owner; Group = group; Index = index;
        Photos = group.Photos.Select(file => new SimilarPhotoItemViewModel(this, file)).ToList().AsReadOnly();
    }
    internal SimilarPhotosReviewViewModel Owner { get; }
    public SimilarPhotoGroup Group { get; }
    public int Index { get; }
    public IReadOnlyList<SimilarPhotoItemViewModel> Photos { get; }
    public SimilarityTier Tier => Group.Tier;
    public string TierLabel => Tier switch { SimilarityTier.VerySimilar => "Very similar", SimilarityTier.Similar => "Similar", _ => "Loosely similar" };
    public string DisplayName => Group.Representative.FileName;
    public long TotalSize => Photos.Sum(photo => photo.File.Length);
    public DateTimeOffset NewestModified => Photos.Max(photo => photo.File.LastWriteTimeUtc);
    public string AccessibleName => $"{Photos.Count} visually similar photos, {TierLabel}";
}

public sealed class SimilarPhotoItemViewModel : INotifyPropertyChanged
{
    private SimilarPhotoReviewMark mark;
    internal SimilarPhotoItemViewModel(SimilarPhotoGroupViewModel group, DiscoveredFile file) { Group = group; File = file; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public SimilarPhotoGroupViewModel Group { get; }
    public DiscoveredFile File { get; }
    public SimilarPhotoReviewMark Mark => mark;
    public string MarkLabel => mark switch { SimilarPhotoReviewMark.Keep => "Keep", SimilarPhotoReviewMark.ConsiderRemoving => "Consider removing", _ => "Unmarked" };
    public string AccessibleName => $"{File.FileName}, {MarkLabel}, {Group.TierLabel}";
    public bool Matches(string text) => File.FileName.Contains(text, StringComparison.OrdinalIgnoreCase) || File.NormalizedPath.Contains(text, StringComparison.OrdinalIgnoreCase);
    public bool SetMark(SimilarPhotoReviewMark value)
    {
        if (Group.Owner.IsStale) return false;
        if (value == SimilarPhotoReviewMark.ConsiderRemoving && Group.Photos.All(photo => photo == this || photo.Mark == SimilarPhotoReviewMark.ConsiderRemoving)) return false;
        if (!SetMarkCore(value)) return true;
        Group.Owner.NotifyMarksChanged();
        return true;
    }

    internal bool SetMarkCore(SimilarPhotoReviewMark value)
    {
        if (mark == value) return false;
        mark = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mark)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
        return true;
    }
}
