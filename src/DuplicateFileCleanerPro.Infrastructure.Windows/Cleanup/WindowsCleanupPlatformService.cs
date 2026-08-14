using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.Cleanup;

/// <summary>
/// Performs fail-closed Windows snapshot/content checks and delegates only to the audited Recycle Bin boundary.
/// </summary>
public sealed class WindowsCleanupPlatformService : ICleanupPlatformService
{
    private readonly WindowsContentAnalysisService contentAnalysis = new();
    private readonly IWindowsRecycleBin recycleBin;
    private readonly ICleanupExecutionObserver? observer;

    public WindowsCleanupPlatformService()
        : this(new WindowsShellRecycleBin(), null)
    {
    }

    internal WindowsCleanupPlatformService(IWindowsRecycleBin recycleBin, ICleanupExecutionObserver? observer = null)
    {
        this.recycleBin = recycleBin;
        this.observer = observer;
    }

    public Task<CleanupFileValidation> ValidateAsync(CleanupPlanMember member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Validate(member, requireSingleLink: false));
    }

    public async Task<CleanupRecycleAttempt> RevalidateAndRecycleAsync(
        CleanupPlanMember candidate,
        CleanupPlanMember keeper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(keeper);
        cancellationToken.ThrowIfCancellationRequested();

        CleanupFileValidation candidateValidation = Validate(candidate, requireSingleLink: true);
        if (!candidateValidation.IsValid)
        {
            return CandidateFailure(candidateValidation.Status);
        }

        CleanupFileValidation keeperValidation = Validate(keeper, requireSingleLink: false);
        if (!keeperValidation.IsValid)
        {
            return KeeperFailure(keeperValidation.Status);
        }

        // Deterministic test seam occurs before the final pathname/identity checks. Production has no observer.
        observer?.BeforeFinalRecycleValidation(candidate, keeper);

        (FileStream? keeperGuard, CleanupFileValidation guardValidation) = TryAcquireKeeperGuard(keeper);
        await using (keeperGuard)
        {
            if (!guardValidation.IsValid)
            {
                return KeeperFailure(guardValidation.Status);
            }

            candidateValidation = Validate(candidate, requireSingleLink: true);
            if (!candidateValidation.IsValid)
            {
                return CandidateFailure(candidateValidation.Status);
            }

            if (candidate.ExpectedFile.Length != keeper.ExpectedFile.Length)
            {
                return new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.ContentMismatch);
            }

            ContentHashOutcome candidateHash = await contentAnalysis.HashAsync(candidate.ExpectedFile, cancellationToken).ConfigureAwait(false);
            ContentHashOutcome keeperHash = await contentAnalysis.HashAsync(keeper.ExpectedFile, cancellationToken).ConfigureAwait(false);
            if (!candidateHash.Succeeded || !keeperHash.Succeeded)
            {
                return new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.VerificationFailed);
            }

            if (!candidateHash.Digest!.ToArray().AsSpan().SequenceEqual(keeperHash.Digest!.ToArray()))
            {
                return new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.ContentMismatch);
            }

            ContentComparisonOutcome comparison = await contentAnalysis.CompareAsync(candidate.ExpectedFile, keeper.ExpectedFile, cancellationToken).ConfigureAwait(false);
            if (!comparison.Succeeded)
            {
                return new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.VerificationFailed);
            }

            if (!comparison.AreEqual!.Value)
            {
                return new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.ContentMismatch);
            }

            candidateValidation = Validate(candidate, requireSingleLink: true);
            if (!candidateValidation.IsValid)
            {
                return CandidateFailure(candidateValidation.Status);
            }

            // The guard denies writes and delete-sharing on the independently verified keeper until
            // the candidate's Recycle Bin operation has returned.
            keeperValidation = ValidateHandle(keeperGuard!, keeper, requireSingleLink: false);
            if (!keeperValidation.IsValid)
            {
                return KeeperFailure(keeperValidation.Status);
            }

            cancellationToken.ThrowIfCancellationRequested();
            WindowsRecycleBinResult recycleResult = await recycleBin
                .RecycleAsync(candidate.ExpectedFile.NormalizedPath, cancellationToken)
                .ConfigureAwait(false);
            return recycleResult.Succeeded
                ? new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.Recycled)
                : new CleanupRecycleAttempt(CleanupRecycleAttemptStatus.RecycleBinFailed, recycleResult.NativeErrorCode);
        }
    }

    private static (FileStream? Guard, CleanupFileValidation Validation) TryAcquireKeeperGuard(CleanupPlanMember keeper)
    {
        try
        {
            var guard = new FileStream(
                keeper.ExpectedFile.NormalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            CleanupFileValidation validation = ValidateHandle(guard, keeper, requireSingleLink: false);
            if (!validation.IsValid)
            {
                guard.Dispose();
                return (null, validation);
            }

            return (guard, validation);
        }
        catch (FileNotFoundException)
        {
            return (null, new CleanupFileValidation(CleanupFileValidationStatus.Missing));
        }
        catch (DirectoryNotFoundException)
        {
            return (null, new CleanupFileValidation(CleanupFileValidationStatus.Missing));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, new CleanupFileValidation(CleanupFileValidationStatus.Unavailable));
        }
    }

    private static CleanupFileValidation ValidateHandle(FileStream stream, CleanupPlanMember member, bool requireSingleLink)
    {
        try
        {
            if (!WindowsFileInspector.TryInspect(stream.SafeFileHandle, out WindowsFileInspector.FileSnapshot? current) || current is null)
            {
                return new CleanupFileValidation(CleanupFileValidationStatus.Unavailable);
            }

            if (!HasSafeRegularFileAttributes(current))
            {
                return new CleanupFileValidation(CleanupFileValidationStatus.PolicyRejected);
            }

            if (requireSingleLink && current.NumberOfLinks != 1)
            {
                return new CleanupFileValidation(CleanupFileValidationStatus.PolicyRejected);
            }

            if (current.Identity != member.ExpectedFile.PhysicalIdentity)
            {
                return new CleanupFileValidation(CleanupFileValidationStatus.IdentityMismatch);
            }

            return current.Length == member.ExpectedFile.Length
                && current.LastWriteTimeUtc == member.ExpectedFile.LastWriteTimeUtc
                && current.ChangeTimeUtc == member.ExpectedFile.ChangeTimeUtc
                ? CleanupFileValidation.Valid()
                : new CleanupFileValidation(CleanupFileValidationStatus.Changed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CleanupFileValidation(CleanupFileValidationStatus.Unavailable);
        }
    }

    private static CleanupFileValidation Validate(CleanupPlanMember member, bool requireSingleLink)
    {
        string path = member.ExpectedFile.NormalizedPath;
        if (!IsEligibleLocalPath(path))
        {
            return new CleanupFileValidation(CleanupFileValidationStatus.PolicyRejected);
        }

        try
        {
            if (!WindowsFileInspector.TryInspect(path, out WindowsFileInspector.FileSnapshot? current) || current is null)
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        return new CleanupFileValidation(CleanupFileValidationStatus.PolicyRejected);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // The missing/unavailable distinction below is diagnostic only; both fail closed.
                }

                return new CleanupFileValidation(File.Exists(path)
                    ? CleanupFileValidationStatus.Unavailable
                    : CleanupFileValidationStatus.Missing);
            }

            if (!HasSafeRegularFileAttributes(current))
            {
                return new CleanupFileValidation(CleanupFileValidationStatus.PolicyRejected);
            }

            if (requireSingleLink && current.NumberOfLinks != 1)
            {
                return new CleanupFileValidation(CleanupFileValidationStatus.PolicyRejected);
            }

            if (current.Identity != member.ExpectedFile.PhysicalIdentity)
            {
                return new CleanupFileValidation(CleanupFileValidationStatus.IdentityMismatch);
            }

            return current.Length == member.ExpectedFile.Length
                && current.LastWriteTimeUtc == member.ExpectedFile.LastWriteTimeUtc
                && current.ChangeTimeUtc == member.ExpectedFile.ChangeTimeUtc
                ? CleanupFileValidation.Valid()
                : new CleanupFileValidation(CleanupFileValidationStatus.Changed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new CleanupFileValidation(CleanupFileValidationStatus.Unavailable);
        }
    }

    private static bool HasSafeRegularFileAttributes(WindowsFileInspector.FileSnapshot snapshot)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        FileAttributes rejected = FileAttributes.Directory
            | FileAttributes.ReparsePoint
            | FileAttributes.Device
            | FileAttributes.Offline
            | FileAttributes.System
            | FileAttributes.Hidden
            | FileAttributes.Encrypted
            | recallOnOpen
            | recallOnDataAccess;
        return !snapshot.HasAdditionalNamedStream && (snapshot.Attributes & rejected) == 0;
    }

    private static bool IsEligibleLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            DriveType driveType = new DriveInfo(root).DriveType;
            return driveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static CleanupRecycleAttempt CandidateFailure(CleanupFileValidationStatus status) => new(status switch
    {
        CleanupFileValidationStatus.Missing => CleanupRecycleAttemptStatus.CandidateMissing,
        CleanupFileValidationStatus.IdentityMismatch => CleanupRecycleAttemptStatus.CandidateIdentityMismatch,
        CleanupFileValidationStatus.Changed => CleanupRecycleAttemptStatus.CandidateChanged,
        CleanupFileValidationStatus.PolicyRejected => CleanupRecycleAttemptStatus.CandidatePolicyRejected,
        _ => CleanupRecycleAttemptStatus.CandidateUnavailable,
    });

    private static CleanupRecycleAttempt KeeperFailure(CleanupFileValidationStatus status) => new(status switch
    {
        CleanupFileValidationStatus.Missing => CleanupRecycleAttemptStatus.KeeperMissing,
        CleanupFileValidationStatus.IdentityMismatch => CleanupRecycleAttemptStatus.KeeperIdentityMismatch,
        CleanupFileValidationStatus.Changed => CleanupRecycleAttemptStatus.KeeperChanged,
        CleanupFileValidationStatus.PolicyRejected => CleanupRecycleAttemptStatus.KeeperPolicyRejected,
        _ => CleanupRecycleAttemptStatus.KeeperUnavailable,
    });
}

internal interface ICleanupExecutionObserver
{
    void BeforeFinalRecycleValidation(CleanupPlanMember candidate, CleanupPlanMember keeper);
}
