namespace DuplicateFileCleanerPro.Infrastructure.Windows.Detection;

internal enum ContentAnalysisOperation
{
    Hashing,
    Comparing,
}

/// <summary>Internal deterministic race-test seam; production composition never supplies one.</summary>
internal interface IContentAnalysisObserver
{
    void OnChunkRead(ContentAnalysisOperation operation, string path, long bytesRead);
}
