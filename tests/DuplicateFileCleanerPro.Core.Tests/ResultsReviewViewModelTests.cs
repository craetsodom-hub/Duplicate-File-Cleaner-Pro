using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class ResultsReviewViewModelTests
{
    [TestMethod]
    public void MappingPreservesVerifiedCountsReclaimableBytesAndPhysicalMembers()
    {
        CompletedScanResult snapshot = Result([Group(10, ("same.bin", 10, 1), ("renamed.bin", 10, 2))]);
        var viewModel = new ResultsReviewViewModel(snapshot);

        Assert.AreEqual(1, viewModel.DuplicateGroupCount);
        Assert.AreEqual(2, viewModel.VerifiedMemberCount);
        Assert.AreEqual(10, viewModel.ReclaimableBytes);
        Assert.HasCount(2, viewModel.AllGroups[0].Members);
        Assert.AreSame(snapshot.Detection.Groups[0].Files[0], viewModel.AllGroups[0].Members[0].File);
    }

    [TestMethod]
    public void SearchMatchesFilenameAndPathCaseInsensitivelyAndClearingRestoresGroups()
    {
        var viewModel = new ResultsReviewViewModel(Result([
            Group(10, ("Résumé.txt", 10, 1), ("copy.bin", 10, 2)),
            Group(20, ("other.bin", 20, 3), ("different.bin", 20, 4), ("path-target.bin", 20, 5))]));

        viewModel.SearchText = "rÉSuMÉ";
        Assert.HasCount(1, viewModel.VisibleGroups);
        viewModel.SearchText = "folder-3";
        Assert.HasCount(1, viewModel.VisibleGroups);
        viewModel.SearchText = string.Empty;
        Assert.HasCount(2, viewModel.VisibleGroups);
    }

    [TestMethod]
    public void SortingIsDeterministicForEverySupportedMode()
    {
        var viewModel = new ResultsReviewViewModel(Result([
            Group(20, ("zeta.bin", 20, 1), ("zeta-copy.bin", 20, 2)),
            Group(10, ("alpha.bin", 10, 3), ("alpha-copy.bin", 10, 4), ("third.bin", 10, 5)),
            Group(20, ("beta.bin", 20, 6), ("beta-copy.bin", 20, 7))]));

        foreach (ResultSortOption option in Enum.GetValues<ResultSortOption>())
        {
            viewModel.SortOption = option;
            viewModel.SortDescending = false;
            string[] first = viewModel.VisibleGroups.Select(group => group.DisplayName).ToArray();
            string[] second = viewModel.VisibleGroups.Select(group => group.DisplayName).ToArray();
            CollectionAssert.AreEqual(first, second, option.ToString());
            viewModel.SortDescending = true;
            Assert.HasCount(3, viewModel.VisibleGroups);
        }
    }

    [TestMethod]
    public void SelectionCannotSelectEveryPhysicalMemberAndKeepsGroupsIndependent()
    {
        var viewModel = new ResultsReviewViewModel(Result([
            Group(20, ("a.bin", 10, 1), ("b.bin", 10, 2), ("c.bin", 10, 3)),
            Group(20, ("d.bin", 10, 4), ("e.bin", 10, 5))]));
        ResultGroupViewModel first = viewModel.AllGroups[0];
        first.Members[0].IsSelected = true;
        first.Members[1].IsSelected = true;
        first.Members[2].IsSelected = true;
        viewModel.AllGroups[1].Members[0].IsSelected = true;

        Assert.AreEqual(2, first.SelectedCandidateCount);
        Assert.IsFalse(first.Members[2].IsSelected);
        Assert.AreEqual(1, viewModel.AllGroups[1].SelectedCandidateCount);
        Assert.AreEqual(3, viewModel.SelectedCandidateCount);
        Assert.AreEqual(30, viewModel.SelectedCandidateBytes);
    }

    [TestMethod]
    public void SelectionSurvivesSearchSortingFilteringAndExpansionWithoutMutatingSnapshot()
    {
        CompletedScanResult snapshot = Result([
            Group(20, ("needle.bin", 10, 1), ("other.bin", 10, 2), ("third.bin", 10, 3)),
            Group(20, ("z.bin", 10, 4), ("y.bin", 10, 5))]);
        var viewModel = new ResultsReviewViewModel(snapshot);
        viewModel.AllGroups[0].Members[1].IsSelected = true;
        viewModel.AllGroups[0].IsExpanded = true;
        viewModel.SearchText = "needle";
        viewModel.SortOption = ResultSortOption.Name;
        viewModel.FilterOption = ResultFilterOption.SelectedGroups;

        Assert.HasCount(1, viewModel.VisibleGroups);
        Assert.IsTrue(viewModel.AllGroups[0].Members[1].IsSelected);
        Assert.IsTrue(viewModel.AllGroups[0].IsExpanded);
        Assert.HasCount(3, snapshot.Detection.Groups[0].Files);
    }

    [TestMethod]
    public void HandoffIsImmutablePhysicalIdentityIntentAndNewResultStartsClean()
    {
        var first = new ResultsReviewViewModel(Result([Group(10, ("a.bin", 10, 1), ("b.bin", 10, 2))]));
        first.AllGroups[0].Members[1].IsSelected = true;
        ReviewSelectionHandoff handoff = first.CreateSelectionHandoff();
        var replacement = new ResultsReviewViewModel(Result([Group(20, ("c.bin", 20, 3), ("d.bin", 20, 4))]));

        Assert.HasCount(1, handoff.SelectedPhysicalMembers);
        Assert.AreEqual((ulong)2, handoff.SelectedPhysicalMembers[0].FileIdLow);
        Assert.AreEqual(0, replacement.SelectedCandidateCount);
    }

    [TestMethod]
    public void LargePresentationStateStaysCorrectAcrossThirtyThousandMembers()
    {
        const int groupCount = 5000;
        var groups = new List<DuplicateFileGroup>(groupCount);
        ulong identity = 1;
        for (int group = 0; group < groupCount; group++)
        {
            var files = new List<DiscoveredFile>(10);
            for (int member = 0; member < 10; member++) files.Add(File($"deep/{group:D4}/member-{member:D2}.bin", group + 1, identity++));
            groups.Add(new DuplicateFileGroup(files.AsReadOnly(), 9L * (group + 1)));
        }

        var viewModel = new ResultsReviewViewModel(Result(groups));
        viewModel.SearchText = "member-05";
        viewModel.SortOption = ResultSortOption.CopyCount;
        viewModel.FilterOption = ResultFilterOption.AllGroups;
        foreach (ResultGroupViewModel group in viewModel.AllGroups.Take(100)) group.Members[0].IsSelected = true;

        Assert.HasCount(groupCount, viewModel.VisibleGroups);
        Assert.AreEqual(100, viewModel.SelectedCandidateCount);
        Assert.AreEqual(groupCount * 10, viewModel.VerifiedMemberCount);
    }

    [TestMethod]
    public void FileTypeSizeLocationAndSearchFiltersComposeWithoutChangingSelections()
    {
        var viewModel = new ResultsReviewViewModel(Result([
            Group(9, ("photo.jpg", 512 * 1024, 1), ("photo-copy.jpg", 512 * 1024, 2)),
            Group(30, ("report.pdf", 2 * 1024 * 1024, 3), ("report-copy.pdf", 2 * 1024 * 1024, 4))], ["C:/"]));
        viewModel.AllGroups[1].Members[1].IsSelected = true;

        viewModel.FileTypeFilter = ResultFileTypeFilter.Photos;
        viewModel.SizeFilter = ResultSizeFilter.LessThan1Mb;
        viewModel.LocationFilter = "C:/";
        viewModel.SearchText = ".jpg";

        Assert.HasCount(1, viewModel.VisibleGroups);
        Assert.AreEqual("photo.jpg", viewModel.VisibleGroups[0].DisplayName);
        Assert.AreEqual(4, viewModel.ActiveFilterCount);
        Assert.IsTrue(viewModel.AllGroups[1].Members[1].IsSelected);

        viewModel.ClearFilters();
        Assert.HasCount(2, viewModel.VisibleGroups);
        Assert.IsFalse(viewModel.HasActiveFilters);
    }

    [TestMethod]
    public void SelectionAssistantIsDeterministicScopedAndUndoable()
    {
        var viewModel = new ResultsReviewViewModel(Result([
            Group(20, ("same.bin", 10, 1), ("tie.bin", 10, 2), ("new.bin", 10, 3)),
            Group(10, ("outside.bin", 10, 4), ("other.bin", 10, 5))]));
        viewModel.SearchText = "same.bin";
        SelectionAssistantProposal proposal = viewModel.CreateSelectionAssistantProposal(SelectionAssistantRule.KeepOldest);

        Assert.AreEqual(1, proposal.AffectedGroupCount);
        Assert.IsTrue(viewModel.ApplySelectionAssistantProposal(proposal));
        Assert.AreEqual(2, viewModel.SelectedCandidateCount);
        Assert.IsFalse(viewModel.AllGroups[0].Members[0].IsSelected);
        Assert.AreEqual(0, viewModel.AllGroups[1].SelectedCandidateCount, "Filtered-out groups must not be replaced.");
        Assert.IsTrue(viewModel.UndoLastSelectionAssistant());
        Assert.AreEqual(0, viewModel.SelectedCandidateCount);
    }

    [TestMethod]
    public void ReportExportIncludesEscapedFactualRowsAndHonorsCurrentFilterScope()
    {
        var viewModel = new ResultsReviewViewModel(Result([
            Group(10, ("comma,name.txt", 10, 1), ("copy.txt", 10, 2)),
            Group(20, ("other.bin", 20, 3), ("other-copy.bin", 20, 4))]));
        viewModel.SearchText = "comma";
        string csv = ResultReportExporter.CreateCsv(viewModel, ResultReportScope.CurrentFilteredResults);
        string text = ResultReportExporter.CreateText(viewModel, ResultReportScope.CurrentFilteredResults);

        StringAssert.Contains(csv, "\"comma,name.txt\"");
        StringAssert.Contains(csv, "C:/folder-1/comma,name.txt");
        Assert.IsFalse(csv.Contains("other-copy.bin", StringComparison.Ordinal));
        StringAssert.Contains(text, "Scope: Current filtered Results");
        Assert.IsFalse(text.Contains("other-copy.bin", StringComparison.Ordinal));
    }

    private static CompletedScanResult Result(IEnumerable<DuplicateFileGroup> groups, IReadOnlyList<string>? scanRoots = null)
    {
        List<DuplicateFileGroup> list = groups.ToList();
        long reclaimable = list.Sum(group => group.ReclaimableBytes);
        return new CompletedScanResult(new DiscoveryResult([], [], false), new ExactDuplicateDetectionResult(list.AsReadOnly(), [], reclaimable, false), scanRoots);
    }

    private static DuplicateFileGroup Group(long reclaimable, params (string Name, long Length, ulong Identity)[] members) =>
        new(members.Select(member => File($"C:/folder-{member.Identity}/{member.Name}", member.Length, member.Identity)).ToArray(), reclaimable);

    private static DiscoveredFile File(string path, long length, ulong identity) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, identity, 0), FileAttributes.Normal);
}
