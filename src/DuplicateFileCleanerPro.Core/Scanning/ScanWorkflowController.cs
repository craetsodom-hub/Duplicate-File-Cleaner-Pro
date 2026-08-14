using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Scanning;

/// <summary>Owns one UI-facing scan workflow without depending on any UI framework.</summary>
public sealed class ScanWorkflowController(ScanSessionService session, SafetyOperationCoordinator? operationCoordinator = null) : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? activeCancellation;
    private long generation;
    private bool disposed;

    public ScanSessionState State { get; private set; } = ScanSessionState.Idle;
    public CompletedScanResult? CompletedResult { get; private set; }
    public Exception? Failure { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (sync)
            {
                return activeCancellation is not null;
            }
        }
    }

    public async Task<ScanSessionResult> StartAsync(
        IEnumerable<ScanRoot> roots,
        DiscoveryPolicy policy,
        IProgress<ScanSessionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(policy);
        using IDisposable? operationLease = operationCoordinator?.Acquire(SafetyOperationKind.Scan);
        CancellationTokenSource ownedCancellation;
        long runGeneration;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeCancellation is not null)
            {
                throw new InvalidOperationException("A scan workflow is already running.");
            }

            ownedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeCancellation = ownedCancellation;
            runGeneration = ++generation;
            State = ScanSessionState.Preparing;
            CompletedResult = null;
            Failure = null;
        }

        var guardedProgress = new GuardedProgress(this, runGeneration, progress);
        ScanSessionResult result = await session.RunAsync(roots, policy, guardedProgress, ownedCancellation.Token).ConfigureAwait(false);
        lock (sync)
        {
            if (!disposed && generation == runGeneration)
            {
                State = result.State;
                CompletedResult = result.CompletedResult;
                Failure = result.Failure;
                activeCancellation = null;
            }
        }

        ownedCancellation.Dispose();
        return result;
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            cancellation = activeCancellation;
        }

        cancellation?.Cancel();
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            cancellation = activeCancellation;
            activeCancellation = null;
        }

        cancellation?.Cancel();
        session.Dispose();
    }

    private sealed class GuardedProgress(ScanWorkflowController owner, long runGeneration, IProgress<ScanSessionProgress>? target) : IProgress<ScanSessionProgress>
    {
        public void Report(ScanSessionProgress value)
        {
            lock (owner.sync)
            {
                if (owner.disposed || owner.generation != runGeneration)
                {
                    return;
                }

                owner.State = value.State;
            }

            target?.Report(value);
        }
    }
}
