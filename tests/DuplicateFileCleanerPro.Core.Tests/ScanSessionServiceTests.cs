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

    [TestMethod]
    public async Task OverlappingSessionIsRejectedAndServiceRecovers()
    {
        var discovery = new ControlledDiscovery();
        using var service = new ScanSessionService(discovery, new FakeAnalysis());
        Task<ScanSessionResult> first = service.RunAsync([new ScanRoot(@"C:\First")], new DiscoveryPolicy());
        await discovery.Started.Task;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.RunAsync([new ScanRoot(@"C:\Second")], new DiscoveryPolicy()));
        discovery.Complete();
        Assert.AreEqual(ScanSessionState.Completed, (await first).State);
        Assert.AreEqual(ScanSessionState.Completed, (await service.RunAsync([new ScanRoot(@"C:\Third")], new DiscoveryPolicy())).State);
    }

    [TestMethod]
    public async Task SelectedRootsAreSnapshottedBeforeWorkerExecution()
    {
        var roots = new List<ScanRoot> { new(@"C:\Original") };
        var discovery = new RecordingDiscovery();
        using var service = new ScanSessionService(discovery, new FakeAnalysis());

        Task<ScanSessionResult> running = service.RunAsync(roots, new DiscoveryPolicy());
        roots[0] = new ScanRoot(@"C:\Mutated");
        await running;

        Assert.HasCount(1, discovery.Paths);
        Assert.AreEqual(@"C:\Original", discovery.Paths[0]);
    }

    [TestMethod]
    public async Task SynchronouslyCompletingDependenciesStillRunOffTheCallingThread()
    {
        var discovery = new ThreadRecordingDiscovery();
        using var service = new ScanSessionService(discovery, new FakeAnalysis());
        var taskReady = new TaskCompletionSource<Task<ScanSessionResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThread = 0;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            taskReady.TrySetResult(service.RunAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy()));
        });

        caller.Start();
        Task<ScanSessionResult> running = await taskReady.Task;
        await running;
        caller.Join();

        Assert.AreNotEqual(callerThread, discovery.ThreadId);
    }

    [TestMethod]
    public async Task CancellationAtFinalVerificationCannotBecomeCompletedProgressOrResult()
    {
        DiscoveredFile first = File("a.bin", 4, 1);
        DiscoveredFile second = File("b.bin", 4, 2);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancellingProgress(cancellation);
        using var service = new ScanSessionService(new FakeDiscovery([first, second]), new FakeAnalysis());

        ScanSessionResult result = await service.RunAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy(), progress, cancellation.Token);

        Assert.AreEqual(ScanSessionState.Cancelled, result.State);
        Assert.IsFalse(progress.Items.Any(item => item.State == ScanSessionState.Completed));
    }

    [TestMethod]
    public async Task AnalysisProgressIsBoundedMonotonicAndVerificationIsExplicit()
    {
        DiscoveredFile first = File("a.bin", 4, 1);
        DiscoveredFile second = File("b.bin", 4, 2);
        var items = new List<ScanSessionProgress>();
        using var service = new ScanSessionService(new FakeDiscovery([first, second]), new FakeAnalysis());

        await service.RunAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy(), new CollectProgress(items));

        ScanSessionProgress[] analysis = items.Where(item => item.State == ScanSessionState.Analyzing).ToArray();
        Assert.IsTrue(analysis.All(item => item.BytesProcessed >= 0 && item.BytesProcessed <= item.TotalCandidateBytes));
        Assert.IsTrue(analysis.Zip(analysis.Skip(1), (left, right) => right.BytesProcessed >= left.BytesProcessed).All(value => value));
        Assert.IsTrue(analysis.Any(item => item.IsVerifying));
        Assert.AreEqual(ScanSessionState.Completed, items[^1].State);
    }

    [TestMethod]
    public async Task CompletionOwnsAReadOnlyDiscoverySnapshot()
    {
        DiscoveredFile first = File("a.bin", 1, 1);
        var mutableFiles = new List<DiscoveredFile> { first };
        using var service = new ScanSessionService(new FakeDiscovery(mutableFiles), new FakeAnalysis());

        ScanSessionResult result = await service.RunAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy());
        mutableFiles.Clear();

        Assert.IsNotNull(result.CompletedResult);
        Assert.HasCount(1, result.CompletedResult.Discovery.Files);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<DiscoveredFile>)result.CompletedResult.Discovery.Files).Add(first));
    }

    [TestMethod]
    public async Task ThrowingProgressSubscriberCannotChangeSessionCorrectness()
    {
        DiscoveredFile first = File("a.bin", 4, 1);
        DiscoveredFile second = File("b.bin", 4, 2);
        using var service = new ScanSessionService(new FakeDiscovery([first, second]), new FakeAnalysis());

        ScanSessionResult result = await service.RunAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy(), new ThrowingSessionProgress());

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
    }

    private static DiscoveredFile File(string path, long length, ulong id) => new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id, 0), FileAttributes.Normal);

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
        public Task<ContentValidationOutcome> ValidateAsync(DiscoveredFile file, CancellationToken cancellationToken = default) => Task.FromResult(ContentValidationOutcome.Valid());
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
        public Task<ContentValidationOutcome> ValidateAsync(DiscoveredFile file, CancellationToken cancellationToken = default) => Task.FromResult(ContentValidationOutcome.Valid());
    }

    private sealed class FailingDiscovery : IFileDiscoveryService
    {
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) => throw new IOException("Generated failure");
    }

    private sealed class ControlledDiscovery : IFileDiscoveryService
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new DiscoveryResult([], [], false);
        }

        public void Complete() => release.TrySetResult();
    }

    private sealed class RecordingDiscovery : IFileDiscoveryService
    {
        public string[] Paths { get; private set; } = [];
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Paths = roots.Select(root => root.NormalizedPath).ToArray();
            return Task.FromResult(new DiscoveryResult([], [], false));
        }
    }

    private sealed class ThreadRecordingDiscovery : IFileDiscoveryService
    {
        public int ThreadId { get; private set; }
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult(new DiscoveryResult([], [], false));
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<ScanSessionProgress>
    {
        public List<ScanSessionProgress> Items { get; } = [];
        public void Report(ScanSessionProgress value)
        {
            Items.Add(value);
            if (value.State == ScanSessionState.Analyzing && value.IsVerifying)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class CollectProgress(List<ScanSessionProgress> target) : IProgress<ScanSessionProgress>
    {
        public void Report(ScanSessionProgress value) => target.Add(value);
    }

    private sealed class ThrowingSessionProgress : IProgress<ScanSessionProgress>
    {
        public void Report(ScanSessionProgress value) => throw new InvalidOperationException("Progress consumer failure");
    }
}
