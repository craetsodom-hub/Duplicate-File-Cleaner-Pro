using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Scanning;

public sealed class ScanSessionService(IFileDiscoveryService discovery, IContentAnalysisService contentAnalysis) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ScanSessionResult> RunAsync(
        IEnumerable<ScanRoot> roots,
        DiscoveryPolicy policy,
        IProgress<ScanSessionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A scan session is already running.");
        }

        try
        {
            List<ScanRoot> rootSnapshot = roots.ToList();
            if (rootSnapshot.Count == 0)
            {
                throw new InvalidOperationException("At least one scan root is required.");
            }

            progress?.Report(new ScanSessionProgress(ScanSessionState.Preparing, string.Empty, 0, 0, 0, 0, 0, 0));
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ScanSessionProgress(ScanSessionState.Discovering, string.Empty, 0, 0, 0, 0, 0, 0));
            var discoveryProgress = new RelayProgress<DiscoveryProgress>(update =>
                progress?.Report(new ScanSessionProgress(ScanSessionState.Discovering, update.CurrentPath, update.FilesDiscovered, 0, 0, 0, 0, update.SkippedItemCount)));
            DiscoveryResult discoveryResult = await Task.Run(
                () => discovery.DiscoverAsync(rootSnapshot, policy, discoveryProgress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
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
            progress?.Report(new ScanSessionProgress(ScanSessionState.Analyzing, string.Empty, discoveryResult.Files.Count, 0, 0, totalCandidateBytes, 0, discoveryResult.SkippedItems.Count));

            var detectionProgress = new RelayProgress<DuplicateDetectionProgress>(update =>
                progress?.Report(new ScanSessionProgress(ScanSessionState.Analyzing, update.CurrentPath, discoveryResult.Files.Count, update.CandidatesProcessed, update.BytesProcessed, update.TotalCandidateBytes, update.VerifiedGroupCount, discoveryResult.SkippedItems.Count + update.SkippedItemCount)));
            ExactDuplicateDetectionResult detectionResult = await Task.Run(
                () => ExactDuplicateDetector.DetectAsync(discoveryResult.Files, contentAnalysis, detectionProgress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            if (detectionResult.WasCancelled || cancellationToken.IsCancellationRequested)
            {
                return ScanSessionResult.Cancelled();
            }

            progress?.Report(new ScanSessionProgress(ScanSessionState.Completed, string.Empty, discoveryResult.Files.Count, candidates.Count, totalCandidateBytes, totalCandidateBytes, detectionResult.Groups.Count, discoveryResult.SkippedItems.Count + detectionResult.SkippedItems.Count));
            return ScanSessionResult.Completed(new CompletedScanResult(discoveryResult, detectionResult));
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
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed class RelayProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
