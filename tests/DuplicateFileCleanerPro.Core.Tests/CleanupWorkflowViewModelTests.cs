using DuplicateFileCleanerPro.App.Cleanup;
using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class CleanupWorkflowViewModelTests
{
    [TestMethod]
    public void ReviewRequiresExplicitCandidatesAndCalculatesFactualScope()
    {
        var results = Results(
            CleanupPlannerTests.Group(CleanupPlannerTests.File("a.bin", 8, 1), CleanupPlannerTests.File("b.bin", 8, 2), CleanupPlannerTests.File("c.bin", 8, 3)),
            CleanupPlannerTests.Group(CleanupPlannerTests.File("d.bin", 11, 4), CleanupPlannerTests.File("e.bin", 11, 5)));
        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(new FakePlatform()));

        Assert.IsFalse(workflow.BeginReview(results));
        results.AllGroups[0].Members[1].IsSelected = true;
        results.AllGroups[1].Members[1].IsSelected = true;

        Assert.IsTrue(workflow.BeginReview(results));
        Assert.AreEqual(CleanupWorkflowState.Reviewing, workflow.State);
        Assert.AreEqual(2, workflow.SelectedCandidateCount);
        Assert.AreEqual(19L, workflow.SelectedCandidateBytes);
        Assert.AreEqual(2, workflow.AffectedGroupCount);
    }

    [TestMethod]
    public async Task ConfirmationUsesCapturedImmutableSelectionAndReportsSuccessProgress()
    {
        var results = Results(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper.bin", 12, 1), CleanupPlannerTests.File("candidate.bin", 12, 2)));
        results.AllGroups[0].Members[1].IsSelected = true;
        var platform = new FakePlatform();
        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(platform));
        var progress = new CapturingProgress();

        Assert.IsTrue(workflow.BeginReview(results));
        results.AllGroups[0].Members[1].IsSelected = false;
        CleanupResult? result = await workflow.ExecuteConfirmedAsync(progress);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.IsTrue(workflow.RequiresRescan);
        Assert.AreEqual(CleanupWorkflowState.Completed, workflow.State);
        Assert.AreEqual(1, progress.Values[^1].RecycledCount);
        Assert.AreEqual(12L, progress.Values[^1].ActuallyReclaimedBytes);
        Assert.AreEqual(1, platform.RecycleCalls);
    }

    [TestMethod]
    public async Task PartialSuccessAndOutcomeMappingRemainFactual()
    {
        var results = Results(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper.bin", 7, 1), CleanupPlannerTests.File("candidate-1.bin", 7, 2), CleanupPlannerTests.File("candidate-2.bin", 7, 3)));
        results.AllGroups[0].Members[1].IsSelected = true;
        results.AllGroups[0].Members[2].IsSelected = true;
        var platform = new FakePlatform();
        platform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled));
        platform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.CandidateChanged));
        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(platform));

        Assert.IsTrue(workflow.BeginReview(results));
        CleanupResult? result = await workflow.ExecuteConfirmedAsync();

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(1, result.SkippedFileCount);
        Assert.AreEqual(7L, result.ActuallyReclaimedBytes);
        Assert.AreEqual("CleanupOutcomeChanged", CleanupOutcomePresentationMapper.Map(result.Groups[0].Outcomes[1].Status).MessageKey);
        Assert.AreEqual(CleanupOutcomeTone.Skipped, CleanupOutcomePresentationMapper.Map(result.Groups[0].Outcomes[1].Status).Tone);
    }

    [TestMethod]
    public async Task CancellationAfterFirstSuccessPreservesActualResultAndRequiresRescan()
    {
        var results = Results(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper.bin", 4, 1), CleanupPlannerTests.File("candidate-1.bin", 4, 2), CleanupPlannerTests.File("candidate-2.bin", 4, 3)));
        results.AllGroups[0].Members[1].IsSelected = true;
        results.AllGroups[0].Members[2].IsSelected = true;
        var platform = new FakePlatform();
        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(platform));
        platform.AfterRecycle = workflow.Cancel;

        Assert.IsTrue(workflow.BeginReview(results));
        CleanupResult? result = await workflow.ExecuteConfirmedAsync();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.WasCancelled);
        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(CleanupWorkflowState.Cancelled, workflow.State);
        Assert.IsTrue(workflow.RequiresRescan);
    }

    [TestMethod]
    public async Task CompletedAttemptCannotReuseTheOldSnapshotForAnotherCleanup()
    {
        var results = Results(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper.bin", 4, 1), CleanupPlannerTests.File("candidate.bin", 4, 2)));
        results.AllGroups[0].Members[1].IsSelected = true;
        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(new FakePlatform()));

        Assert.IsTrue(workflow.BeginReview(results));
        await workflow.ExecuteConfirmedAsync();
        workflow.ReturnToResults();

        Assert.IsTrue(workflow.RequiresRescan);
        Assert.IsFalse(workflow.BeginReview(results));
        workflow.ResetForNewScan();
        Assert.IsFalse(workflow.RequiresRescan);
    }

    [TestMethod]
    public async Task AllSkippedAndRecycleBinFailureAreReportedWithoutClaimingReclaimedBytes()
    {
        var skippedResults = Results(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper-a.bin", 9, 1), CleanupPlannerTests.File("candidate-a.bin", 9, 2)));
        skippedResults.AllGroups[0].Members[1].IsSelected = true;
        var skippedPlatform = new FakePlatform();
        skippedPlatform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.CandidateChanged));
        var skippedWorkflow = new CleanupWorkflowViewModel(new CleanupEngine(skippedPlatform));
        Assert.IsTrue(skippedWorkflow.BeginReview(skippedResults));

        CleanupResult? skipped = await skippedWorkflow.ExecuteConfirmedAsync();

        Assert.IsNotNull(skipped);
        Assert.AreEqual(0, skipped.RecycledFileCount);
        Assert.AreEqual(1, skipped.SkippedFileCount);
        Assert.AreEqual(0L, skipped.ActuallyReclaimedBytes);

        var failedResults = Results(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper-b.bin", 9, 3), CleanupPlannerTests.File("candidate-b.bin", 9, 4)));
        failedResults.AllGroups[0].Members[1].IsSelected = true;
        var failedPlatform = new FakePlatform();
        failedPlatform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.RecycleBinFailed));
        var failedWorkflow = new CleanupWorkflowViewModel(new CleanupEngine(failedPlatform));
        Assert.IsTrue(failedWorkflow.BeginReview(failedResults));

        CleanupResult? failed = await failedWorkflow.ExecuteConfirmedAsync();

        Assert.IsNotNull(failed);
        Assert.AreEqual(0, failed.RecycledFileCount);
        Assert.AreEqual(1, failed.FailedFileCount);
        Assert.AreEqual(0L, failed.ActuallyReclaimedBytes);
    }

    [TestMethod]
    public async Task SimulatedLargeCleanupKeepsReviewAccountingAndMixedOutcomesFactual()
    {
        const int groupCount = 750;
        DuplicateFileGroup[] groups = Enumerable.Range(0, groupCount)
            .Select(index => CleanupPlannerTests.Group(
                CleanupPlannerTests.File($"keeper-{index:D4}.bin", 3, (ulong)(index * 2 + 1)),
                CleanupPlannerTests.File($"candidate-{index:D4}.bin", 3, (ulong)(index * 2 + 2))))
            .ToArray();
        ResultsReviewViewModel results = Results(groups);
        foreach (ResultGroupViewModel group in results.AllGroups)
        {
            group.Members[1].IsSelected = true;
        }

        var platform = new FakePlatform();
        for (int index = 0; index < groupCount; index++)
        {
            platform.Attempts.Enqueue(new CleanupRecycleAttempt((index % 3) switch
            {
                0 => CleanupRecycleAttemptStatus.Recycled,
                1 => CleanupRecycleAttemptStatus.CandidateChanged,
                _ => CleanupRecycleAttemptStatus.RecycleBinFailed,
            }));
        }

        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(platform));
        var progress = new CapturingProgress();
        Assert.IsTrue(workflow.BeginReview(results));
        Assert.AreEqual(groupCount, workflow.SelectedCandidateCount);
        Assert.AreEqual(groupCount * 3L, workflow.SelectedCandidateBytes);

        CleanupResult? result = await workflow.ExecuteConfirmedAsync(progress);

        Assert.IsNotNull(result);
        Assert.AreEqual(250, result.RecycledFileCount);
        Assert.AreEqual(250, result.SkippedFileCount);
        Assert.AreEqual(250, result.FailedFileCount);
        Assert.AreEqual(750L, result.ActuallyReclaimedBytes);
        Assert.AreEqual(groupCount, progress.Values[^1].CandidatesProcessed);
        Assert.AreEqual(groupCount, platform.RecycleCalls);
    }

    [TestMethod]
    public async Task WindowCloseStyleDisposeRequestsCancellationWithoutRollback()
    {
        var results = Results(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper.bin", 4, 1), CleanupPlannerTests.File("candidate.bin", 4, 2)));
        results.AllGroups[0].Members[1].IsSelected = true;
        var platform = new BlockingPlatform();
        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(platform));
        Assert.IsTrue(workflow.BeginReview(results));

        Task<CleanupResult?> running = workflow.ExecuteConfirmedAsync();
        await platform.Started.Task;
        workflow.Dispose();
        CleanupResult? result = await running;

        Assert.IsNotNull(result);
        Assert.IsTrue(result.WasCancelled);
        Assert.AreEqual(0, result.RecycledFileCount);
        Assert.AreEqual(CleanupWorkflowState.Cancelled, workflow.State);
    }

    [TestMethod]
    [DataRow(CleanupCandidateOutcomeStatus.SkippedMissing, "CleanupOutcomeMissing", CleanupOutcomeTone.Skipped)]
    [DataRow(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, "CleanupOutcomeKeeper", CleanupOutcomeTone.Skipped)]
    [DataRow(CleanupCandidateOutcomeStatus.SkippedPolicy, "CleanupOutcomePolicy", CleanupOutcomeTone.Skipped)]
    [DataRow(CleanupCandidateOutcomeStatus.FailedRecycleBin, "CleanupOutcomeRecycleBinFailed", CleanupOutcomeTone.Failed)]
    [DataRow(CleanupCandidateOutcomeStatus.Cancelled, "CleanupOutcomeCancelled", CleanupOutcomeTone.Cancelled)]
    public void OutcomeMappingPreservesUsefulSafeCategories(CleanupCandidateOutcomeStatus status, string key, CleanupOutcomeTone tone)
    {
        CleanupOutcomePresentation presentation = CleanupOutcomePresentationMapper.Map(status);
        Assert.AreEqual(key, presentation.MessageKey);
        Assert.AreEqual(tone, presentation.Tone);
    }

    private static ResultsReviewViewModel Results(params DuplicateFileGroup[] groups) =>
        new(CleanupPlannerTests.Result(groups));

    private sealed class CapturingProgress : IProgress<CleanupProgress>
    {
        public List<CleanupProgress> Values { get; } = [];
        public void Report(CleanupProgress value) => Values.Add(value);
    }

    private sealed class FakePlatform : ICleanupPlatformService
    {
        public Queue<CleanupRecycleAttempt> Attempts { get; } = [];
        public int RecycleCalls { get; private set; }
        public Action? AfterRecycle { get; set; }

        public Task<CleanupFileValidation> ValidateAsync(CleanupPlanMember member, CancellationToken cancellationToken = default) =>
            Task.FromResult(CleanupFileValidation.Valid());

        public Task<CleanupRecycleAttempt> RevalidateAndRecycleAsync(CleanupPlanMember candidate, CleanupPlanMember keeper, CancellationToken cancellationToken = default)
        {
            RecycleCalls++;
            CleanupRecycleAttempt result = Attempts.Count == 0
                ? new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled)
                : Attempts.Dequeue();
            AfterRecycle?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingPlatform : ICleanupPlatformService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CleanupFileValidation> ValidateAsync(CleanupPlanMember member, CancellationToken cancellationToken = default) =>
            Task.FromResult(CleanupFileValidation.Valid());

        public async Task<CleanupRecycleAttempt> RevalidateAndRecycleAsync(CleanupPlanMember candidate, CleanupPlanMember keeper, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled);
        }
    }
}
