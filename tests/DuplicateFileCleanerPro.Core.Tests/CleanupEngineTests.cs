using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Scanning;
using System.Diagnostics;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class CleanupEngineTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TwoMemberGroupRecyclesOneOnlyAfterKeeperValidation()
    {
        CleanupPlan plan = CleanupPlannerTests.Plan(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("keeper.bin", 10, 1), CleanupPlannerTests.File("candidate.bin", 10, 2)));
        var platform = new FakePlatform();

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(10L, result.ActuallyReclaimedBytes);
        Assert.AreEqual(1, platform.RecycleCalls);
        Assert.Contains(CleanupPlannerTests.Id(1), platform.Validated);
        Assert.AreEqual(1, result.Groups[0].SurvivingVerifiedMembers);
    }

    [TestMethod]
    public async Task ThreeMembersRecycleTwoSequentiallyWhileKeeperRemainsValid()
    {
        CleanupPlan plan = CleanupPlannerTests.Plan(CleanupPlannerTests.Group(
            CleanupPlannerTests.File("a.bin", 5, 1), CleanupPlannerTests.File("b.bin", 5, 2), CleanupPlannerTests.File("c.bin", 5, 3)));
        var platform = new FakePlatform();

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(2, result.RecycledFileCount);
        Assert.AreEqual(10L, result.ActuallyReclaimedBytes);
        Assert.AreEqual(2, platform.RecycleCalls);
        Assert.AreEqual(2, platform.Validated.Count(identity => identity == CleanupPlannerTests.Id(1)));
    }

    [TestMethod]
    public async Task MultipleGroupsAreIsolatedWhenOneRecycleFails()
    {
        CleanupPlan plan = CleanupPlannerTests.Plan(
            CleanupPlannerTests.Group(CleanupPlannerTests.File("a.bin", 2, 1), CleanupPlannerTests.File("b.bin", 2, 2)),
            CleanupPlannerTests.Group(CleanupPlannerTests.File("c.bin", 3, 3), CleanupPlannerTests.File("d.bin", 3, 4)));
        var platform = new FakePlatform();
        platform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.RecycleBinFailed, -1));
        platform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled));

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(1, result.FailedFileCount);
        Assert.AreEqual(3L, result.ActuallyReclaimedBytes);
        Assert.HasCount(2, result.Groups);
    }

    [TestMethod]
    [DataRow(CleanupRecycleAttemptStatus.CandidateMissing, CleanupCandidateOutcomeStatus.SkippedMissing)]
    [DataRow(CleanupRecycleAttemptStatus.CandidateIdentityMismatch, CleanupCandidateOutcomeStatus.SkippedIdentityMismatch)]
    [DataRow(CleanupRecycleAttemptStatus.CandidateChanged, CleanupCandidateOutcomeStatus.SkippedChanged)]
    [DataRow(CleanupRecycleAttemptStatus.CandidatePolicyRejected, CleanupCandidateOutcomeStatus.SkippedPolicy)]
    [DataRow(CleanupRecycleAttemptStatus.CandidateUnavailable, CleanupCandidateOutcomeStatus.SkippedVerificationFailed)]
    [DataRow(CleanupRecycleAttemptStatus.ContentMismatch, CleanupCandidateOutcomeStatus.SkippedVerificationFailed)]
    [DataRow(CleanupRecycleAttemptStatus.VerificationFailed, CleanupCandidateOutcomeStatus.SkippedVerificationFailed)]
    public async Task CandidateSafetyFailuresNeverCountAsRecycled(CleanupRecycleAttemptStatus attemptStatus, CleanupCandidateOutcomeStatus expected)
    {
        CleanupPlan plan = TwoMemberPlan();
        var platform = new FakePlatform();
        platform.Attempts.Enqueue(new CleanupRecycleAttempt(attemptStatus));

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(expected, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, result.RecycledFileCount);
        Assert.AreEqual(0L, result.ActuallyReclaimedBytes);
    }

    [TestMethod]
    public async Task MissingOrChangedOnlyKeeperPreventsCandidateRecycle()
    {
        CleanupPlan plan = TwoMemberPlan();
        var platform = new FakePlatform();
        platform.Validation[CleanupPlannerTests.Id(1)] = CleanupFileValidationStatus.Missing;

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, platform.RecycleCalls);
    }

    [TestMethod]
    public async Task FirstSuccessThenCandidateChangeProducesSafePartialSuccess()
    {
        CleanupPlan plan = ThreeMemberPlan();
        var platform = new FakePlatform();
        platform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled));
        platform.Attempts.Enqueue(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.CandidateChanged));

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(1, result.SkippedFileCount);
        Assert.AreEqual(4L, result.ActuallyReclaimedBytes);
    }

    [TestMethod]
    public async Task KeeperBecomingUnsafeAfterFirstSuccessStopsSecondCandidate()
    {
        CleanupPlan plan = ThreeMemberPlan();
        var platform = new FakePlatform();
        platform.OnRecycle = call =>
        {
            if (call == 1) platform.Validation[CleanupPlannerTests.Id(1)] = CleanupFileValidationStatus.Changed;
        };

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, result.Groups[0].Outcomes[1].Status);
        Assert.AreEqual(1, platform.RecycleCalls);
    }

    [TestMethod]
    public async Task CancellationAfterFirstSuccessReportsExactPartialResult()
    {
        CleanupPlan plan = ThreeMemberPlan();
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform { OnRecycle = _ => cancellation.Cancel() };

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan, cancellationToken: cancellation.Token);

        Assert.IsTrue(result.WasCancelled);
        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(CleanupCandidateOutcomeStatus.Cancelled, result.Groups[0].Outcomes[1].Status);
        Assert.AreEqual(4L, result.ActuallyReclaimedBytes);
    }

    [TestMethod]
    public async Task CancellationBeforeFirstCandidatePerformsNoPlatformOperation()
    {
        CleanupPlan plan = TwoMemberPlan();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var platform = new FakePlatform();

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan, cancellationToken: cancellation.Token);

        Assert.IsTrue(result.WasCancelled);
        Assert.AreEqual(0, platform.RecycleCalls);
        Assert.AreEqual(CleanupCandidateOutcomeStatus.Cancelled, result.Groups[0].Outcomes[0].Status);
    }

    [TestMethod]
    public async Task PlatformExceptionIsIsolatedAndDoesNotAuthorizeFallback()
    {
        CleanupPlan plan = TwoMemberPlan();
        var platform = new FakePlatform { ThrowOnRecycle = true };

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.FailedPlatform, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, result.RecycledFileCount);
    }

    [TestMethod]
    public async Task ThrowingProgressObserverCannotCorruptExecution()
    {
        CleanupResult result = await new CleanupEngine(new FakePlatform()).ExecuteAsync(TwoMemberPlan(), new ThrowingProgress());

        Assert.AreEqual(1, result.RecycledFileCount);
    }

    [TestMethod]
    public async Task ProgressReportsOnlyActuallyCompletedCandidateFacts()
    {
        var progress = new CapturingProgress();

        CleanupResult result = await new CleanupEngine(new FakePlatform()).ExecuteAsync(ThreeMemberPlan(), progress);

        CleanupProgress final = progress.Values[^1];
        Assert.AreEqual(2, final.CandidatesProcessed);
        Assert.AreEqual(2, final.CandidatesTotal);
        Assert.AreEqual(result.RecycledFileCount, final.RecycledCount);
        Assert.AreEqual(result.ActuallyReclaimedBytes, final.ActuallyReclaimedBytes);
    }

    [TestMethod]
    public async Task CancellationDuringPrevalidationStopsBeforeRecycle()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new CancellingPlatform(cancellation, cancelDuringRecycle: false);

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(TwoMemberPlan(), cancellationToken: cancellation.Token);

        Assert.IsTrue(result.WasCancelled);
        Assert.AreEqual(0, platform.RecycleCalls);
        Assert.AreEqual(CleanupCandidateOutcomeStatus.Cancelled, result.Groups[0].Outcomes[0].Status);
    }

    [TestMethod]
    public async Task CancellationAtRecycleBoundaryDoesNotClaimUncompletedRemoval()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new CancellingPlatform(cancellation, cancelDuringRecycle: true);

        CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(TwoMemberPlan(), cancellationToken: cancellation.Token);

        Assert.IsTrue(result.WasCancelled);
        Assert.AreEqual(0, result.RecycledFileCount);
        Assert.AreEqual(0L, result.ActuallyReclaimedBytes);
        Assert.AreEqual(CleanupCandidateOutcomeStatus.Cancelled, result.Groups[0].Outcomes[0].Status);
    }

    [TestMethod]
    public async Task ConcurrentCleanupIsRejectedAndEngineCanRunAgain()
    {
        CleanupPlan plan = TwoMemberPlan();
        var platform = new BlockingPlatform();
        var engine = new CleanupEngine(platform);
        Task<CleanupResult> first = engine.ExecuteAsync(plan);
        await platform.Started.Task;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => engine.ExecuteAsync(plan));
        platform.Release.TrySetResult();
        Assert.AreEqual(1, (await first).RecycledFileCount);
        Assert.AreEqual(1, (await engine.ExecuteAsync(plan)).RecycledFileCount);
    }

    [TestMethod]
    public async Task SharedLifecycleCoordinatorPreventsCleanupDuringScanOwnership()
    {
        var coordinator = new SafetyOperationCoordinator();
        using IDisposable scanLease = coordinator.Acquire(SafetyOperationKind.Scan);
        var engine = new CleanupEngine(new FakePlatform(), coordinator);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => engine.ExecuteAsync(TwoMemberPlan()));

        Assert.AreEqual(SafetyOperationKind.Scan, coordinator.ActiveOperation);
    }

    [TestMethod]
    public async Task MalformedExecutionPlanIsRejectedBeforePlatformAccess()
    {
        DiscoveredFile first = CleanupPlannerTests.File("a.bin", 1, 1);
        DiscoveredFile second = CleanupPlannerTests.File("b.bin", 1, 2);
        var firstMember = new CleanupPlanMember(first);
        var secondMember = new CleanupPlanMember(second);
        var malformedGroup = new CleanupPlanGroup(
            0,
            Array.AsReadOnly(new[] { firstMember, secondMember }),
            Array.AsReadOnly(new[] { firstMember, secondMember }),
            Array.AsReadOnly(new[] { firstMember }));
        var malformed = new CleanupPlan(
            CleanupPlannerTests.Result(CleanupPlannerTests.Group(first, second)),
            Array.AsReadOnly(new[] { malformedGroup }));
        var platform = new FakePlatform();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new CleanupEngine(platform).ExecuteAsync(malformed));

        Assert.AreEqual(0, platform.RecycleCalls);
        Assert.IsEmpty(platform.Validated);
    }

    [TestMethod]
    public async Task MalformedPlanCannotRetargetAnAuthorizedPhysicalIdentityToAnotherPath()
    {
        DiscoveredFile first = CleanupPlannerTests.File("a.bin", 1, 1);
        DiscoveredFile second = CleanupPlannerTests.File("b.bin", 1, 2);
        DiscoveredFile retargeted = second with
        {
            NormalizedPath = @"C:\CleanupTests\attacker\replacement.bin",
            FileName = "replacement.bin",
        };
        var firstMember = new CleanupPlanMember(first);
        var retargetedMember = new CleanupPlanMember(retargeted);
        var malformedGroup = new CleanupPlanGroup(
            0,
            Array.AsReadOnly(new[] { firstMember, retargetedMember }),
            Array.AsReadOnly(new[] { retargetedMember }),
            Array.AsReadOnly(new[] { firstMember }));
        var malformed = new CleanupPlan(
            CleanupPlannerTests.Result(CleanupPlannerTests.Group(first, second)),
            Array.AsReadOnly(new[] { malformedGroup }));
        var platform = new FakePlatform();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new CleanupEngine(platform).ExecuteAsync(malformed));

        Assert.AreEqual(0, platform.RecycleCalls);
        Assert.IsEmpty(platform.Validated);
    }

    [TestMethod]
    public async Task EmptyAndLargeMetadataGroupsUseExactCheckedReclaimedBytes()
    {
        CleanupPlan plan = CleanupPlannerTests.Plan(
            CleanupPlannerTests.Group(CleanupPlannerTests.File("empty-a", 0, 1), CleanupPlannerTests.File("empty-b", 0, 2)),
            CleanupPlannerTests.Group(CleanupPlannerTests.File("large-a", 2_147_483_648L, 3), CleanupPlannerTests.File("large-b", 2_147_483_648L, 4)));

        CleanupResult result = await new CleanupEngine(new FakePlatform()).ExecuteAsync(plan);

        Assert.AreEqual(2, result.RecycledFileCount);
        Assert.AreEqual(2_147_483_648L, result.ActuallyReclaimedBytes);
    }

    [TestMethod]
    public async Task TwoThousandCandidateExecutionRetainsMetadataOnlyAndProducesExactResult()
    {
        const int groupCount = 2000;
        DuplicateFileGroup[] groups = Enumerable.Range(0, groupCount)
            .Select(index => CleanupPlannerTests.Group(
                CleanupPlannerTests.File($"keeper-{index}.bin", 4096, (ulong)(index * 2 + 1)),
                CleanupPlannerTests.File($"candidate-{index}.bin", 4096, (ulong)(index * 2 + 2))))
            .ToArray();
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var timer = Stopwatch.StartNew();

        CleanupResult result = await new CleanupEngine(new FakePlatform()).ExecuteAsync(CleanupPlannerTests.Plan(groups));

        timer.Stop();
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        Assert.AreEqual(groupCount, result.RecycledFileCount);
        Assert.AreEqual(groupCount * 4096L, result.ActuallyReclaimedBytes);
        TestContext.WriteLine($"groups={groupCount}; candidates={groupCount}; elapsedMs={timer.ElapsedMilliseconds}; managedBefore={memoryBefore}; managedAfter={memoryAfter}");
    }

    [TestMethod]
    public async Task DeterministicGeneratedMatrixNeverRunsWithoutAValidIndependentKeeper()
    {
        var random = new Random(0x5A17);
        const int cases = 1000;
        for (int testCase = 0; testCase < cases; testCase++)
        {
            int memberCount = random.Next(2, 13);
            int selectedCount = random.Next(1, memberCount);
            DiscoveredFile[] files = Enumerable.Range(0, memberCount)
                .Select(index => CleanupPlannerTests.File($"{testCase}-{index}.bin", testCase % 31, (ulong)(testCase * 20 + index + 1)))
                .ToArray();
            PhysicalFileIdentity[] selected = files.Skip(memberCount - selectedCount).Select(file => file.PhysicalIdentity).ToArray();
            CleanupPlan plan = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(CleanupPlannerTests.Result(CleanupPlannerTests.Group(files)), selected)).Plan!;
            var platform = new FakePlatform();
            foreach (CleanupPlanMember keeper in plan.Groups[0].Keepers)
            {
                if (random.Next(4) == 0) platform.Validation[keeper.ExpectedFile.PhysicalIdentity] = CleanupFileValidationStatus.Changed;
            }

            CleanupResult result = await new CleanupEngine(platform).ExecuteAsync(plan);
            bool anyValidKeeper = plan.Groups[0].Keepers.Any(keeper => !platform.Validation.TryGetValue(keeper.ExpectedFile.PhysicalIdentity, out CleanupFileValidationStatus status) || status == CleanupFileValidationStatus.Valid);

            Assert.IsTrue(anyValidKeeper || result.RecycledFileCount == 0, $"case {testCase}");
            Assert.IsLessThan(memberCount, result.RecycledFileCount, $"case {testCase}");
            Assert.AreEqual(result.RecycledFileCount, platform.RecycleCalls, $"case {testCase}");
        }
    }

    private static CleanupPlan TwoMemberPlan() => CleanupPlannerTests.Plan(CleanupPlannerTests.Group(
        CleanupPlannerTests.File("keeper.bin", 4, 1), CleanupPlannerTests.File("candidate.bin", 4, 2)));

    private static CleanupPlan ThreeMemberPlan() => CleanupPlannerTests.Plan(CleanupPlannerTests.Group(
        CleanupPlannerTests.File("keeper.bin", 4, 1), CleanupPlannerTests.File("candidate-1.bin", 4, 2), CleanupPlannerTests.File("candidate-2.bin", 4, 3)));

    private sealed class FakePlatform : ICleanupPlatformService
    {
        public Dictionary<PhysicalFileIdentity, CleanupFileValidationStatus> Validation { get; } = [];
        public Queue<CleanupRecycleAttempt> Attempts { get; } = [];
        public List<PhysicalFileIdentity> Validated { get; } = [];
        public int RecycleCalls { get; private set; }
        public bool ThrowOnRecycle { get; init; }
        public Action<int>? OnRecycle { get; set; }

        public Task<CleanupFileValidation> ValidateAsync(CleanupPlanMember member, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validated.Add(member.ExpectedFile.PhysicalIdentity);
            CleanupFileValidationStatus status = Validation.GetValueOrDefault(member.ExpectedFile.PhysicalIdentity, CleanupFileValidationStatus.Valid);
            return Task.FromResult(new CleanupFileValidation(status));
        }

        public Task<CleanupRecycleAttempt> RevalidateAndRecycleAsync(CleanupPlanMember candidate, CleanupPlanMember keeper, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecycleCalls++;
            if (ThrowOnRecycle) throw new IOException("deterministic platform failure");
            CleanupRecycleAttempt result = Attempts.Count > 0 ? Attempts.Dequeue() : new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled);
            OnRecycle?.Invoke(RecycleCalls);
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingPlatform : ICleanupPlatformService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CleanupFileValidation> ValidateAsync(CleanupPlanMember member, CancellationToken cancellationToken = default) =>
            Task.FromResult(CleanupFileValidation.Valid());

        public async Task<CleanupRecycleAttempt> RevalidateAndRecycleAsync(CleanupPlanMember candidate, CleanupPlanMember keeper, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled);
        }
    }

    private sealed class ThrowingProgress : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => throw new InvalidOperationException("observer failure");
    }

    private sealed class CapturingProgress : IProgress<CleanupProgress>
    {
        public List<CleanupProgress> Values { get; } = [];
        public void Report(CleanupProgress value) => Values.Add(value);
    }

    private sealed class CancellingPlatform(CancellationTokenSource cancellation, bool cancelDuringRecycle) : ICleanupPlatformService
    {
        public int RecycleCalls { get; private set; }

        public Task<CleanupFileValidation> ValidateAsync(CleanupPlanMember member, CancellationToken cancellationToken = default)
        {
            if (!cancelDuringRecycle)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(CleanupFileValidation.Valid());
        }

        public Task<CleanupRecycleAttempt> RevalidateAndRecycleAsync(CleanupPlanMember candidate, CleanupPlanMember keeper, CancellationToken cancellationToken = default)
        {
            RecycleCalls++;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled));
        }
    }
}
