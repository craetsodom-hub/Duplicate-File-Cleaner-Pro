using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class ExactDuplicateDetectorTests
{
    [TestMethod]
    public async Task EmptySingleAndDifferentSizeInputsNeverInvokeContentAnalysis()
    {
        DiscoveredFile single = File("single.bin", 1, 1);
        DiscoveredFile differentSize = File("other.bin", 2, 2);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool>());

        ExactDuplicateDetectionResult empty = await ExactDuplicateDetector.DetectAsync([], analysis);
        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([single, differentSize], analysis);

        Assert.IsEmpty(empty.Groups);
        Assert.IsEmpty(result.Groups);
        Assert.AreEqual(0, analysis.HashCalls);
    }

    [TestMethod]
    public async Task DetectsOnlyVerifiedExactGroupsWithDeterministicOrderAndReclaimableBytes()
    {
        DiscoveredFile alpha = File("z\\alpha.bin", 4, 1);
        DiscoveredFile beta = File("a\\beta.txt", 4, 2);
        DiscoveredFile differentLength = File("a\\other.bin", 5, 3);
        DiscoveredFile sameLengthDifferent = File("a\\same-size.dat", 4, 4);
        var analysis = new FakeAnalysis(
            new Dictionary<(DiscoveredFile, DiscoveredFile), bool>
            {
                [(alpha, beta)] = true,
                [(alpha, sameLengthDifferent)] = false,
            });

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([sameLengthDifferent, alpha, differentLength, beta], analysis);

        Assert.IsFalse(result.WasCancelled);
        Assert.HasCount(1, result.Groups);
        CollectionAssert.AreEqual(new[] { beta.NormalizedPath, alpha.NormalizedPath }, result.Groups[0].Files.Select(file => file.NormalizedPath).ToArray());
        Assert.AreEqual(4, result.Groups[0].ReclaimableBytes);
        Assert.AreEqual(4, result.TotalReclaimableBytes);
    }

    [TestMethod]
    public async Task MultipleIndependentGroupsHaveOverflowCheckedTotalReclaimableBytes()
    {
        DiscoveredFile first = File("a.bin", 3, 1);
        DiscoveredFile firstCopy = File("b.bin", 3, 2);
        DiscoveredFile second = File("c.bin", 5, 3);
        DiscoveredFile secondCopy = File("d.bin", 5, 4);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool>
        {
            [(first, firstCopy)] = true,
            [(second, secondCopy)] = true,
        });

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([secondCopy, firstCopy, second, first], analysis);

        Assert.HasCount(2, result.Groups);
        Assert.AreEqual(8, result.TotalReclaimableBytes);
    }

    [TestMethod]
    public async Task CollidingDigestDoesNotCreateFalseDuplicate()
    {
        DiscoveredFile first = File("a.bin", 7, 1);
        DiscoveredFile second = File("b.bin", 7, 2);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool> { [(first, second)] = false });

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([first, second], analysis);

        Assert.IsEmpty(result.Groups);
    }

    [TestMethod]
    public async Task HardLinkAliasesCollapseToOnePhysicalFile()
    {
        DiscoveredFile aliasOne = File("a-alias.bin", 8, 9);
        DiscoveredFile aliasTwo = aliasOne with { NormalizedPath = "z-alias.bin" };
        DiscoveredFile independentCopy = File("copy.bin", 8, 10);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool> { [(aliasOne, independentCopy)] = true });

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([aliasTwo, independentCopy, aliasOne], analysis);

        Assert.HasCount(1, result.Groups);
        Assert.HasCount(2, result.Groups[0].Files);
        Assert.AreEqual(8, result.Groups[0].ReclaimableBytes);
    }

    [TestMethod]
    public async Task AnalysisFailuresAreIsolatedAndCancellationIsDistinct()
    {
        DiscoveredFile first = File("a.bin", 1, 1);
        DiscoveredFile unavailable = File("b.bin", 1, 2);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool>()) { Failures = [unavailable] };
        ExactDuplicateDetectionResult failed = await ExactDuplicateDetector.DetectAsync([first, unavailable], analysis);
        Assert.IsFalse(failed.WasCancelled);
        Assert.HasCount(1, failed.SkippedItems);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ExactDuplicateDetectionResult cancelled = await ExactDuplicateDetector.DetectAsync([first, unavailable], analysis, cancellationToken: cancellation.Token);
        Assert.IsTrue(cancelled.WasCancelled);
        Assert.IsEmpty(cancelled.Groups);
    }

    [TestMethod]
    public async Task ThreeMemberGroupRequiresVerificationAndCountsOnlyIndependentCopies()
    {
        DiscoveredFile first = File("a.bin", 9, 1);
        DiscoveredFile second = File("b.bin", 9, 2);
        DiscoveredFile third = File("c.bin", 9, 3);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool>
        {
            [(first, second)] = true,
            [(first, third)] = true,
        });

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([third, second, first], analysis);

        Assert.HasCount(1, result.Groups);
        Assert.HasCount(3, result.Groups[0].Files);
        Assert.AreEqual(18, result.TotalReclaimableBytes);
        Assert.AreEqual(3, analysis.ValidationCalls);
    }

    [TestMethod]
    public async Task ComparisonFailureInvalidatesTheEntirePartiallyVerifiedSet()
    {
        DiscoveredFile first = File("a.bin", 9, 1);
        DiscoveredFile second = File("b.bin", 9, 2);
        DiscoveredFile third = File("c.bin", 9, 3);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool> { [(first, second)] = true })
        {
            ComparisonFailures = [(first, third)],
        };

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([first, second, third], analysis);

        Assert.IsEmpty(result.Groups);
        Assert.HasCount(3, result.SkippedItems);
    }

    [TestMethod]
    public async Task FinalSnapshotFailureInvalidatesTheEntireGroup()
    {
        DiscoveredFile first = File("a.bin", 9, 1);
        DiscoveredFile second = File("b.bin", 9, 2);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool> { [(first, second)] = true })
        {
            ValidationFailures = [second],
        };

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([first, second], analysis);

        Assert.IsEmpty(result.Groups);
        Assert.HasCount(2, result.SkippedItems);
    }

    [TestMethod]
    public void ContentDigestOwnsImmutableInputMemory()
    {
        byte[] input = [1, 2, 3];
        var digest = new ContentDigest(input);
        input[0] = 9;

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, digest.ToArray());
    }

    [TestMethod]
    public async Task Full128BitFileIdParticipatesInPhysicalIdentity()
    {
        DiscoveredFile first = File("a.bin", 5, 7);
        DiscoveredFile second = File("b.bin", 5, 7) with { PhysicalIdentity = new PhysicalFileIdentity(1, 7, 1) };
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool> { [(first, second)] = true });

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([first, second], analysis);

        Assert.HasCount(1, result.Groups);
    }

    [TestMethod]
    public async Task ReclaimableArithmeticFailsClosedOnOverflow()
    {
        DiscoveredFile first = File("a.bin", long.MaxValue, 1);
        DiscoveredFile second = File("b.bin", long.MaxValue, 2);
        DiscoveredFile third = File("c.bin", long.MaxValue, 3);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool>
        {
            [(first, second)] = true,
            [(first, third)] = true,
        });

        await Assert.ThrowsExactlyAsync<OverflowException>(() => ExactDuplicateDetector.DetectAsync([first, second, third], analysis));
    }

    [TestMethod]
    public async Task ThrowingProgressSubscriberCannotChangeDetectionCorrectness()
    {
        DiscoveredFile first = File("a.bin", 5, 1);
        DiscoveredFile second = File("b.bin", 5, 2);
        var analysis = new FakeAnalysis(new Dictionary<(DiscoveredFile, DiscoveredFile), bool> { [(first, second)] = true });

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync([first, second], analysis, new ThrowingProgress());

        Assert.HasCount(1, result.Groups);
    }

    private static DiscoveredFile File(string path, long length, ulong id) => new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id, 0), FileAttributes.Normal);

    private sealed class FakeAnalysis(IReadOnlyDictionary<(DiscoveredFile, DiscoveredFile), bool> comparisons) : IContentAnalysisService
    {
        public HashSet<DiscoveredFile> Failures { get; init; } = [];
        public HashSet<(DiscoveredFile, DiscoveredFile)> ComparisonFailures { get; init; } = [];
        public HashSet<DiscoveredFile> ValidationFailures { get; init; } = [];

        public int HashCalls { get; private set; }
        public int ValidationCalls { get; private set; }

        public Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default)
        {
            HashCalls++;
            return Task.FromResult(Failures.Contains(file)
                ? ContentHashOutcome.Failure(ContentAnalysisFailureReason.Unavailable)
                : ContentHashOutcome.Success(new ContentDigest([1, 2, 3])));
        }

        public Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default)
        {
            if (ComparisonFailures.Contains((left, right)) || ComparisonFailures.Contains((right, left)))
            {
                return Task.FromResult(ContentComparisonOutcome.Failure(ContentAnalysisFailureReason.ComparisonFailed));
            }

            bool areEqual = comparisons.TryGetValue((left, right), out bool value)
                ? value
                : comparisons.TryGetValue((right, left), out value) && value;
            return Task.FromResult(areEqual ? ContentComparisonOutcome.Equal() : ContentComparisonOutcome.Different());
        }

        public Task<ContentValidationOutcome> ValidateAsync(DiscoveredFile file, CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            return Task.FromResult(ValidationFailures.Contains(file)
                ? ContentValidationOutcome.Failure(ContentAnalysisFailureReason.ChangedDuringAnalysis)
                : ContentValidationOutcome.Valid());
        }
    }

    private sealed class ThrowingProgress : IProgress<DuplicateDetectionProgress>
    {
        public void Report(DuplicateDetectionProgress value) => throw new InvalidOperationException("Progress consumer failure");
    }
}
