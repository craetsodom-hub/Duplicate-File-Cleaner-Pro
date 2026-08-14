using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Core.Cleanup;

namespace DuplicateFileCleanerPro.App.Results;

/// <summary>
/// Session-only presentation state layered over the immutable verified scan result.
/// It deliberately never changes the Core snapshot or reconstructs physical identity from paths.
/// </summary>
public sealed class ResultsReviewViewModel : INotifyPropertyChanged
{
    private readonly List<ResultGroupViewModel> allGroups;
    private string searchText = string.Empty;
    private ResultSortOption sortOption = ResultSortOption.ReclaimableBytes;
    private bool sortDescending = true;
    private ResultFilterOption filterOption = ResultFilterOption.AllGroups;

    public ResultsReviewViewModel(CompletedScanResult completedResult)
    {
        CompletedResult = completedResult ?? throw new ArgumentNullException(nameof(completedResult));
        allGroups = completedResult.Detection.Groups
            .Select((group, index) => new ResultGroupViewModel(this, group, index))
            .ToList();
        VisibleGroups = new ObservableCollection<ResultGroupViewModel>();
        RefreshVisibleGroups();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CompletedScanResult CompletedResult { get; }
    public ObservableCollection<ResultGroupViewModel> VisibleGroups { get; }
    public IReadOnlyList<ResultGroupViewModel> AllGroups => allGroups;
    public int DuplicateGroupCount => allGroups.Count;
    public int VerifiedMemberCount => allGroups.Sum(group => group.Members.Count);
    public long ReclaimableBytes => CompletedResult.Detection.TotalReclaimableBytes;
    public int SkippedItemCount => CompletedResult.Discovery.SkippedItems.Count + CompletedResult.Detection.SkippedItems.Count;
    public int SelectedCandidateCount => allGroups.Sum(group => group.SelectedCandidateCount);
    public long SelectedCandidateBytes => allGroups.Sum(group => group.SelectedCandidateBytes);
    public bool HasResults => allGroups.Count > 0;
    public bool HasVisibleGroups => VisibleGroups.Count > 0;

    public string SearchText
    {
        get => searchText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(searchText, value, StringComparison.Ordinal)) return;
            searchText = value;
            OnPropertyChanged();
            RefreshVisibleGroups();
        }
    }

    public ResultSortOption SortOption
    {
        get => sortOption;
        set
        {
            if (sortOption == value) return;
            sortOption = value;
            OnPropertyChanged();
            RefreshVisibleGroups();
        }
    }

    public bool SortDescending
    {
        get => sortDescending;
        set
        {
            if (sortDescending == value) return;
            sortDescending = value;
            OnPropertyChanged();
            RefreshVisibleGroups();
        }
    }

    public ResultFilterOption FilterOption
    {
        get => filterOption;
        set
        {
            if (filterOption == value) return;
            filterOption = value;
            OnPropertyChanged();
            RefreshVisibleGroups();
        }
    }

    public void ExpandAll()
    {
        foreach (ResultGroupViewModel group in allGroups) group.IsExpanded = true;
    }

    public void CollapseAll()
    {
        foreach (ResultGroupViewModel group in allGroups) group.IsExpanded = false;
    }

    public ReviewSelectionHandoff CreateSelectionHandoff() => new(
        CompletedResult,
        allGroups.SelectMany(group => group.Members).Where(member => member.IsSelected).Select(member => member.File.PhysicalIdentity));

    internal bool TrySetSelection(ResultMemberViewModel member, bool selected)
    {
        ResultGroupViewModel group = member.Group;
        if (selected && !member.IsSelected && group.SelectedCandidateCount >= group.Members.Count - 1)
        {
            member.NotifySelectionRejected();
            return false;
        }

        if (member.SetSelection(selected))
        {
            OnPropertyChanged(nameof(SelectedCandidateCount));
            OnPropertyChanged(nameof(SelectedCandidateBytes));
            RefreshVisibleGroups();
        }

        return true;
    }

    private void RefreshVisibleGroups()
    {
        IEnumerable<ResultGroupViewModel> query = allGroups;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(group => group.Members.Any(member =>
                member.File.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                member.File.NormalizedPath.Contains(searchText, StringComparison.OrdinalIgnoreCase)));
        }

        if (filterOption == ResultFilterOption.SelectedGroups)
        {
            query = query.Where(group => group.SelectedCandidateCount > 0);
        }

        IOrderedEnumerable<ResultGroupViewModel> ordered = (sortOption, sortDescending) switch
        {
            (ResultSortOption.FileSize, true) => query.OrderByDescending(group => group.FileSize),
            (ResultSortOption.FileSize, false) => query.OrderBy(group => group.FileSize),
            (ResultSortOption.CopyCount, true) => query.OrderByDescending(group => group.Members.Count),
            (ResultSortOption.CopyCount, false) => query.OrderBy(group => group.Members.Count),
            (ResultSortOption.Name, true) => query.OrderByDescending(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            (ResultSortOption.Name, false) => query.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase),
            (ResultSortOption.Path, true) => query.OrderByDescending(group => group.Representative.File.NormalizedPath, StringComparer.OrdinalIgnoreCase),
            (ResultSortOption.Path, false) => query.OrderBy(group => group.Representative.File.NormalizedPath, StringComparer.OrdinalIgnoreCase),
            (_, true) => query.OrderByDescending(group => group.ReclaimableBytes),
            _ => query.OrderBy(group => group.ReclaimableBytes),
        };

        // A stable path/index tie break makes every view deterministic irrespective of LINQ implementation.
        List<ResultGroupViewModel> replacement = ordered
            .ThenBy(group => group.Representative.File.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Index)
            .ToList();
        VisibleGroups.Clear();
        foreach (ResultGroupViewModel group in replacement) VisibleGroups.Add(group);
        OnPropertyChanged(nameof(HasVisibleGroups));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum ResultSortOption { ReclaimableBytes, FileSize, CopyCount, Name, Path }
public enum ResultFilterOption { AllGroups, SelectedGroups }

public sealed class ResultGroupViewModel : INotifyPropertyChanged
{
    private bool isExpanded;

    internal ResultGroupViewModel(ResultsReviewViewModel owner, DuplicateFileGroup group, int index)
    {
        Owner = owner;
        SnapshotGroup = group;
        Index = index;
        Members = group.Files.Select(file => new ResultMemberViewModel(this, file)).ToList().AsReadOnly();
        Representative = Members[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal ResultsReviewViewModel Owner { get; }
    public DuplicateFileGroup SnapshotGroup { get; }
    public int Index { get; }
    public IReadOnlyList<ResultMemberViewModel> Members { get; }
    public ResultMemberViewModel Representative { get; }
    public string DisplayName => Representative.File.FileName;
    public long FileSize => Representative.File.Length;
    public long ReclaimableBytes => SnapshotGroup.ReclaimableBytes;
    public int SelectedCandidateCount => Members.Count(member => member.IsSelected);
    public long SelectedCandidateBytes => Members.Where(member => member.IsSelected).Sum(member => member.File.Length);

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value) return;
            isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    internal void NotifySelectionChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCandidateCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCandidateBytes)));
    }
}

public sealed class ResultMemberViewModel : INotifyPropertyChanged
{
    private bool isSelected;

    internal ResultMemberViewModel(ResultGroupViewModel group, DiscoveredFile file)
    {
        Group = group;
        File = file;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal ResultGroupViewModel Group { get; }
    public DiscoveredFile File { get; }

    public bool IsSelected
    {
        get => isSelected;
        set => Group.Owner.TrySetSelection(this, value);
    }

    internal bool SetSelection(bool value)
    {
        if (isSelected == value) return false;
        isSelected = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        Group.NotifySelectionChanged();
        return true;
    }

    internal void NotifySelectionRejected() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
}

/// <summary>Immutable intent handoff for a future cleanup phase. It grants no authority to mutate files.</summary>
public sealed class ReviewSelectionHandoff
{
    public ReviewSelectionHandoff(CompletedScanResult verifiedResult, IEnumerable<PhysicalFileIdentity> selectedPhysicalMembers)
    {
        VerifiedResult = verifiedResult ?? throw new ArgumentNullException(nameof(verifiedResult));
        SelectedPhysicalMembers = Array.AsReadOnly((selectedPhysicalMembers ?? throw new ArgumentNullException(nameof(selectedPhysicalMembers))).Distinct().ToArray());
    }

    public CompletedScanResult VerifiedResult { get; }
    public IReadOnlyList<PhysicalFileIdentity> SelectedPhysicalMembers { get; }

    public CleanupSelectionIntent CreateCleanupIntent() => new(VerifiedResult, SelectedPhysicalMembers);
}

public static class ResultDisplayFormatter
{
    public static string FormatBytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? string.Format(culture, "{0:N0} {1}", value, units[unit]) : string.Format(culture, "{0:N1} {1}", value, units[unit]);
    }

    public static string FormatDateTime(DateTimeOffset value, CultureInfo? culture = null) => value.ToLocalTime().ToString("g", culture ?? CultureInfo.CurrentCulture);
}
