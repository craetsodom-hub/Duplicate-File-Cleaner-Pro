using System.ComponentModel;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

public sealed class WindowsFileDiscoveryService : IFileDiscoveryService
{
    public async Task<DiscoveryResult> DiscoverAsync(
        IEnumerable<ScanRoot> roots,
        DiscoveryPolicy policy,
        IProgress<DiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(policy);

        await Task.Yield();
        List<DiscoveredFile> files = [];
        List<SkippedDiscoveryItem> skipped = [];
        List<string> excludedFolders = NormalizeExcludedFolders(policy.ExcludedFolders);
        Stack<string> directories = new();
        foreach (ScanRoot root in roots.Reverse())
        {
            if (IsExcludedPath(root.NormalizedPath, excludedFolders))
            {
                skipped.Add(new SkippedDiscoveryItem(root.NormalizedPath, DiscoverySkipReason.FolderExcluded));
            }
            else if (IsLocalDirectory(root.NormalizedPath))
            {
                directories.Push(root.NormalizedPath);
            }
            else
            {
                skipped.Add(new SkippedDiscoveryItem(root.NormalizedPath, DiscoverySkipReason.UnsupportedLocation));
            }
        }
        int inspectedEntries = 0;

        while (directories.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Snapshot(files, skipped, wasCancelled: true);
            }

            string directory = directories.Pop();
            if (IsExcludedPath(directory, excludedFolders))
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.FolderExcluded));
                continue;
            }

            if (!WindowsFileInspector.TryInspectDirectory(directory, out WindowsFileInspector.FileSnapshot? directoryBefore)
                || directoryBefore is null)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.IdentityUnavailable));
                continue;
            }

            if ((directoryBefore.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.ReparsePoint));
                continue;
            }

            if ((directoryBefore.Attributes & FileAttributes.Directory) == 0)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.UnstableOrDisappeared));
                continue;
            }

            DiscoverySkipReason? directoryPolicyReason = GetPolicySkipReason(directoryBefore.Attributes, policy);
            if (directoryPolicyReason is not null)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, directoryPolicyReason.Value));
                continue;
            }

            ReportProgress(progress, new DiscoveryProgress(directory, files.Count, skipped.Count));
            int fileCountBefore = files.Count;
            int skipCountBefore = skipped.Count;
            int pendingDirectoryCountBefore = directories.Count;
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false,
                });
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.Inaccessible));
                continue;
            }
            catch (IOException)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.UnstableOrDisappeared));
                continue;
            }

            try
            {
                foreach (string entry in entries)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Snapshot(files, skipped, wasCancelled: true);
                    }

                    if (++inspectedEntries % 32 == 0)
                    {
                        ReportProgress(progress, new DiscoveryProgress(entry, files.Count, skipped.Count));
                        await Task.Yield();
                    }

                    InspectEntry(entry, policy, excludedFolders, directories, files, skipped);
                }
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.Inaccessible));
            }
            catch (IOException)
            {
                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.UnstableOrDisappeared));
            }

            if (!WindowsFileInspector.TryInspectDirectory(directory, out WindowsFileInspector.FileSnapshot? directoryAfter)
                || directoryAfter is null
                || directoryAfter.Identity != directoryBefore.Identity
                || (directoryAfter.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                files.RemoveRange(fileCountBefore, files.Count - fileCountBefore);
                skipped.RemoveRange(skipCountBefore, skipped.Count - skipCountBefore);
                while (directories.Count > pendingDirectoryCountBefore)
                {
                    directories.Pop();
                }

                skipped.Add(new SkippedDiscoveryItem(directory, DiscoverySkipReason.UnstableOrDisappeared));
            }
        }

        ReportProgress(progress, new DiscoveryProgress(string.Empty, files.Count, skipped.Count));
        return Snapshot(files, skipped, wasCancelled: false);
    }

    private static void InspectEntry(
        string entry,
        DiscoveryPolicy policy,
        IReadOnlyList<string> excludedFolders,
        Stack<string> directories,
        List<DiscoveredFile> files,
        List<SkippedDiscoveryItem> skipped)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(entry);
        }
        catch (UnauthorizedAccessException)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.Inaccessible));
            return;
        }
        catch (IOException)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.UnstableOrDisappeared));
            return;
        }

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.ReparsePoint));
            return;
        }

        if (isDirectory)
        {
            if (IsExcludedPath(entry, excludedFolders))
            {
                skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.FolderExcluded));
                return;
            }

            DiscoverySkipReason? policyReason = GetPolicySkipReason(attributes, policy);
            if (policyReason is not null)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, policyReason.Value));
                return;
            }

            if (!policy.IncludeSubfolders)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.SubfolderExcluded));
                return;
            }

            directories.Push(entry);
            return;
        }

        try
        {
            if (!WindowsFileInspector.TryInspect(entry, out WindowsFileInspector.FileSnapshot? snapshot) || snapshot is null)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.IdentityUnavailable));
                return;
            }

            if (snapshot.HasAdditionalNamedStream)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.AlternateDataStream));
                return;
            }

            if ((snapshot.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.ReparsePoint));
                return;
            }

            DiscoverySkipReason? policyReason = GetPolicySkipReason(snapshot.Attributes, policy);
            if (policyReason is not null)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, policyReason.Value));
                return;
            }

            ScanCriteriaRejection criteriaRejection = policy.Criteria.Evaluate(
                Path.GetExtension(entry),
                snapshot.Length,
                policy.ExcludedExtensions);
            if (criteriaRejection != ScanCriteriaRejection.None)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, criteriaRejection switch
                {
                    ScanCriteriaRejection.ExtensionExcluded => DiscoverySkipReason.ExtensionExcluded,
                    ScanCriteriaRejection.FileTypeExcluded => DiscoverySkipReason.FileTypeExcluded,
                    ScanCriteriaRejection.BelowMinimumSize => DiscoverySkipReason.BelowMinimumSize,
                    ScanCriteriaRejection.AboveMaximumSize => DiscoverySkipReason.AboveMaximumSize,
                    _ => throw new InvalidOperationException("Unexpected scan criteria result."),
                }));
                return;
            }

            files.Add(new DiscoveredFile(
                Path.GetFullPath(entry),
                Path.GetFileName(entry),
                Path.GetExtension(entry),
                snapshot.Length,
                snapshot.LastWriteTimeUtc,
                snapshot.ChangeTimeUtc,
                snapshot.Identity,
                snapshot.Attributes));
        }
        catch (UnauthorizedAccessException)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.Inaccessible));
        }
        catch (IOException)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.UnstableOrDisappeared));
        }
        catch (Win32Exception)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.IdentityUnavailable));
        }
        catch (InvalidOperationException)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.IdentityUnavailable));
        }
    }

    private static DiscoverySkipReason? GetPolicySkipReason(FileAttributes attributes, DiscoveryPolicy policy)
    {
        if ((attributes & FileAttributes.Offline) != 0)
        {
            return DiscoverySkipReason.Offline;
        }

        if (!policy.IncludeHiddenFiles && (attributes & FileAttributes.Hidden) != 0)
        {
            return DiscoverySkipReason.HiddenByPolicy;
        }

        if (!policy.IncludeSystemFiles && (attributes & FileAttributes.System) != 0)
        {
            return DiscoverySkipReason.SystemByPolicy;
        }

        return !policy.IncludeEncryptedFiles && (attributes & FileAttributes.Encrypted) != 0
            ? DiscoverySkipReason.Encrypted
            : null;
    }

    private static DiscoveryResult Snapshot(List<DiscoveredFile> files, List<SkippedDiscoveryItem> skipped, bool wasCancelled) =>
        new(Array.AsReadOnly(files.ToArray()), Array.AsReadOnly(skipped.ToArray()), wasCancelled);

    private static void ReportProgress(IProgress<DiscoveryProgress>? progress, DiscoveryProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception)
        {
            // Progress is observational and must not change discovery correctness.
        }
    }

    private static bool IsLocalDirectory(string path)
    {
        try
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(path))
            {
                return false;
            }

            return new DriveInfo(Path.GetPathRoot(path) ?? path).DriveType != DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static List<string> NormalizeExcludedFolders(IEnumerable<string> folders)
    {
        List<string> normalized = [];
        foreach (string folder in folders)
        {
            try
            {
                string fullPath = TrimTrailingSeparator(Path.GetFullPath(folder));
                if (!string.IsNullOrWhiteSpace(fullPath) && !normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(fullPath);
                }
            }
            catch (ArgumentException)
            {
                // Invalid persisted/user criteria are ignored; they never relax filesystem safety checks.
            }
            catch (NotSupportedException)
            {
            }
        }

        return normalized;
    }

    private static bool IsExcludedPath(string path, IReadOnlyList<string> excludedFolders)
    {
        try
        {
            string candidate = TrimTrailingSeparator(Path.GetFullPath(path));
            foreach (string excluded in excludedFolders)
            {
                if (candidate.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string relative = Path.GetRelativePath(excluded, candidate);
                if (relative != "."
                    && relative != ".."
                    && !Path.IsPathRooted(relative)
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return false;
    }

    private static string TrimTrailingSeparator(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
