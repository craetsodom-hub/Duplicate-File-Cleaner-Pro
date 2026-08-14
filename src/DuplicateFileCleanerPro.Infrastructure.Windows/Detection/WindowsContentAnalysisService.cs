using System.Buffers;
using System.Security.Cryptography;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.Detection;

/// <summary>Windows read-only, bounded-memory SHA-256 and byte comparison implementation.</summary>
public sealed class WindowsContentAnalysisService : IContentAnalysisService
{
    private const int BufferSize = 64 * 1024;

    public async Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default)
    {
        try
        {
            await using FileStream stream = OpenReadOnly(file.NormalizedPath);
            if (!MatchesDiscoverySnapshot(stream, file))
            {
                return ContentHashOutcome.Failure(ContentAnalysisFailureReason.ChangedDuringAnalysis);
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, bytesRead);
                }

                if (!MatchesDiscoverySnapshot(stream, file))
                {
                    return ContentHashOutcome.Failure(ContentAnalysisFailureReason.ChangedDuringAnalysis);
                }

                return ContentHashOutcome.Success(new ContentDigest(hash.GetHashAndReset()));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return ContentHashOutcome.Failure(ContentAnalysisFailureReason.ReadFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return ContentHashOutcome.Failure(ContentAnalysisFailureReason.Unavailable);
        }
        catch (CryptographicException)
        {
            return ContentHashOutcome.Failure(ContentAnalysisFailureReason.HashFailed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return ContentHashOutcome.Failure(ContentAnalysisFailureReason.Unavailable);
        }
    }

    public async Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default)
    {
        try
        {
            await using FileStream leftStream = OpenReadOnly(left.NormalizedPath);
            await using FileStream rightStream = OpenReadOnly(right.NormalizedPath);
            if (!MatchesDiscoverySnapshot(leftStream, left) || !MatchesDiscoverySnapshot(rightStream, right))
            {
                return ContentComparisonOutcome.Failure(ContentAnalysisFailureReason.ChangedDuringAnalysis);
            }

            byte[] leftBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            byte[] rightBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    int leftRead = await leftStream.ReadAsync(leftBuffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    int rightRead = await rightStream.ReadAsync(rightBuffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (leftRead != rightRead || !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                    {
                        return ContentComparisonOutcome.Different();
                    }

                    if (leftRead == 0)
                    {
                        break;
                    }
                }

                if (!MatchesDiscoverySnapshot(leftStream, left) || !MatchesDiscoverySnapshot(rightStream, right))
                {
                    return ContentComparisonOutcome.Failure(ContentAnalysisFailureReason.ChangedDuringAnalysis);
                }

                return ContentComparisonOutcome.Equal();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(leftBuffer);
                ArrayPool<byte>.Shared.Return(rightBuffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return ContentComparisonOutcome.Failure(ContentAnalysisFailureReason.ComparisonFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return ContentComparisonOutcome.Failure(ContentAnalysisFailureReason.Unavailable);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return ContentComparisonOutcome.Failure(ContentAnalysisFailureReason.ComparisonFailed);
        }
    }

    private static FileStream OpenReadOnly(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool MatchesDiscoverySnapshot(FileStream stream, DiscoveredFile file)
    {
        try
        {
            return WindowsFileInspector.TryInspect(stream.SafeFileHandle, out WindowsFileInspector.FileSnapshot? snapshot)
                && snapshot is { HasAdditionalNamedStream: false }
                && snapshot.Identity == file.PhysicalIdentity
                && snapshot.Length == file.Length
                && snapshot.LastWriteTimeUtc == file.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
