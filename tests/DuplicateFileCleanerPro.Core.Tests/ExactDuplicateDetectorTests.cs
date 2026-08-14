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
        ExactDuplicateDetectionResult cancelled = await ExactDuplicateDetector.DetectAsync([first, unavailable], analysis, cancellation.Token);
        Assert.IsTrue(cancelled.WasCancelled);
        Assert.IsEmpty(cancelled.Groups);
    }

    private static DiscoveredFile File(string path, long length, ulong id) => new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id), FileAttributes.Normal);

    private sealed class FakeAnalysis(IReadOnlyDictionary<(DiscoveredFile, DiscoveredFile), bool> comparisons) : IContentAnalysisService
    {
        public HashSet<DiscoveredFile> Failures { get; init; } = [];

        public int HashCalls { get; private set; }

        public Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default)
        {
            HashCalls++;
            return Task.FromResult(Failures.Contains(file)
                ? ContentHashOutcome.Failure(ContentAnalysisFailureReason.Unavailable)
                : ContentHashOutcome.Success(new ContentDigest([1, 2, 3])));
        }

        public Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default)
        {
            bool areEqual = comparisons.TryGetValue((left, right), out bool value)
                ? value
                : comparisons.TryGetValue((right, left), out value) && value;
            return Task.FromResult(areEqual ? ContentComparisonOutcome.Equal() : ContentComparisonOutcome.Different());
        }
    }
}
