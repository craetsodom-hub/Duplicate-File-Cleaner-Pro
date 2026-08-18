using DuplicateFileCleanerPro.Core.SimilarRemoval;
using DuplicateFileCleanerPro.Infrastructure.Windows.Cleanup;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.SimilarRemoval;

/// <summary>Fail-closed identity/snapshot validation for explicit Similar Photos removal intent.</summary>
public sealed class WindowsSimilarPhotoRemovalPlatform : ISimilarPhotoRemovalPlatform
{
    private readonly IWindowsRecycleBin recycleBin;
    private readonly ISimilarPhotoRemovalExecutionObserver? observer;

    public WindowsSimilarPhotoRemovalPlatform() : this(new WindowsShellRecycleBin()) { }

    internal WindowsSimilarPhotoRemovalPlatform(
        IWindowsRecycleBin recycleBin,
        ISimilarPhotoRemovalExecutionObserver? observer = null)
    {
        this.recycleBin = recycleBin;
        this.observer = observer;
    }

    public Task<SimilarPhotoRemovalValidation> ValidateAsync(
        SimilarPhotoRemovalPlanMember member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Validate(member, requireSingleLink: true));
    }

    public async Task<SimilarPhotoRecycleAttempt> RevalidateAndRecycleAsync(
        SimilarPhotoRemovalPlanMember candidate,
        IReadOnlyList<SimilarPhotoRemovalPlanMember> survivors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(survivors);
        cancellationToken.ThrowIfCancellationRequested();
        if (survivors.Count == 0) return new(SimilarPhotoRecycleAttemptStatus.SurvivorUnavailable);

        SimilarPhotoRemovalValidation candidateValidation = Validate(candidate, requireSingleLink: true);
        if (!candidateValidation.IsValid) return CandidateFailure(candidateValidation.Status);

        observer?.BeforeFinalRecycleValidation(candidate, survivors);
        var guards = new List<FileStream>();
        try
        {
            foreach (SimilarPhotoRemovalPlanMember survivor in survivors)
            {
                (FileStream? guard, SimilarPhotoRemovalValidation validation) = TryAcquireSurvivorGuard(survivor);
                if (guard is not null && validation.IsValid) guards.Add(guard);
            }

            if (guards.Count == 0) return new(SimilarPhotoRecycleAttemptStatus.SurvivorUnavailable);
            candidateValidation = Validate(candidate, requireSingleLink: true);
            if (!candidateValidation.IsValid) return CandidateFailure(candidateValidation.Status);
            cancellationToken.ThrowIfCancellationRequested();
            WindowsRecycleBinResult result = await recycleBin.RecycleAsync(candidate.ExpectedFile.NormalizedPath, cancellationToken).ConfigureAwait(false);
            return result.Succeeded
                ? new(SimilarPhotoRecycleAttemptStatus.Recycled)
                : new(SimilarPhotoRecycleAttemptStatus.RecycleBinFailed, result.NativeErrorCode);
        }
        finally
        {
            foreach (FileStream guard in guards) await guard.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static (FileStream? Guard, SimilarPhotoRemovalValidation Validation) TryAcquireSurvivorGuard(
        SimilarPhotoRemovalPlanMember survivor)
    {
        try
        {
            var guard = new FileStream(
                survivor.ExpectedFile.NormalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            SimilarPhotoRemovalValidation validation = ValidateHandle(guard, survivor, requireSingleLink: true);
            if (validation.IsValid) return (guard, validation);
            guard.Dispose();
            return (null, validation);
        }
        catch (FileNotFoundException) { return (null, new(SimilarPhotoRemovalValidationStatus.Missing)); }
        catch (DirectoryNotFoundException) { return (null, new(SimilarPhotoRemovalValidationStatus.Missing)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, new(SimilarPhotoRemovalValidationStatus.Unavailable));
        }
    }

    private static SimilarPhotoRemovalValidation Validate(SimilarPhotoRemovalPlanMember member, bool requireSingleLink)
    {
        string path = member.ExpectedFile.NormalizedPath;
        if (!IsEligibleLocalPath(path)) return new(SimilarPhotoRemovalValidationStatus.PolicyRejected);
        try
        {
            if (!WindowsFileInspector.TryInspect(path, out WindowsFileInspector.FileSnapshot? current) || current is null)
            {
                return new(File.Exists(path) ? SimilarPhotoRemovalValidationStatus.Unavailable : SimilarPhotoRemovalValidationStatus.Missing);
            }

            return ValidateSnapshot(current, member, requireSingleLink);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(SimilarPhotoRemovalValidationStatus.Unavailable);
        }
    }

    private static SimilarPhotoRemovalValidation ValidateHandle(
        FileStream stream,
        SimilarPhotoRemovalPlanMember member,
        bool requireSingleLink)
    {
        try
        {
            return WindowsFileInspector.TryInspect(stream.SafeFileHandle, out WindowsFileInspector.FileSnapshot? current) && current is not null
                ? ValidateSnapshot(current, member, requireSingleLink)
                : new(SimilarPhotoRemovalValidationStatus.Unavailable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(SimilarPhotoRemovalValidationStatus.Unavailable);
        }
    }

    private static SimilarPhotoRemovalValidation ValidateSnapshot(
        WindowsFileInspector.FileSnapshot current,
        SimilarPhotoRemovalPlanMember member,
        bool requireSingleLink)
    {
        if (!HasSafeRegularFileAttributes(current)) return new(SimilarPhotoRemovalValidationStatus.PolicyRejected);
        if (requireSingleLink && current.NumberOfLinks != 1) return new(SimilarPhotoRemovalValidationStatus.AmbiguousHardLinks);
        if (current.Identity != member.ExpectedFile.PhysicalIdentity) return new(SimilarPhotoRemovalValidationStatus.IdentityMismatch);
        return current.Length == member.ExpectedFile.Length
            && current.LastWriteTimeUtc == member.ExpectedFile.LastWriteTimeUtc
            && current.ChangeTimeUtc == member.ExpectedFile.ChangeTimeUtc
            ? SimilarPhotoRemovalValidation.Valid()
            : new(SimilarPhotoRemovalValidationStatus.Changed);
    }

    private static bool HasSafeRegularFileAttributes(WindowsFileInspector.FileSnapshot snapshot)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        FileAttributes rejected = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device
            | FileAttributes.Offline | FileAttributes.System | FileAttributes.Hidden | FileAttributes.Encrypted
            | recallOnOpen | recallOnDataAccess;
        return !snapshot.HasAdditionalNamedStream && (snapshot.Attributes & rejected) == 0;
    }

    private static bool IsEligibleLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SimilarPhotoRecycleAttempt CandidateFailure(SimilarPhotoRemovalValidationStatus status) => new(status switch
    {
        SimilarPhotoRemovalValidationStatus.Missing => SimilarPhotoRecycleAttemptStatus.CandidateMissing,
        SimilarPhotoRemovalValidationStatus.IdentityMismatch => SimilarPhotoRecycleAttemptStatus.CandidateIdentityMismatch,
        SimilarPhotoRemovalValidationStatus.Changed => SimilarPhotoRecycleAttemptStatus.CandidateChanged,
        SimilarPhotoRemovalValidationStatus.PolicyRejected => SimilarPhotoRecycleAttemptStatus.CandidatePolicyRejected,
        SimilarPhotoRemovalValidationStatus.AmbiguousHardLinks => SimilarPhotoRecycleAttemptStatus.CandidateAmbiguousHardLinks,
        _ => SimilarPhotoRecycleAttemptStatus.CandidateUnavailable,
    });
}

internal interface ISimilarPhotoRemovalExecutionObserver
{
    void BeforeFinalRecycleValidation(
        SimilarPhotoRemovalPlanMember candidate,
        IReadOnlyList<SimilarPhotoRemovalPlanMember> survivors);
}
