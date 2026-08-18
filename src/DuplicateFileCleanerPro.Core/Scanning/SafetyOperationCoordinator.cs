namespace DuplicateFileCleanerPro.Core.Scanning;

public enum SafetyOperationKind
{
    Scan,
    Cleanup,
    SimilarPhotoRemoval,
}

/// <summary>Small session-scoped owner that prevents scan and cleanup lifecycles from overlapping.</summary>
public sealed class SafetyOperationCoordinator
{
    private readonly object sync = new();
    private SafetyOperationKind? activeOperation;

    public SafetyOperationKind? ActiveOperation
    {
        get
        {
            lock (sync) return activeOperation;
        }
    }

    public IDisposable Acquire(SafetyOperationKind operation)
    {
        lock (sync)
        {
            if (activeOperation is not null)
            {
                throw new InvalidOperationException($"A safety operation is already active: {activeOperation}.");
            }

            activeOperation = operation;
            return new Lease(this, operation);
        }
    }

    private void Release(SafetyOperationKind operation)
    {
        lock (sync)
        {
            if (activeOperation == operation) activeOperation = null;
        }
    }

    private sealed class Lease(SafetyOperationCoordinator owner, SafetyOperationKind operation) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(operation);
        }
    }
}
