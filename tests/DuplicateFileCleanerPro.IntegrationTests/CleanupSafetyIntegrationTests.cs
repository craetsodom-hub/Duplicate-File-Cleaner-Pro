using System.Runtime.InteropServices;
using System.Text;
using DuplicateFileCleanerPro.App.Cleanup;
using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Cleanup;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class CleanupSafetyIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RecycleBinBoundaryStructurallyRequiresRecycleOnDeleteFlag()
    {
        Assert.AreNotEqual(0u, WindowsShellRecycleBin.RecycleOperationFlags & WindowsShellRecycleBin.RequiredRecycleFlag);
        Assert.AreEqual(0x00080000u, WindowsShellRecycleBin.RequiredRecycleFlag);
        Assert.AreNotEqual(0u, WindowsShellRecycleBin.RecycleOperationFlags & WindowsShellRecycleBin.RequiredUndoFlag);
        Assert.AreEqual(0x20000000u, WindowsShellRecycleBin.RequiredUndoFlag);
    }

    [TestMethod]
    public async Task CandidatePathReplacementBeforeFinalValidationIsNeverRecycled()
    {
        using var corpus = await CleanupCorpus.CreateAsync("replacement");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        string displaced = candidate.ExpectedFile.NormalizedPath + ".original";
        var recycleBin = new RecordingRecycleBin();
        var observer = new ActionObserver((_, _) =>
        {
            File.Move(candidate.ExpectedFile.NormalizedPath, displaced);
            File.WriteAllText(candidate.ExpectedFile.NormalizedPath, corpus.Content, new UTF8Encoding(false));
        });

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin, observer)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedIdentityMismatch, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(candidate.ExpectedFile.NormalizedPath));
        Assert.IsTrue(File.Exists(displaced));
    }

    [TestMethod]
    public async Task KeeperReplacementPreventsCandidateRecycle()
    {
        using var corpus = await CleanupCorpus.CreateAsync("keeper-replacement");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember keeper = plan.Groups[0].Keepers[0];
        string displaced = keeper.ExpectedFile.NormalizedPath + ".original";
        var recycleBin = new RecordingRecycleBin();
        var observer = new ActionObserver((_, _) =>
        {
            File.Move(keeper.ExpectedFile.NormalizedPath, displaced);
            File.WriteAllText(keeper.ExpectedFile.NormalizedPath, corpus.Content, new UTF8Encoding(false));
        });

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin, observer)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(plan.Groups[0].Candidates[0].ExpectedFile.NormalizedPath));
    }

    [TestMethod]
    public async Task CandidateMutationWithRestoredLastWriteTimeIsDetectedByChangeTime()
    {
        using var corpus = await CleanupCorpus.CreateAsync("candidate-mutation");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        var recycleBin = new RecordingRecycleBin();
        var observer = new ActionObserver((_, _) =>
        {
            byte[] mutation = Encoding.UTF8.GetBytes(new string('X', Encoding.UTF8.GetByteCount(corpus.Content)));
            File.WriteAllBytes(candidate.ExpectedFile.NormalizedPath, mutation);
            File.SetLastWriteTimeUtc(candidate.ExpectedFile.NormalizedPath, candidate.ExpectedFile.LastWriteTimeUtc.UtcDateTime);
        });

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin, observer)).ExecuteAsync(plan);

        Assert.Contains(result.Groups[0].Outcomes[0].Status, new[]
        {
            CleanupCandidateOutcomeStatus.SkippedChanged,
            CleanupCandidateOutcomeStatus.SkippedVerificationFailed,
        });
        Assert.AreEqual(0, recycleBin.CallCount);
    }

    [TestMethod]
    public async Task KeeperMutationWithRestoredLastWriteTimePreventsCandidateRecycle()
    {
        using var corpus = await CleanupCorpus.CreateAsync("keeper-mutation");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember keeper = plan.Groups[0].Keepers[0];
        var recycleBin = new RecordingRecycleBin();
        var observer = new ActionObserver((_, _) =>
        {
            byte[] mutation = Encoding.UTF8.GetBytes(new string('K', Encoding.UTF8.GetByteCount(corpus.Content)));
            File.WriteAllBytes(keeper.ExpectedFile.NormalizedPath, mutation);
            File.SetLastWriteTimeUtc(keeper.ExpectedFile.NormalizedPath, keeper.ExpectedFile.LastWriteTimeUtc.UtcDateTime);
        });

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin, observer)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycleBin.CallCount);
    }

    [TestMethod]
    public async Task CandidateLengthChangeIsRejectedBeforeRecycle()
    {
        using var corpus = await CleanupCorpus.CreateAsync("candidate-length");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        await File.AppendAllTextAsync(candidate.ExpectedFile.NormalizedPath, "length-change", new UTF8Encoding(false));
        var recycleBin = new RecordingRecycleBin();

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedChanged, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(candidate.ExpectedFile.NormalizedPath));
    }

    [TestMethod]
    public async Task KeeperLengthChangePreventsCandidateRecycle()
    {
        using var corpus = await CleanupCorpus.CreateAsync("keeper-length");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember keeper = plan.Groups[0].Keepers[0];
        await File.AppendAllTextAsync(keeper.ExpectedFile.NormalizedPath, "length-change", new UTF8Encoding(false));
        var recycleBin = new RecordingRecycleBin();

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(plan.Groups[0].Candidates[0].ExpectedFile.NormalizedPath));
    }

    [TestMethod]
    public async Task CurrentByteMismatchFailsEvenWhenSnapshotMetadataAndIdentitiesMatch()
    {
        using var corpus = await CleanupCorpus.CreateAsync("content-proof");
        CleanupPlan originalPlan = await corpus.CreatePlanAsync();
        CleanupPlanMember originalCandidate = originalPlan.Groups[0].Candidates[0];
        byte[] mutation = Encoding.UTF8.GetBytes(new string('M', Encoding.UTF8.GetByteCount(corpus.Content)));
        await File.WriteAllBytesAsync(originalCandidate.ExpectedFile.NormalizedPath, mutation);

        RootNormalizationResult roots = new WindowsScanRootNormalizer().Normalize([corpus.Root]);
        DiscoveryResult currentDiscovery = await new WindowsFileDiscoveryService().DiscoverAsync(roots.Roots, new DiscoveryPolicy());
        DiscoveredFile currentCandidate = currentDiscovery.Files.Single(file => file.FileName.StartsWith("candidate", StringComparison.Ordinal));
        DiscoveredFile currentKeeper = currentDiscovery.Files.Single(file => file.FileName.StartsWith("keeper", StringComparison.Ordinal));
        var forgedGroup = new DuplicateFileGroup(
            Array.AsReadOnly(new[] { currentKeeper, currentCandidate }),
            currentCandidate.Length);
        var forgedResult = new CompletedScanResult(
            currentDiscovery,
            new ExactDuplicateDetectionResult([forgedGroup], [], forgedGroup.ReclaimableBytes, false));
        CleanupPlan plan = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(forgedResult, [currentCandidate.PhysicalIdentity])).Plan!;
        var recycleBin = new RecordingRecycleBin();

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedVerificationFailed, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(currentCandidate.NormalizedPath));
    }

    [TestMethod]
    public async Task HardLinkSubstitutionCannotBorrowKeeperIdentityAsCandidateAuthorization()
    {
        using var corpus = await CleanupCorpus.CreateAsync("hardlink-substitution");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        CleanupPlanMember keeper = plan.Groups[0].Keepers[0];
        string displaced = candidate.ExpectedFile.NormalizedPath + ".original";
        var recycleBin = new RecordingRecycleBin();
        var observer = new ActionObserver((_, _) =>
        {
            File.Move(candidate.ExpectedFile.NormalizedPath, displaced);
            if (!CreateHardLink(candidate.ExpectedFile.NormalizedPath, keeper.ExpectedFile.NormalizedPath, IntPtr.Zero))
            {
                throw new InvalidOperationException($"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
            }
        });

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin, observer)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(displaced));
        Assert.IsTrue(File.Exists(keeper.ExpectedFile.NormalizedPath));
    }

    [TestMethod]
    public async Task SelectedCandidateWithAnAdditionalHardLinkIsNotCountedAsReclaimableCleanup()
    {
        using var corpus = await CleanupCorpus.CreateAsync("hardlink-candidate");
        string candidatePath = Directory.EnumerateFiles(corpus.Root).Single(path => Path.GetFileName(path).StartsWith("candidate", StringComparison.Ordinal));
        string aliasPath = Path.Combine(corpus.Root, "zz-hardlink-alias.bin");
        if (!CreateHardLink(aliasPath, candidatePath, IntPtr.Zero))
        {
            Assert.Inconclusive($"Hard-link creation is unavailable: {Marshal.GetLastWin32Error()}.");
        }

        CleanupPlan plan = await corpus.CreatePlanAsync();
        var recycleBin = new RecordingRecycleBin();

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedPolicy, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, result.RecycledFileCount);
        Assert.AreEqual(0L, result.ActuallyReclaimedBytes);
        Assert.AreEqual(0, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(aliasPath));
    }

    [TestMethod]
    public async Task CandidateDisappearanceIsFactualSkipAndCountsNoBytes()
    {
        using var corpus = await CleanupCorpus.CreateAsync("candidate-missing");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        var recycleBin = new RecordingRecycleBin();
        var observer = new ActionObserver((_, _) => File.Move(candidate.ExpectedFile.NormalizedPath, candidate.ExpectedFile.NormalizedPath + ".gone"));

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin, observer)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedMissing, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0L, result.ActuallyReclaimedBytes);
        Assert.AreEqual(0, recycleBin.CallCount);
    }

    [TestMethod]
    public async Task KeeperDisappearanceBeforeCleanupLeavesCandidateUntouched()
    {
        using var corpus = await CleanupCorpus.CreateAsync("keeper-missing");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember keeper = plan.Groups[0].Keepers[0];
        File.Move(keeper.ExpectedFile.NormalizedPath, keeper.ExpectedFile.NormalizedPath + ".gone");
        var recycleBin = new RecordingRecycleBin();

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedKeeperUnavailable, result.Groups[0].Outcomes[0].Status);
        Assert.IsTrue(File.Exists(plan.Groups[0].Candidates[0].ExpectedFile.NormalizedPath));
        Assert.AreEqual(0, recycleBin.CallCount);
    }

    [TestMethod]
    public async Task RenameAfterScanIsNotChased()
    {
        using var corpus = await CleanupCorpus.CreateAsync("renamed");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        string renamed = candidate.ExpectedFile.NormalizedPath + ".renamed";
        File.Move(candidate.ExpectedFile.NormalizedPath, renamed);
        var recycleBin = new RecordingRecycleBin();

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.SkippedMissing, result.Groups[0].Outcomes[0].Status);
        Assert.IsTrue(File.Exists(renamed));
        Assert.AreEqual(0, recycleBin.CallCount);
    }

    [TestMethod]
    public async Task RecycleBinFailureNeverFallsBackAndLeavesCandidateUntouched()
    {
        using var corpus = await CleanupCorpus.CreateAsync("recycle-failure");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        var recycleBin = new RecordingRecycleBin(succeeds: false);

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin)).ExecuteAsync(plan);

        Assert.AreEqual(CleanupCandidateOutcomeStatus.FailedRecycleBin, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, result.RecycledFileCount);
        Assert.AreEqual(0L, result.ActuallyReclaimedBytes);
        Assert.AreEqual(1, recycleBin.CallCount);
        Assert.IsTrue(File.Exists(candidate.ExpectedFile.NormalizedPath));
    }

    [TestMethod]
    public async Task ReparseSubstitutionIsRejectedWithoutFollowingTarget()
    {
        using var corpus = await CleanupCorpus.CreateAsync("reparse");
        CleanupPlan plan = await corpus.CreatePlanAsync();
        CleanupPlanMember candidate = plan.Groups[0].Candidates[0];
        string displaced = candidate.ExpectedFile.NormalizedPath + ".original";
        string unrelated = Path.Combine(corpus.Root, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "must survive", new UTF8Encoding(false));
        var recycleBin = new RecordingRecycleBin();
        var observer = new ActionObserver((_, _) =>
        {
            File.Move(candidate.ExpectedFile.NormalizedPath, displaced);
            if (!CreateSymbolicLink(candidate.ExpectedFile.NormalizedPath, unrelated, 0))
            {
                throw new InvalidOperationException($"CreateSymbolicLink failed: {Marshal.GetLastWin32Error()}");
            }
        });

        CleanupResult result = await new CleanupEngine(new WindowsCleanupPlatformService(recycleBin, observer)).ExecuteAsync(plan);

        if (result.Groups[0].Outcomes[0].Status == CleanupCandidateOutcomeStatus.FailedPlatform && !File.Exists(candidate.ExpectedFile.NormalizedPath))
        {
            Assert.Inconclusive("Symbolic-link creation is unavailable for this test user.");
        }

        Assert.Contains(result.Groups[0].Outcomes[0].Status, new[]
        {
            CleanupCandidateOutcomeStatus.SkippedPolicy,
            CleanupCandidateOutcomeStatus.SkippedMissing,
        });
        Assert.AreEqual("must survive", await File.ReadAllTextAsync(unrelated));
        Assert.AreEqual(0, recycleBin.CallCount);
    }

    [TestMethod]
    [TestCategory("RecycleBinSmoke")]
    public async Task RealPresentationWorkflowMovesOnlySelectedDisposableCandidateToRecycleBin()
    {
        using var corpus = await CleanupCorpus.CreateAsync("real-recycle");
        CompletedScanResult completed = await corpus.CreateCompletedResultAsync();
        var review = new ResultsReviewViewModel(completed);
        ResultMemberViewModel candidate = review.AllGroups.Single().Members.Single(member => member.File.FileName.StartsWith("candidate", StringComparison.Ordinal));
        ResultMemberViewModel keeper = review.AllGroups.Single().Members.Single(member => member.File.FileName.StartsWith("keeper", StringComparison.Ordinal));
        candidate.IsSelected = true;
        var workflow = new CleanupWorkflowViewModel(new CleanupEngine(new WindowsCleanupPlatformService()));

        Assert.IsTrue(workflow.BeginReview(review));
        Assert.AreEqual(1, workflow.SelectedCandidateCount);
        Assert.AreEqual(candidate.File.Length, workflow.SelectedCandidateBytes);

        CleanupResult? result = await workflow.ExecuteConfirmedAsync();

        Assert.IsNotNull(result);
        Assert.AreEqual(CleanupCandidateOutcomeStatus.Recycled, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(1, result.RecycledFileCount);
        Assert.AreEqual(candidate.File.Length, result.ActuallyReclaimedBytes);
        Assert.AreSame(result, workflow.Result);
        Assert.IsTrue(workflow.RequiresRescan);
        Assert.IsFalse(File.Exists(candidate.File.NormalizedPath));
        Assert.IsTrue(File.Exists(keeper.File.NormalizedPath));
        Assert.AreEqual(corpus.Content, await File.ReadAllTextAsync(keeper.File.NormalizedPath));
        TestContext.WriteLine($"groups=1; members=2; recycled={result.RecycledFileCount}; reclaimedBytes={result.ActuallyReclaimedBytes}; keeperExists={File.Exists(keeper.File.NormalizedPath)}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string symbolicLink, string target, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class RecordingRecycleBin(bool succeeds = true) : IWindowsRecycleBin
    {
        public int CallCount { get; private set; }

        public Task<WindowsRecycleBinResult> RecycleAsync(string absolutePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new WindowsRecycleBinResult(succeeds, succeeds ? null : -1));
        }
    }

    private sealed class ActionObserver(Action<CleanupPlanMember, CleanupPlanMember> action) : ICleanupExecutionObserver
    {
        public void BeforeFinalRecycleValidation(CleanupPlanMember candidate, CleanupPlanMember keeper) => action(candidate, keeper);
    }

    private sealed class CleanupCorpus : IDisposable
    {
        private CleanupCorpus(string root, string content)
        {
            Root = root;
            Content = content;
        }

        public string Root { get; }
        public string Content { get; }

        public static async Task<CleanupCorpus> CreateAsync(string name)
        {
            string root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Phase6", $"{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            const string content = "verified exact duplicate cleanup payload ✓";
            await File.WriteAllTextAsync(Path.Combine(root, "keeper-α.txt"), content, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "candidate-副本.bin"), content, new UTF8Encoding(false));
            return new CleanupCorpus(root, content);
        }

        public async Task<CleanupPlan> CreatePlanAsync()
        {
            CompletedScanResult completed = await CreateCompletedResultAsync();
            DuplicateFileGroup group = completed.Detection.Groups[0];
            DiscoveredFile candidate = group.Files.Single(file => file.FileName.StartsWith("candidate", StringComparison.Ordinal));
            CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(completed, [candidate.PhysicalIdentity]));
            Assert.IsTrue(planning.Succeeded);
            return planning.Plan!;
        }

        public async Task<CompletedScanResult> CreateCompletedResultAsync()
        {
            RootNormalizationResult roots = new WindowsScanRootNormalizer().Normalize([Root]);
            DiscoveryResult discovery = await new WindowsFileDiscoveryService().DiscoverAsync(roots.Roots, new DiscoveryPolicy());
            ExactDuplicateDetectionResult detection = await ExactDuplicateDetector.DetectAsync(discovery.Files, new WindowsContentAnalysisService());
            Assert.HasCount(1, detection.Groups);
            return new CompletedScanResult(discovery, detection);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
