using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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
    private ResultFileTypeFilter fileTypeFilter = ResultFileTypeFilter.All;
    private ResultSizeFilter sizeFilter = ResultSizeFilter.Any;
    private string locationFilter = ResultLocationFilter.AllLocationsId;
    private long customMinimumSizeBytes;
    private long? customMaximumSizeBytes;
    private SelectionAssistantUndoState? lastAssistantUndo;
    private ResultMemberViewModel? activeMember;

    public ResultsReviewViewModel(CompletedScanResult completedResult)
    {
        CompletedResult = completedResult ?? throw new ArgumentNullException(nameof(completedResult));
        allGroups = completedResult.Detection.Groups
            .Select((group, index) => new ResultGroupViewModel(this, group, index))
            .ToList();
        VisibleGroups = new ObservableCollection<ResultGroupViewModel>();
        Locations = Array.AsReadOnly(BuildLocations(completedResult, allGroups).ToArray());
        RefreshVisibleGroups();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised when presentation prevents selecting every independent member of a group.</summary>
    public event EventHandler? SelectionRejected;

    public CompletedScanResult CompletedResult { get; }
    public ObservableCollection<ResultGroupViewModel> VisibleGroups { get; }
    public IReadOnlyList<ResultGroupViewModel> AllGroups => allGroups;
    public IReadOnlyList<ResultLocationFilter> Locations { get; }
    public int DuplicateGroupCount => allGroups.Count;
    public int VerifiedMemberCount => allGroups.Sum(group => group.Members.Count);
    public long ReclaimableBytes => CompletedResult.Detection.TotalReclaimableBytes;
    public int SkippedItemCount => CompletedResult.Discovery.SkippedItems.Count + CompletedResult.Detection.SkippedItems.Count;
    public int SelectedCandidateCount => allGroups.Sum(group => group.SelectedCandidateCount);
    public long SelectedCandidateBytes => allGroups.Sum(group => group.SelectedCandidateBytes);
    public bool HasResults => allGroups.Count > 0;
    public bool HasVisibleGroups => VisibleGroups.Count > 0;
    public int ActiveFilterCount =>
        (!string.IsNullOrWhiteSpace(SearchText) ? 1 : 0)
        + (FilterOption != ResultFilterOption.AllGroups ? 1 : 0)
        + (FileTypeFilter != ResultFileTypeFilter.All ? 1 : 0)
        + (SizeFilter != ResultSizeFilter.Any ? 1 : 0)
        + (!string.Equals(LocationFilter, ResultLocationFilter.AllLocationsId, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
    public bool HasActiveFilters => ActiveFilterCount > 0;
    public bool CanUndoSelectionAssistant => lastAssistantUndo is not null;
    public ResultMemberViewModel? ActiveMember
    {
        get => activeMember;
        set
        {
            if (ReferenceEquals(activeMember, value)) return;
            activeMember = value;
            OnPropertyChanged();
        }
    }

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

    public ResultFileTypeFilter FileTypeFilter
    {
        get => fileTypeFilter;
        set
        {
            if (fileTypeFilter == value) return;
            fileTypeFilter = value;
            OnPropertyChanged();
            RefreshVisibleGroups();
        }
    }

    public ResultSizeFilter SizeFilter
    {
        get => sizeFilter;
        set
        {
            if (sizeFilter == value) return;
            sizeFilter = value;
            OnPropertyChanged();
            RefreshVisibleGroups();
        }
    }

    public string LocationFilter
    {
        get => locationFilter;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? ResultLocationFilter.AllLocationsId : value;
            if (string.Equals(locationFilter, value, StringComparison.OrdinalIgnoreCase)) return;
            locationFilter = value;
            OnPropertyChanged();
            RefreshVisibleGroups();
        }
    }

    public void SetCustomSizeRange(long minimumSizeBytes, long? maximumSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumSizeBytes);
        if (maximumSizeBytes is < 0 || maximumSizeBytes < minimumSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSizeBytes));
        }

        customMinimumSizeBytes = minimumSizeBytes;
        customMaximumSizeBytes = maximumSizeBytes;
        SizeFilter = ResultSizeFilter.Custom;
    }

    public void ClearFilters()
    {
        bool changed = !string.IsNullOrWhiteSpace(searchText)
            || filterOption != ResultFilterOption.AllGroups
            || fileTypeFilter != ResultFileTypeFilter.All
            || sizeFilter != ResultSizeFilter.Any
            || !string.Equals(locationFilter, ResultLocationFilter.AllLocationsId, StringComparison.OrdinalIgnoreCase);
        searchText = string.Empty;
        filterOption = ResultFilterOption.AllGroups;
        fileTypeFilter = ResultFileTypeFilter.All;
        sizeFilter = ResultSizeFilter.Any;
        locationFilter = ResultLocationFilter.AllLocationsId;
        if (!changed) return;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(FilterOption));
        OnPropertyChanged(nameof(FileTypeFilter));
        OnPropertyChanged(nameof(SizeFilter));
        OnPropertyChanged(nameof(LocationFilter));
        RefreshVisibleGroups();
    }

    public void ClearSelections()
    {
        foreach (ResultMemberViewModel member in allGroups.SelectMany(group => group.Members))
        {
            member.SetSelection(false);
        }

        lastAssistantUndo = null;
        NotifySelectionStateChanged();
    }

    public SelectionAssistantProposal CreateSelectionAssistantProposal(SelectionAssistantRule rule, string? preferredLocation = null, bool currentFilteredResultsOnly = true)
    {
        if (!Enum.IsDefined(rule)) throw new ArgumentOutOfRangeException(nameof(rule));
        IReadOnlyList<ResultGroupViewModel> scope = currentFilteredResultsOnly ? VisibleGroups.ToArray() : allGroups;
        var proposed = new HashSet<PhysicalFileIdentity>();
        var affected = new HashSet<ResultGroupViewModel>();

        foreach (ResultGroupViewModel group in scope)
        {
            IReadOnlyList<ResultMemberViewModel> members = group.Members;
            if (members.Count < 2) continue;
            ResultMemberViewModel? keeper = SelectKeeper(rule, members, preferredLocation);
            if (keeper is null) continue;
            affected.Add(group);
            foreach (ResultMemberViewModel member in members)
            {
                if (!ReferenceEquals(member, keeper)) proposed.Add(member.File.PhysicalIdentity);
            }
        }

        long bytes = allGroups.SelectMany(group => group.Members)
            .Where(member => proposed.Contains(member.File.PhysicalIdentity))
            .Sum(member => member.File.Length);
        return new SelectionAssistantProposal(rule, currentFilteredResultsOnly, preferredLocation, proposed,
            affected.Select(group => group.Index).ToHashSet(), affected.Count, bytes);
    }

    public bool ApplySelectionAssistantProposal(SelectionAssistantProposal? proposal)
    {
        if (proposal is null || !Enum.IsDefined(proposal.Rule)) return false;
        HashSet<PhysicalFileIdentity> proposed = proposal.SelectedPhysicalMembers.ToHashSet();
        IReadOnlyList<ResultGroupViewModel> scope = proposal.CurrentFilteredResultsOnly ? VisibleGroups.ToArray() : allGroups;
        var previous = new Dictionary<PhysicalFileIdentity, bool>();

        foreach (ResultGroupViewModel group in scope.Where(group => proposal.AffectedGroupIndexes.Contains(group.Index)))
        {
            IReadOnlyList<ResultMemberViewModel> members = group.Members;
            if (members.Count < 2) continue;
            HashSet<PhysicalFileIdentity> groupProposed = members
                .Where(member => proposed.Contains(member.File.PhysicalIdentity))
                .Select(member => member.File.PhysicalIdentity)
                .ToHashSet();
            // Fail closed: presentation may never select every independent member.
            if (groupProposed.Count >= members.Count) return false;
            foreach (ResultMemberViewModel member in members)
            {
                previous[member.File.PhysicalIdentity] = member.IsSelected;
                member.SetSelection(groupProposed.Contains(member.File.PhysicalIdentity));
            }
        }

        lastAssistantUndo = new SelectionAssistantUndoState(previous);
        NotifySelectionStateChanged();
        return true;
    }

    public bool UndoLastSelectionAssistant()
    {
        if (lastAssistantUndo is null) return false;
        foreach (ResultMemberViewModel member in allGroups.SelectMany(group => group.Members))
        {
            if (lastAssistantUndo.Selections.TryGetValue(member.File.PhysicalIdentity, out bool selected)) member.SetSelection(selected);
        }

        lastAssistantUndo = null;
        NotifySelectionStateChanged();
        return true;
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
            SelectionRejected?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (member.SetSelection(selected))
        {
            NotifySelectionStateChanged();
        }

        return true;
    }

    private void RefreshVisibleGroups()
    {
        Func<ResultMemberViewModel, bool> matchesMember = MatchesMember;
        foreach (ResultGroupViewModel group in allGroups) group.RefreshVisibleMembers(matchesMember);
        IEnumerable<ResultGroupViewModel> query = allGroups.Where(group => group.VisibleMembers.Count > 0);

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
            (ResultSortOption.ModifiedDate, true) => query.OrderByDescending(group => group.Representative.File.LastWriteTimeUtc),
            (ResultSortOption.ModifiedDate, false) => query.OrderBy(group => group.Representative.File.LastWriteTimeUtc),
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
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(CanUndoSelectionAssistant));
    }

    private bool MatchesMember(ResultMemberViewModel member)
    {
        if (!string.IsNullOrWhiteSpace(searchText)
            && !member.File.FileName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            && !member.File.Extension.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            && !member.File.NormalizedPath.Contains(searchText, StringComparison.OrdinalIgnoreCase)) return false;
        if (fileTypeFilter != ResultFileTypeFilter.All && ResultFileTypeMapper.ToFilter(member.File.Extension) != fileTypeFilter) return false;
        if (!MatchesSize(member.File.Length)) return false;
        return string.Equals(locationFilter, ResultLocationFilter.AllLocationsId, StringComparison.OrdinalIgnoreCase)
            || member.LocationId.Equals(locationFilter, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSize(long length) => sizeFilter switch
    {
        ResultSizeFilter.Any => true,
        ResultSizeFilter.LessThan1Mb => length < 1L * 1024 * 1024,
        ResultSizeFilter.OneTo10Mb => length >= 1L * 1024 * 1024 && length < 10L * 1024 * 1024,
        ResultSizeFilter.TenTo100Mb => length >= 10L * 1024 * 1024 && length < 100L * 1024 * 1024,
        ResultSizeFilter.OneHundredMbTo1Gb => length >= 100L * 1024 * 1024 && length <= 1L * 1024 * 1024 * 1024,
        ResultSizeFilter.MoreThan1Gb => length > 1L * 1024 * 1024 * 1024,
        ResultSizeFilter.Custom => length >= customMinimumSizeBytes && (customMaximumSizeBytes is null || length <= customMaximumSizeBytes),
        _ => false,
    };

    private static ResultMemberViewModel? SelectKeeper(SelectionAssistantRule rule, IReadOnlyList<ResultMemberViewModel> members, string? preferredLocation)
    {
        IEnumerable<ResultMemberViewModel> ordered = members
            .OrderBy(member => member.File.LastWriteTimeUtc)
            .ThenBy(member => member.File.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.File.PhysicalIdentity.FileIdLow);
        return rule switch
        {
            SelectionAssistantRule.KeepNewest => ordered.Last(),
            SelectionAssistantRule.KeepOldest => ordered.First(),
            SelectionAssistantRule.PreferLocation => string.IsNullOrWhiteSpace(preferredLocation)
                ? null
                : ordered.FirstOrDefault(member => member.LocationId.Equals(preferredLocation, StringComparison.OrdinalIgnoreCase)),
            SelectionAssistantRule.SelectOutsideLocation => string.IsNullOrWhiteSpace(preferredLocation)
                ? null
                : ordered.FirstOrDefault(member => member.LocationId.Equals(preferredLocation, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(SelectedCandidateCount));
        OnPropertyChanged(nameof(SelectedCandidateBytes));
        OnPropertyChanged(nameof(CanUndoSelectionAssistant));
        RefreshVisibleGroups();
    }

    private static List<ResultLocationFilter> BuildLocations(CompletedScanResult result, IReadOnlyList<ResultGroupViewModel> groups)
    {
        IEnumerable<string> roots = result.ScanRoots is { Count: > 0 }
            ? result.ScanRoots
            : groups.SelectMany(group => group.Members).Select(member => Path.GetPathRoot(member.File.NormalizedPath) ?? member.File.NormalizedPath);
        return roots.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new ResultLocationFilter(path, path))
            .Prepend(new ResultLocationFilter(ResultLocationFilter.AllLocationsId, "All locations"))
            .DistinctBy(location => location.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(location => location.IsAll ? 0 : 1)
            .ThenBy(location => location.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal string ResolveLocationId(string path)
    {
        ResultLocationFilter? best = Locations
            .Where(location => !location.IsAll && IsSameOrDescendant(path, location.Id))
            .OrderByDescending(location => location.Id.Length)
            .FirstOrDefault();
        return best?.Id ?? (Path.GetPathRoot(path) ?? path);
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum ResultSortOption { ReclaimableBytes, FileSize, CopyCount, Name, Path, ModifiedDate }
public enum ResultFilterOption { AllGroups, SelectedGroups }
public enum ResultFileTypeFilter { All, Photos, Videos, Audio, Documents, Archives, Other }
public enum ResultSizeFilter { Any, LessThan1Mb, OneTo10Mb, TenTo100Mb, OneHundredMbTo1Gb, MoreThan1Gb, Custom }
public enum SelectionAssistantRule { KeepNewest, KeepOldest, PreferLocation, SelectOutsideLocation }

public sealed record ResultLocationFilter(string Id, string DisplayName)
{
    public const string AllLocationsId = "__all_locations__";
    public bool IsAll => string.Equals(Id, AllLocationsId, StringComparison.Ordinal);
}

public sealed record SelectionAssistantProposal(
    SelectionAssistantRule Rule,
    bool CurrentFilteredResultsOnly,
    string? PreferredLocation,
    IReadOnlySet<PhysicalFileIdentity> SelectedPhysicalMembers,
    IReadOnlySet<int> AffectedGroupIndexes,
    int AffectedGroupCount,
    long SelectedBytes)
{
    public int SelectedCount => SelectedPhysicalMembers.Count;
}

internal sealed record SelectionAssistantUndoState(IReadOnlyDictionary<PhysicalFileIdentity, bool> Selections);

public static class ResultFileTypeMapper
{
    public static ResultFileTypeFilter ToFilter(string? extension) => ScanCriteria.Classify(extension) switch
    {
        ScanFileType.Images => ResultFileTypeFilter.Photos,
        ScanFileType.Video => ResultFileTypeFilter.Videos,
        ScanFileType.Audio => ResultFileTypeFilter.Audio,
        ScanFileType.Documents => ResultFileTypeFilter.Documents,
        ScanFileType.Archives => ResultFileTypeFilter.Archives,
        _ => ResultFileTypeFilter.Other,
    };

    public static string Glyph(ResultFileTypeFilter type) => type switch
    {
        ResultFileTypeFilter.Photos => "\uE91B",
        ResultFileTypeFilter.Videos => "\uE714",
        ResultFileTypeFilter.Audio => "\uE8D6",
        ResultFileTypeFilter.Documents => "\uE8A5",
        ResultFileTypeFilter.Archives => "\uE7B8",
        _ => "\uE8B7",
    };
}

public sealed class ResultGroupViewModel : INotifyPropertyChanged
{
    private bool isExpanded;

    internal ResultGroupViewModel(ResultsReviewViewModel owner, DuplicateFileGroup group, int index)
    {
        Owner = owner;
        SnapshotGroup = group;
        Index = index;
        Members = group.Files.Select(file => new ResultMemberViewModel(this, file)).ToList().AsReadOnly();
        VisibleMembers = new ObservableCollection<ResultMemberViewModel>(Members);
        Representative = Members[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal ResultsReviewViewModel Owner { get; }
    public DuplicateFileGroup SnapshotGroup { get; }
    public int Index { get; }
    public IReadOnlyList<ResultMemberViewModel> Members { get; }
    public ObservableCollection<ResultMemberViewModel> VisibleMembers { get; }
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

    internal void RefreshVisibleMembers(Func<ResultMemberViewModel, bool> predicate)
    {
        ResultMemberViewModel[] replacement = Members.Where(predicate).ToArray();
        if (VisibleMembers.Count == replacement.Length && VisibleMembers.SequenceEqual(replacement)) return;
        VisibleMembers.Clear();
        foreach (ResultMemberViewModel member in replacement) VisibleMembers.Add(member);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleMembers)));
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
    public ResultGroupViewModel Group { get; }
    public DiscoveredFile File { get; }
    /// <summary>Native checkbox semantics announce the checked state; the filename distinguishes the member.</summary>
    public string AccessibleName => File.FileName;
    /// <summary>The exact untrimmed path remains available without putting it in every visible row.</summary>
    public string AccessiblePath => File.NormalizedPath;
    public string ContainingFolder => Path.GetDirectoryName(File.NormalizedPath) ?? string.Empty;
    public string LocationId => Group.Owner.ResolveLocationId(File.NormalizedPath);
    public ScanFileType FileType => ScanCriteria.Classify(File.Extension);
    public ResultFileTypeFilter FileTypeFilter => ResultFileTypeMapper.ToFilter(File.Extension);

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

public enum ResultReportScope { AllResults, CurrentFilteredResults }

/// <summary>Pure report formatter. It writes no files and never persists result state.</summary>
#pragma warning disable CA1305 // Reports deliberately use invariant fields plus localized display text.
public static class ResultReportExporter
{
    public static string CreateCsv(ResultsReviewViewModel results, ResultReportScope scope)
    {
        ArgumentNullException.ThrowIfNull(results);
        var builder = new StringBuilder();
        WriteSummaryCsv(builder, results, scope);
        builder.AppendLine("Group,Filename,Full path,Size bytes,Formatted size,Modified,Extension,Type,Selected candidate");
        foreach ((int groupNumber, ResultMemberViewModel member) in EnumerateMembers(results, scope))
        {
            string[] values = [
                groupNumber.ToString(CultureInfo.InvariantCulture), member.File.FileName, member.File.NormalizedPath,
                member.File.Length.ToString(CultureInfo.InvariantCulture), ResultDisplayFormatter.FormatBytes(member.File.Length, CultureInfo.InvariantCulture),
                member.File.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture), member.File.Extension,
                member.FileTypeFilter.ToString(), member.IsSelected ? "Yes" : "No"];
            builder.AppendLine(string.Join(',', values.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    public static string CreateText(ResultsReviewViewModel results, ResultReportScope scope)
    {
        ArgumentNullException.ThrowIfNull(results);
        var builder = new StringBuilder();
        builder.AppendLine("Duplicate File Cleaner Pro — Results report");
        builder.AppendLine($"Scope: {ScopeLabel(scope)}");
        builder.AppendLine($"Duplicate groups: {ScopedGroups(results, scope).Count}");
        builder.AppendLine($"Verified duplicate members: {EnumerateMembers(results, scope).Count()}");
        builder.AppendLine($"Reclaimable space: {ResultDisplayFormatter.FormatBytes(ScopedGroups(results, scope).Sum(group => group.ReclaimableBytes))}");
        builder.AppendLine($"Selected candidates: {results.SelectedCandidateCount} ({ResultDisplayFormatter.FormatBytes(results.SelectedCandidateBytes)})");
        builder.AppendLine();
        foreach ((int groupNumber, ResultMemberViewModel member) in EnumerateMembers(results, scope))
        {
            builder.AppendLine($"Group {groupNumber}: {member.File.FileName}");
            builder.AppendLine($"  Path: {member.File.NormalizedPath}");
            builder.AppendLine($"  Size: {member.File.Length.ToString(CultureInfo.InvariantCulture)} bytes ({ResultDisplayFormatter.FormatBytes(member.File.Length)})");
            builder.AppendLine($"  Modified: {member.File.LastWriteTimeUtc:O}");
            builder.AppendLine($"  Type: {member.FileTypeFilter}; selected candidate: {(member.IsSelected ? "Yes" : "No")}");
        }

        return builder.ToString();
    }

    private static void WriteSummaryCsv(StringBuilder builder, ResultsReviewViewModel results, ResultReportScope scope)
    {
        IReadOnlyList<ResultGroupViewModel> groups = ScopedGroups(results, scope);
        builder.AppendLine("Duplicate File Cleaner Pro Results Report");
        builder.AppendLine($"Scope,{EscapeCsv(ScopeLabel(scope))}");
        builder.AppendLine($"Duplicate groups,{groups.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Duplicate members,{EnumerateMembers(results, scope).Count().ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Reclaimable bytes,{groups.Sum(group => group.ReclaimableBytes).ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Selected candidates,{results.SelectedCandidateCount.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Selected candidate bytes,{results.SelectedCandidateBytes.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine();
    }

    private static IReadOnlyList<ResultGroupViewModel> ScopedGroups(ResultsReviewViewModel results, ResultReportScope scope) =>
        scope == ResultReportScope.CurrentFilteredResults ? results.VisibleGroups.ToArray() : results.AllGroups;

    private static IEnumerable<(int GroupNumber, ResultMemberViewModel Member)> EnumerateMembers(ResultsReviewViewModel results, ResultReportScope scope) =>
        ScopedGroups(results, scope).SelectMany(group =>
            (scope == ResultReportScope.CurrentFilteredResults ? group.VisibleMembers : group.Members)
            .Select(member => (group.Index + 1, member)));

    private static string EscapeCsv(string value)
    {
        value ??= string.Empty;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string ScopeLabel(ResultReportScope scope) => scope == ResultReportScope.CurrentFilteredResults ? "Current filtered Results" : "All Results";
}
#pragma warning restore CA1305
