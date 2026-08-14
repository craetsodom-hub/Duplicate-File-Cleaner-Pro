using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

public sealed class WindowsScanRootNormalizer : IScanRootNormalizer
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public RootNormalizationResult Normalize(IEnumerable<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);

        List<SkippedDiscoveryItem> rejected = [];
        List<string> candidates = [];

        foreach (string selectedPath in selectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!TryNormalize(selectedPath, out string? normalizedPath, out DiscoverySkipReason reason))
            {
                rejected.Add(new SkippedDiscoveryItem(selectedPath, reason));
                continue;
            }

            if (!candidates.Contains(normalizedPath, PathComparer))
            {
                candidates.Add(normalizedPath!);
            }
        }

        candidates.Sort(PathComparer);
        List<string> roots = [];
        foreach (string candidate in candidates)
        {
            if (roots.Any(existing => IsSameOrDescendant(candidate, existing)))
            {
                continue;
            }

            roots.RemoveAll(existing => IsSameOrDescendant(existing, candidate));
            roots.Add(candidate);
        }

        roots.Sort(PathComparer);
        return new RootNormalizationResult(
            Array.AsReadOnly(roots.Select(path => new ScanRoot(path)).ToArray()),
            Array.AsReadOnly(rejected.ToArray()));
    }

    private static bool TryNormalize(string selectedPath, out string? normalizedPath, out DiscoverySkipReason reason)
    {
        normalizedPath = null;
        reason = DiscoverySkipReason.InvalidRoot;

        try
        {
            string fullPath = Path.GetFullPath(selectedPath);
            if (fullPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
            {
                reason = DiscoverySkipReason.NetworkLocation;
                return false;
            }

            if (fullPath.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = fullPath[4..];
            }

            if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                reason = DiscoverySkipReason.NetworkLocation;
                return false;
            }

            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            DriveInfo drive = new(root);
            if (drive.DriveType == DriveType.Network)
            {
                reason = DiscoverySkipReason.NetworkLocation;
                return false;
            }

            if (drive.DriveType is DriveType.Unknown or DriveType.NoRootDirectory || !Directory.Exists(fullPath))
            {
                reason = DiscoverySkipReason.UnsupportedLocation;
                return false;
            }

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = DiscoverySkipReason.ReparsePoint;
                return false;
            }

            normalizedPath = TrimTrailingSeparator(fullPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            reason = DiscoverySkipReason.Inaccessible;
            return false;
        }
        catch (IOException)
        {
            reason = DiscoverySkipReason.UnsupportedLocation;
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (PathComparer.Equals(candidate, parent))
        {
            return true;
        }

        string relative = Path.GetRelativePath(parent, candidate);
        return relative != "." && !Path.IsPathRooted(relative) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != "..";
    }

    private static string TrimTrailingSeparator(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return PathComparer.Equals(path, root) ? path : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
