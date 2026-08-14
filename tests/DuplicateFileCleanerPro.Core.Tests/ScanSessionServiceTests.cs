using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class ScanSessionServiceTests
{
    [TestMethod]
    public async Task RequiresRootsAndDoesNotStart()
    {
        using var service = new ScanSessionService(new FakeDiscovery([]), new FakeAnalysis());
        ScanSessionResult result = await service.RunAsync([], new DiscoveryPolicy());
        Assert.AreEqual(ScanSessionState.Failed, result.State);
    }

    [TestMethod]
    public async Task CompletesWithDeterministicVerifiedResultAndMonotonicProgress()
    {
        DiscoveredFile first = File("a.bin", 4, 1);
        DiscoveredFile second = File("b.txt", 4, 2);
        var progress = new List<ScanSessionProgress>();
        using var service = new ScanSessionService(new FakeDiscovery([first, second]), new FakeAnalysis());

        ScanSessionResult result = await service.RunAsync([new ScanRoot("C:\\Test")], new DiscoveryPolicy(), new CollectProgress(progress));

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
        Assert.HasCount(1, result.CompletedResult.Detection.Groups);
        Assert.AreEqual(4, result.CompletedResult.Detection.TotalReclaimableBytes);
        Assert.IsTrue(progress.Any(item => item.State == ScanSessionState.Discovering && item.TotalCandidateBytes == 0));
        Assert.AreEqual(ScanSessionState.Completed, progress[^1].State);
    }

    [TestMethod]
    public async Task CancellationReturnsNoCompletedResultAndCanBeFollowedByAnotherSession()
    {
        DiscoveredFile first = File("a.bin", 4, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var service = new ScanSessionService(new FakeDiscovery([first]), new FakeAnalysis());

        ScanSessionResult cancelled = await service.RunAsync([new ScanRoot("C:\\Test")], new DiscoveryPolicy(), cancellationToken: cancellation.Token);
        ScanSessionResult next = await service.RunAsync([new ScanRoot("C:\\Test")], new DiscoveryPolicy());

        Assert.AreEqual(ScanSessionState.Cancelled, cancelled.State);
        Assert.IsNull(cancelled.CompletedResult);
        Assert.AreEqual(ScanSessionState.Completed, next.State);
    }

    [TestMethod]
    public async Task CancellationDuringDiscoveryDoesNotComplete()
    {
        using var cancellation = new CancellationTokenSource();
        using var service = new ScanSessionService(new DelayedDiscovery(), new FakeAnalysis());
        Task<ScanSessionResult> running = service.RunAsync([new ScanRoot("C:\\Test")], new DiscoveryPolicy(), cancellationToken: cancellation.Token);
        cancellation.Cancel();

        ScanSessionResult result = await running;

        Assert.AreEqual(ScanSessionState.Cancelled, result.State);
        Assert.IsNull(result.CompletedResult);
    }

    [TestMethod]
    public async Task CancellationDuringAnalysisDoesNotComplete()
    {
        DiscoveredFile first = File("a.bin", 4, 1);
        DiscoveredFile second = File("b.bin", 4, 2);
        using var cancellation = new CancellationTokenSource();
        var analysis = new DelayedAnalysis();
        using var service = new ScanSessionService(new FakeDiscovery([first, second]), analysis);
        Task<ScanSessionResult> running = service.RunAsync([new ScanRoot("C:\\Test")], new DiscoveryPolicy(), cancellationToken: cancellation.Token);
        await analysis.Started.Task;
        cancellation.Cancel();

        ScanSessionResult result = await running;

        Assert.AreEqual(ScanSessionState.Cancelled, result.State);
        Assert.IsNull(result.CompletedResult);
    }

    [TestMethod]
    public async Task FatalFailureIsNotReportedAsCompleted()
    {
        using var service = new ScanSessionService(new FailingDiscovery(), new FakeAnalysis());
        ScanSessionResult result = await service.RunAsync([new ScanRoot("C:\\Test")], new DiscoveryPolicy());

        Assert.AreEqual(ScanSessionState.Failed, result.State);
        Assert.IsNull(result.CompletedResult);
        Assert.IsNotNull(result.Failure);
    }

    private static DiscoveredFile File(string path, long length, ulong id) => new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id), FileAttributes.Normal);

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredFile> files) : IFileDiscoveryService
    {
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(new DiscoveryProgress("C:\\Test", files.Count, 0));
            return Task.FromResult(new DiscoveryResult(files, [], cancellationToken.IsCancellationRequested));
        }
    }

    private sealed class FakeAnalysis : IContentAnalysisService
    {
        public Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default) => Task.FromResult(ContentHashOutcome.Success(new ContentDigest([1])));
        public Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default) => Task.FromResult(ContentComparisonOutcome.Equal());
    }

    private sealed class DelayedDiscovery : IFileDiscoveryService
    {
        public async Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DiscoveryResult([], [], false);
        }
    }

    private sealed class DelayedAnalysis : IContentAnalysisService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ContentHashOutcome.Failure(ContentAnalysisFailureReason.Unavailable);
        }

        public Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default) => Task.FromResult(ContentComparisonOutcome.Different());
    }

    private sealed class FailingDiscovery : IFileDiscoveryService
    {
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) => throw new IOException("Generated failure");
    }

    private sealed class CollectProgress(List<ScanSessionProgress> target) : IProgress<ScanSessionProgress>
    {
        public void Report(ScanSessionProgress value) => target.Add(value);
    }
}
