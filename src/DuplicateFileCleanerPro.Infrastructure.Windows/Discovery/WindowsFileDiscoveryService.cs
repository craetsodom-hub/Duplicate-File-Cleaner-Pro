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
                return new DiscoveryResult(files, skipped, true);
            }

            string directory = directories.Pop();
            progress?.Report(new DiscoveryProgress(directory, files.Count, skipped.Count));
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
                        return new DiscoveryResult(files, skipped, true);
                    }

                    if (++inspectedEntries % 32 == 0)
                    {
                        progress?.Report(new DiscoveryProgress(entry, files.Count, skipped.Count));
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
        }

        progress?.Report(new DiscoveryProgress(string.Empty, files.Count, skipped.Count));
        return new DiscoveryResult(files, skipped, false);
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
            directories.Push(entry);
            return;
        }

        if ((attributes & FileAttributes.Offline) != 0)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.Offline));
            return;
        }

        if (!policy.IncludeHiddenFiles && (attributes & FileAttributes.Hidden) != 0)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.HiddenByPolicy));
            return;
        }

        if (!policy.IncludeSystemFiles && (attributes & FileAttributes.System) != 0)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.SystemByPolicy));
            return;
        }

        if (!policy.IncludeEncryptedFiles && (attributes & FileAttributes.Encrypted) != 0)
        {
            skipped.Add(new SkippedDiscoveryItem(entry, DiscoverySkipReason.Encrypted));
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

            files.Add(new DiscoveredFile(
                Path.GetFullPath(entry),
                Path.GetFileName(entry),
                Path.GetExtension(entry),
                snapshot.Length,
                snapshot.LastWriteTimeUtc,
                snapshot.Identity,
                attributes));
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
