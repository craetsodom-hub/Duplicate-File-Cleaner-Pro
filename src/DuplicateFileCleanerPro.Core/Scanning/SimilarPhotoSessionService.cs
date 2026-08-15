using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.Scanning;

/// <summary>Runs safe discovery followed by the independent, local Similar Photos engine.</summary>
public sealed class SimilarPhotoSessionService(IFileDiscoveryService discovery, ISimilarPhotoDecoder decoder) : IDisposable
{
    private readonly object lifecycle = new();
    private bool active;
    private bool disposed;

    public async Task<SimilarPhotoSessionResult> RunAsync(
        IEnumerable<ScanRoot> roots,
        DiscoveryPolicy policy,
        SimilarPhotoSensitivity sensitivity,
        IProgress<SimilarPhotoSessionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active) throw new InvalidOperationException("A Similar Photos session is already running.");
            active = true;
        }

        try
        {
            ScanRoot[] snapshot = roots.ToArray();
            if (snapshot.Length == 0) throw new InvalidOperationException("At least one scan root is required.");
            DiscoveryPolicy imagePolicy = new(
                IncludeHiddenFiles: policy.IncludeHiddenFiles,
                IncludeSystemFiles: policy.IncludeSystemFiles,
                IncludeEncryptedFiles: policy.IncludeEncryptedFiles,
                IncludeSubfolders: policy.IncludeSubfolders,
                Criteria: new ScanCriteria(ScanFileType.Images, minimumSizeBytes: policy.Criteria.MinimumSizeBytes, maximumSizeBytes: policy.Criteria.MaximumSizeBytes),
                ExcludedFolders: policy.ExcludedFolders,
                ExcludedExtensions: policy.ExcludedExtensions);
            DiscoveryResult discoveryResult = await discovery.DiscoverAsync(snapshot, imagePolicy, new RelayProgress<DiscoveryProgress>(value =>
                Report(progress, new SimilarPhotoSessionProgress(SimilarPhotoProgressStage.FindingPhotos, value.CurrentPath, value.FilesDiscovered, null, 0, 0, 0, value.SkippedItemCount))), cancellationToken).ConfigureAwait(false);
            if (discoveryResult.WasCancelled || cancellationToken.IsCancellationRequested) return SimilarPhotoSessionResult.Cancelled();

            SimilarPhotoAnalysisResult analysis = await new SimilarPhotoEngine(decoder).AnalyzeAsync(
                discoveryResult.Files,
                sensitivity,
                new RelayProgress<SimilarPhotoProgress>(value => Report(progress, new SimilarPhotoSessionProgress(value.Stage, value.CurrentPath, value.CompletedItems, value.TotalItems, value.CandidatePairs, value.FinalComparisons, value.GroupCount, discoveryResult.SkippedItems.Count + value.SkippedItemCount))),
                cancellationToken).ConfigureAwait(false);
            if (analysis.WasCancelled || cancellationToken.IsCancellationRequested) return SimilarPhotoSessionResult.Cancelled();
            return SimilarPhotoSessionResult.Completed(new CompletedSimilarPhotoScanResult(discoveryResult, analysis, sensitivity, Array.AsReadOnly(snapshot.Select(root => root.NormalizedPath).ToArray())));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return SimilarPhotoSessionResult.Cancelled(); }
        catch (Exception exception) { return SimilarPhotoSessionResult.Failed(exception); }
        finally { lock (lifecycle) active = false; }
    }

    public void Dispose() { lock (lifecycle) disposed = true; }
    private static void Report(IProgress<SimilarPhotoSessionProgress>? progress, SimilarPhotoSessionProgress value) { try { progress?.Report(value); } catch (Exception) { } }
    private sealed class RelayProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}

public sealed record SimilarPhotoSessionProgress(SimilarPhotoProgressStage Stage, string CurrentPath, int CompletedItems, int? TotalItems, int CandidatePairs, int FinalComparisons, int GroupCount, int SkippedItemCount);
public sealed record CompletedSimilarPhotoScanResult(DiscoveryResult Discovery, SimilarPhotoAnalysisResult Analysis, SimilarPhotoSensitivity Sensitivity, IReadOnlyList<string> ScanRoots);
public sealed record SimilarPhotoSessionResult(ScanSessionState State, CompletedSimilarPhotoScanResult? CompletedResult, Exception? Failure)
{
    public static SimilarPhotoSessionResult Completed(CompletedSimilarPhotoScanResult value) => new(ScanSessionState.Completed, value, null);
    public static SimilarPhotoSessionResult Cancelled() => new(ScanSessionState.Cancelled, null, null);
    public static SimilarPhotoSessionResult Failed(Exception exception) => new(ScanSessionState.Failed, null, exception);
}
