namespace DuplicateFileCleanerPro.FileSystem;

public interface IFileSystemScanner
{
    Task ScanAsync(CancellationToken cancellationToken);
}
