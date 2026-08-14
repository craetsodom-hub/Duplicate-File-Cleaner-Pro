using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class ScanWorkflowControllerTests
{
    [TestMethod]
    public async Task CompletionPublishesOnlyCompletedSnapshotState()
    {
        DiscoveredFile first = File("first.bin", 4, 1);
        DiscoveredFile second = File("second.bin", 4, 2);
        using var workflow = CreateWorkflow(new ImmediateDiscovery([first, second]));

        ScanSessionResult result = await workflow.StartAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy());

        Assert.AreEqual(ScanSessionState.Completed, workflow.State);
        Assert.AreSame(result.CompletedResult, workflow.CompletedResult);
        Assert.IsNull(workflow.Failure);
        Assert.IsFalse(workflow.IsRunning);
    }

    [TestMethod]
    public async Task CancelIsIdempotentAndASecondRunCanComplete()
    {
        var discovery = new FirstRunBlockingDiscovery();
        using var workflow = CreateWorkflow(discovery);
        Task<ScanSessionResult> first = workflow.StartAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy());
        await discovery.Started.Task;

        workflow.Cancel();
        workflow.Cancel();
        ScanSessionResult cancelled = await first;
        ScanSessionResult completed = await workflow.StartAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy());

        Assert.AreEqual(ScanSessionState.Cancelled, cancelled.State);
        Assert.IsNull(cancelled.CompletedResult);
        Assert.AreEqual(ScanSessionState.Completed, completed.State);
        Assert.AreEqual(ScanSessionState.Completed, workflow.State);
    }

    [TestMethod]
    public async Task StartCannotReenterAnActiveWorkflow()
    {
        var discovery = new AlwaysBlockingDiscovery();
        using var workflow = CreateWorkflow(discovery);
        Task<ScanSessionResult> running = workflow.StartAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy());
        await discovery.Started.Task;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workflow.StartAsync([new ScanRoot(@"C:\Other")], new DiscoveryPolicy()));
        workflow.Cancel();
        await running;
    }

    [TestMethod]
    public async Task FailureNeverLeavesACompletedResult()
    {
        using var workflow = CreateWorkflow(new ThrowingDiscovery());

        ScanSessionResult result = await workflow.StartAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy());

        Assert.AreEqual(ScanSessionState.Failed, result.State);
        Assert.AreEqual(ScanSessionState.Failed, workflow.State);
        Assert.IsNull(workflow.CompletedResult);
        Assert.IsNotNull(workflow.Failure);
    }

    [TestMethod]
    public async Task DisposeCancelsActiveWorkAndRejectsFutureStarts()
    {
        var discovery = new AlwaysBlockingDiscovery();
        var workflow = CreateWorkflow(discovery);
        Task<ScanSessionResult> running = workflow.StartAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy());
        await discovery.Started.Task;

        workflow.Dispose();
        ScanSessionResult result = await running;

        Assert.AreEqual(ScanSessionState.Cancelled, result.State);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => workflow.StartAsync([new ScanRoot(@"C:\Test")], new DiscoveryPolicy()));
    }

    [TestMethod]
    public async Task LateProgressFromPreviousRunCannotContaminateNewState()
    {
        var discovery = new RetainingDiscovery();
        using var workflow = CreateWorkflow(discovery);
        await workflow.StartAsync([new ScanRoot(@"C:\First")], new DiscoveryPolicy());
        IProgress<DiscoveryProgress> staleProgress = discovery.FirstProgress!;
        await workflow.StartAsync([new ScanRoot(@"C:\Second")], new DiscoveryPolicy());

        staleProgress.Report(new DiscoveryProgress("stale", 999, 999));

        Assert.AreEqual(ScanSessionState.Completed, workflow.State);
        Assert.IsNotNull(workflow.CompletedResult);
    }

    private static ScanWorkflowController CreateWorkflow(IFileDiscoveryService discovery) =>
        new(new ScanSessionService(discovery, new EqualAnalysis()));

    private static DiscoveredFile File(string path, long length, ulong id) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id, 0), FileAttributes.Normal);

    private sealed class ImmediateDiscovery(IReadOnlyList<DiscoveredFile> files) : IFileDiscoveryService
    {
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryResult(files, [], false));
    }

    private sealed class FirstRunBlockingDiscovery : IFileDiscoveryService
    {
        private int calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new DiscoveryResult([], [], true);
                }
            }

            return new DiscoveryResult([], [], false);
        }
    }

    private sealed class AlwaysBlockingDiscovery : IFileDiscoveryService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DiscoveryResult([], [], true);
            }

            return new DiscoveryResult([], [], false);
        }
    }

    private sealed class ThrowingDiscovery : IFileDiscoveryService
    {
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) => throw new IOException("deterministic failure");
    }

    private sealed class RetainingDiscovery : IFileDiscoveryService
    {
        private int calls;
        public IProgress<DiscoveryProgress>? FirstProgress { get; private set; }

        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                FirstProgress = progress;
            }

            return Task.FromResult(new DiscoveryResult([], [], false));
        }
    }

    private sealed class EqualAnalysis : IContentAnalysisService
    {
        public Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default) => Task.FromResult(ContentHashOutcome.Success(new ContentDigest([1])));
        public Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default) => Task.FromResult(ContentComparisonOutcome.Equal());
        public Task<ContentValidationOutcome> ValidateAsync(DiscoveredFile file, CancellationToken cancellationToken = default) => Task.FromResult(ContentValidationOutcome.Valid());
    }
}
