using DuplicateFileCleanerPro.App.SimilarPhotos;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Core.SimilarRemoval;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class SimilarPhotoRemovalTests
{
    [TestMethod]
    public void PlannerAcceptsOnlyExplicitHumanIntentAndDoesNotRequireByteEquality()
    {
        CompletedSimilarPhotoScanResult result = Result([Group(SimilarityTier.Similar,
            File("C:\\Photos\\original.jpg", 100, 1),
            File("C:\\Photos\\crop.jpg", 74, 2),
            File("C:\\Photos\\bright.jpg", 121, 3))]);

        SimilarPhotoRemovalPlanningResult planning = SimilarPhotoRemovalPlanner.CreatePlan(new(result, [Identity(2), Identity(3)]));

        Assert.IsTrue(planning.Succeeded);
        Assert.IsNotNull(planning.Plan);
        Assert.AreEqual(2, planning.Plan.RequestedPhotoCount);
        Assert.AreEqual(195, planning.Plan.SelectedBytes);
        Assert.HasCount(1, planning.Plan.Groups[0].Survivors);
    }

    [TestMethod]
    public void PlannerRejectsEmptyDuplicateUnknownAndAllMemberIntent()
    {
        CompletedSimilarPhotoScanResult result = Result([Group(SimilarityTier.VerySimilar,
            File("C:\\Photos\\a.jpg", 10, 1), File("C:\\Photos\\b.jpg", 10, 2))]);

        AssertIssue(result, [], SimilarPhotoRemovalPlanningIssueReason.EmptyIntent);
        AssertIssue(result, [Identity(1), Identity(1)], SimilarPhotoRemovalPlanningIssueReason.DuplicateCandidateIntent);
        AssertIssue(result, [Identity(99)], SimilarPhotoRemovalPlanningIssueReason.UnknownCandidate);
        AssertIssue(result, [Identity(1), Identity(2)], SimilarPhotoRemovalPlanningIssueReason.AllIndependentMembersSelected);
    }

    [TestMethod]
    public void PlannerRejectsHardLinkAliasesAndMalformedGroups()
    {
        DiscoveredFile first = File("C:\\Photos\\a.jpg", 10, 1);
        DiscoveredFile alias = first with { NormalizedPath = "C:\\Photos\\alias.jpg", FileName = "alias.jpg" };
        CompletedSimilarPhotoScanResult aliases = Result([Group(SimilarityTier.Similar, first, alias)]);
        SimilarPhotoRemovalPlanningResult aliasPlanning = SimilarPhotoRemovalPlanner.CreatePlan(new(aliases, [Identity(1)]));
        Assert.IsTrue(aliasPlanning.Issues.Any(issue => issue.Reason == SimilarPhotoRemovalPlanningIssueReason.DuplicatePhysicalIdentity));

        SimilarPhotoGroup malformed = new(first, [first], SimilarityTier.Similar);
        SimilarPhotoRemovalPlanningResult malformedPlanning = SimilarPhotoRemovalPlanner.CreatePlan(new(Result([malformed]), [Identity(1)]));
        Assert.IsTrue(malformedPlanning.Issues.Any(issue => issue.Reason == SimilarPhotoRemovalPlanningIssueReason.InvalidGroup));
    }

    [TestMethod]
    public void PlansAreDeterministicAcrossCandidateOrdering()
    {
        CompletedSimilarPhotoScanResult result = Result([Group(SimilarityTier.Similar,
            File("C:\\Photos\\z.jpg", 10, 1), File("C:\\Photos\\a.jpg", 20, 2), File("C:\\Photos\\m.jpg", 30, 3))]);
        SimilarPhotoRemovalPlan first = SimilarPhotoRemovalPlanner.CreatePlan(new(result, [Identity(3), Identity(2)])).Plan!;
        SimilarPhotoRemovalPlan second = SimilarPhotoRemovalPlanner.CreatePlan(new(result, [Identity(2), Identity(3)])).Plan!;
        CollectionAssert.AreEqual(
            first.Groups[0].Candidates.Select(item => item.ExpectedFile.NormalizedPath).ToArray(),
            second.Groups[0].Candidates.Select(item => item.ExpectedFile.NormalizedPath).ToArray());
    }

    [TestMethod]
    public async Task EngineReportsSuccessPartialFailureMissingAndChangedFactually()
    {
        CompletedSimilarPhotoScanResult result = Result([
            Group(SimilarityTier.Similar, File("C:\\Photos\\keep-a.jpg", 10, 1), File("C:\\Photos\\remove-a.jpg", 20, 2)),
            Group(SimilarityTier.Similar, File("C:\\Photos\\keep-b.jpg", 11, 3), File("C:\\Photos\\remove-b.jpg", 21, 4)),
            Group(SimilarityTier.Similar, File("C:\\Photos\\keep-c.jpg", 12, 5), File("C:\\Photos\\remove-c.jpg", 22, 6)),
            Group(SimilarityTier.Similar, File("C:\\Photos\\keep-d.jpg", 13, 7), File("C:\\Photos\\remove-d.jpg", 23, 8))]);
        SimilarPhotoRemovalPlan plan = SimilarPhotoRemovalPlanner.CreatePlan(new(result, [Identity(2), Identity(4), Identity(6), Identity(8)])).Plan!;
        var platform = new FakePlatform();
        platform.Attempts[Identity(4)] = new(SimilarPhotoRecycleAttemptStatus.CandidateMissing);
        platform.Attempts[Identity(6)] = new(SimilarPhotoRecycleAttemptStatus.CandidateChanged);
        platform.Attempts[Identity(8)] = new(SimilarPhotoRecycleAttemptStatus.RecycleBinFailed, 5);

        SimilarPhotoRemovalResult execution = await new SimilarPhotoRemovalEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(1, execution.RecycledPhotoCount);
        Assert.AreEqual(2, execution.SkippedPhotoCount);
        Assert.AreEqual(1, execution.FailedPhotoCount);
        Assert.AreEqual(20, execution.RecycledBytes);
    }

    [TestMethod]
    public async Task MissingOrChangedSurvivorPreventsCandidateRemoval()
    {
        CompletedSimilarPhotoScanResult result = Result([Group(SimilarityTier.Similar,
            File("C:\\Photos\\keeper.jpg", 10, 1), File("C:\\Photos\\candidate.jpg", 20, 2))]);
        SimilarPhotoRemovalPlan plan = SimilarPhotoRemovalPlanner.CreatePlan(new(result, [Identity(2)])).Plan!;
        var platform = new FakePlatform();
        platform.Validations[Identity(1)] = new(SimilarPhotoRemovalValidationStatus.Changed);

        SimilarPhotoRemovalResult execution = await new SimilarPhotoRemovalEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(0, execution.RecycledPhotoCount);
        Assert.AreEqual(SimilarPhotoRemovalOutcomeStatus.SkippedSurvivorUnavailable, execution.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, platform.RecycleCalls);
    }

    [TestMethod]
    public async Task CancellationStopsBetweenCandidatesAndRecordsActualOutcomes()
    {
        CompletedSimilarPhotoScanResult result = Result([Group(SimilarityTier.Similar,
            File("C:\\Photos\\keeper.jpg", 10, 1), File("C:\\Photos\\one.jpg", 20, 2), File("C:\\Photos\\two.jpg", 30, 3))]);
        SimilarPhotoRemovalPlan plan = SimilarPhotoRemovalPlanner.CreatePlan(new(result, [Identity(2), Identity(3)])).Plan!;
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform { AfterRecycle = cancellation.Cancel };

        SimilarPhotoRemovalResult execution = await new SimilarPhotoRemovalEngine(platform).ExecuteAsync(plan, cancellationToken: cancellation.Token);

        Assert.IsTrue(execution.WasCancelled);
        Assert.AreEqual(1, execution.RecycledPhotoCount);
        Assert.AreEqual(1, execution.SkippedPhotoCount);
    }

    [TestMethod]
    public async Task CancellationReportsUnprocessedGroupsAsCancelled()
    {
        CompletedSimilarPhotoScanResult result = Result([
            Group(SimilarityTier.Similar, File("C:\\Photos\\keep-a.jpg", 10, 1), File("C:\\Photos\\remove-a.jpg", 20, 2)),
            Group(SimilarityTier.Similar, File("C:\\Photos\\keep-b.jpg", 11, 3), File("C:\\Photos\\remove-b.jpg", 21, 4))]);
        SimilarPhotoRemovalPlan plan = SimilarPhotoRemovalPlanner.CreatePlan(new(result, [Identity(2), Identity(4)])).Plan!;
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform { AfterRecycle = cancellation.Cancel };

        SimilarPhotoRemovalResult execution = await new SimilarPhotoRemovalEngine(platform).ExecuteAsync(plan, cancellationToken: cancellation.Token);

        Assert.IsTrue(execution.WasCancelled);
        Assert.AreEqual(2, execution.RequestedPhotoCount);
        Assert.AreEqual(1, execution.RecycledPhotoCount);
        Assert.AreEqual(1, execution.SkippedPhotoCount);
        Assert.HasCount(2, execution.Groups);
        Assert.AreEqual(SimilarPhotoRemovalOutcomeStatus.Cancelled, execution.Groups[1].Outcomes[0].Status);
    }

    [TestMethod]
    public async Task PresentationCapturesHiddenMarksAndStalesSourceAfterAttempt()
    {
        var review = new SimilarPhotosReviewViewModel(Result([
            Group(SimilarityTier.VerySimilar, File("C:\\Photos\\beach-a.jpg", 10, 1), File("C:\\Photos\\beach-b.jpg", 20, 2)),
            Group(SimilarityTier.Similar, File("C:\\Photos\\city-a.jpg", 30, 3), File("C:\\Photos\\city-b.jpg", 40, 4))]));
        review.AllGroups[0].Photos[1].SetMark(SimilarPhotoReviewMark.ConsiderRemoving);
        review.AllGroups[1].Photos[1].SetMark(SimilarPhotoReviewMark.ConsiderRemoving);
        review.TierFilter = SimilarityTier.VerySimilar;
        Assert.AreEqual(2, review.MarkedForRemovalCount);

        var workflow = new SimilarPhotoRemovalWorkflowViewModel(new(new FakePlatform()));
        Assert.IsTrue(workflow.BeginReview(review));
        Assert.AreEqual(2, workflow.SelectedPhotoCount);
        Assert.AreEqual(2, workflow.AffectedGroupCount);
        await workflow.ExecuteConfirmedAsync();

        Assert.IsTrue(review.IsStale);
        Assert.IsFalse(review.CanReviewRemoval);
        Assert.AreEqual(0, review.MarkedForRemovalCount);
        Assert.IsFalse(review.AllGroups[0].Photos[0].SetMark(SimilarPhotoReviewMark.Keep));
    }

    [TestMethod]
    [TestCategory("Stress")]
    public void PlannerScalesLinearlyAcrossFiveThousandPhotos()
    {
        var groups = new List<SimilarPhotoGroup>(1000);
        var selected = new List<PhysicalFileIdentity>(500);
        ulong identity = 1;
        for (int groupIndex = 0; groupIndex < 1000; groupIndex++)
        {
            DiscoveredFile[] files = Enumerable.Range(0, 5)
                .Select(member => File($"C:\\Photos\\g-{groupIndex:D4}-{member}.jpg", 100 + member, identity++))
                .ToArray();
            groups.Add(Group(SimilarityTier.Similar, files));
            if ((groupIndex & 1) == 0) selected.Add(files[4].PhysicalIdentity);
        }

        SimilarPhotoRemovalPlanningResult planning = SimilarPhotoRemovalPlanner.CreatePlan(new(Result(groups), selected));

        Assert.IsTrue(planning.Succeeded);
        Assert.AreEqual(500, planning.Plan!.RequestedPhotoCount);
        Assert.HasCount(500, planning.Plan.Groups);
    }

    private static void AssertIssue(CompletedSimilarPhotoScanResult result, IEnumerable<PhysicalFileIdentity> selected, SimilarPhotoRemovalPlanningIssueReason reason)
    {
        SimilarPhotoRemovalPlanningResult planning = SimilarPhotoRemovalPlanner.CreatePlan(new(result, selected));
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == reason), string.Join(", ", planning.Issues.Select(issue => issue.Reason)));
    }

    private static CompletedSimilarPhotoScanResult Result(IEnumerable<SimilarPhotoGroup> groups)
    {
        SimilarPhotoGroup[] snapshot = groups.ToArray();
        DiscoveredFile[] files = snapshot.SelectMany(group => group.Photos).DistinctBy(file => file.NormalizedPath).ToArray();
        return new(new DiscoveryResult(files, [], false), new(snapshot, [], [], files.Length, 0, 0, false), SimilarPhotoSensitivity.Balanced, ["C:\\Photos"]);
    }

    private static SimilarPhotoGroup Group(SimilarityTier tier, params DiscoveredFile[] files) => new(files[0], files, tier);
    private static PhysicalFileIdentity Identity(ulong value) => new(7, value, 0);
    private static DiscoveredFile File(string path, long length, ulong identity) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch.AddMinutes(identity), DateTimeOffset.UnixEpoch.AddMinutes(identity), Identity(identity), FileAttributes.Normal);

    private sealed class FakePlatform : ISimilarPhotoRemovalPlatform
    {
        public Dictionary<PhysicalFileIdentity, SimilarPhotoRemovalValidation> Validations { get; } = [];
        public Dictionary<PhysicalFileIdentity, SimilarPhotoRecycleAttempt> Attempts { get; } = [];
        public Action? AfterRecycle { get; init; }
        public int RecycleCalls { get; private set; }
        public Task<SimilarPhotoRemovalValidation> ValidateAsync(SimilarPhotoRemovalPlanMember member, CancellationToken cancellationToken = default) =>
            Task.FromResult(Validations.GetValueOrDefault(member.ExpectedFile.PhysicalIdentity, SimilarPhotoRemovalValidation.Valid()));
        public Task<SimilarPhotoRecycleAttempt> RevalidateAndRecycleAsync(SimilarPhotoRemovalPlanMember candidate, IReadOnlyList<SimilarPhotoRemovalPlanMember> survivors, CancellationToken cancellationToken = default)
        {
            RecycleCalls++;
            SimilarPhotoRecycleAttempt attempt = Attempts.GetValueOrDefault(candidate.ExpectedFile.PhysicalIdentity, new(SimilarPhotoRecycleAttemptStatus.Recycled));
            AfterRecycle?.Invoke();
            return Task.FromResult(attempt);
        }
    }
}
