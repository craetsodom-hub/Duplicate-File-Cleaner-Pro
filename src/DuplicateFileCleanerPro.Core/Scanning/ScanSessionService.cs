using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Scanning;

public sealed class ScanSessionService(IFileDiscoveryService discovery, IContentAnalysisService contentAnalysis) : IDisposable
{
    private readonly object _lifecycle = new();
    private bool _active;
    private bool _disposed;

    public async Task<ScanSessionResult> RunAsync(
        IEnumerable<ScanRoot> roots,
        DiscoveryPolicy policy,
        IProgress<ScanSessionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active)
            {
                throw new InvalidOperationException("A scan session is already running.");
            }

            _active = true;
        }

        try
        {
            List<ScanRoot> rootSnapshot = roots.ToList();
            if (rootSnapshot.Count == 0)
            {
                throw new InvalidOperationException("At least one scan root is required.");
            }

            ReportProgress(progress, new ScanSessionProgress(ScanSessionState.Preparing, string.Empty, 0, 0, 0, 0, 0, 0, false));
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(
                () => RunPipelineAsync(rootSnapshot, policy, progress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ScanSessionResult.Cancelled();
        }
        catch (Exception exception)
        {
            return ScanSessionResult.Failed(exception);
        }
        finally
        {
            lock (_lifecycle)
            {
                _active = false;
            }
        }
    }

    private async Task<ScanSessionResult> RunPipelineAsync(
        IReadOnlyList<ScanRoot> roots,
        DiscoveryPolicy policy,
        IProgress<ScanSessionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, new ScanSessionProgress(ScanSessionState.Discovering, string.Empty, 0, 0, 0, 0, 0, 0, false));
        var discoveryProgress = new RelayProgress<DiscoveryProgress>(update =>
            ReportProgress(progress, new ScanSessionProgress(ScanSessionState.Discovering, update.CurrentPath, update.FilesDiscovered, 0, 0, 0, 0, update.SkippedItemCount, false)));
        DiscoveryResult discovered = await discovery.DiscoverAsync(roots, policy, discoveryProgress, cancellationToken).ConfigureAwait(false);
        DiscoveryResult discoveryResult = new(
            Array.AsReadOnly(discovered.Files.ToArray()),
            Array.AsReadOnly(discovered.SkippedItems.ToArray()),
            discovered.WasCancelled);
        if (discoveryResult.WasCancelled || cancellationToken.IsCancellationRequested)
        {
            return ScanSessionResult.Cancelled();
        }

        List<DiscoveredFile> candidates = discoveryResult.Files
            .GroupBy(file => file.PhysicalIdentity)
            .Select(group => group.First())
            .GroupBy(file => file.Length)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();
        long totalCandidateBytes = candidates.Aggregate(0L, (total, file) => checked(total + file.Length));
        ReportProgress(progress, new ScanSessionProgress(ScanSessionState.Analyzing, string.Empty, discoveryResult.Files.Count, 0, 0, totalCandidateBytes, 0, discoveryResult.SkippedItems.Count, false));

        var detectionProgress = new RelayProgress<DuplicateDetectionProgress>(update =>
            ReportProgress(progress, new ScanSessionProgress(ScanSessionState.Analyzing, update.CurrentPath, discoveryResult.Files.Count, update.CandidatesProcessed, update.BytesProcessed, update.TotalCandidateBytes, update.VerifiedGroupCount, discoveryResult.SkippedItems.Count + update.SkippedItemCount, update.IsVerifying)));
        ExactDuplicateDetectionResult detectionResult = await ExactDuplicateDetector.DetectAsync(discoveryResult.Files, contentAnalysis, detectionProgress, cancellationToken).ConfigureAwait(false);
        if (detectionResult.WasCancelled || cancellationToken.IsCancellationRequested)
        {
            return ScanSessionResult.Cancelled();
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(progress, new ScanSessionProgress(ScanSessionState.Completed, string.Empty, discoveryResult.Files.Count, candidates.Count, totalCandidateBytes, totalCandidateBytes, detectionResult.Groups.Count, discoveryResult.SkippedItems.Count + detectionResult.SkippedItems.Count, false));
        return ScanSessionResult.Completed(new CompletedScanResult(
            discoveryResult,
            detectionResult,
            Array.AsReadOnly(roots.Select(root => root.NormalizedPath).ToArray())));
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
        }
    }

    private static void ReportProgress(IProgress<ScanSessionProgress>? progress, ScanSessionProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception)
        {
            // Progress is observational and must not change session correctness.
        }
    }

    private sealed class RelayProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
