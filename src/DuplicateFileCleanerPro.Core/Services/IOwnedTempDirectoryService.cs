namespace DuplicateFileCleanerPro.Core.Services;

public interface IOwnedTempDirectoryService
{
    ValueTask<string> CreateSessionDirectoryAsync(string purpose, CancellationToken cancellationToken = default);

    ValueTask CleanupStaleSessionsAsync(CancellationToken cancellationToken = default);
}
