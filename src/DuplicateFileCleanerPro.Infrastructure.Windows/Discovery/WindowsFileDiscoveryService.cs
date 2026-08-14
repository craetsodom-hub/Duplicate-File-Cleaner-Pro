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
        Stack<string> directories = new();
        foreach (ScanRoot root in roots.Reverse())
        {
            if (IsLocalDirectory(root.NormalizedPath))
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

                    InspectEntry(entry, policy, directories, files, skipped);
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
            DiscoverySkipReason? policyReason = GetPolicySkipReason(attributes, policy);
            if (policyReason is not null)
            {
                skipped.Add(new SkippedDiscoveryItem(entry, policyReason.Value));
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
}
