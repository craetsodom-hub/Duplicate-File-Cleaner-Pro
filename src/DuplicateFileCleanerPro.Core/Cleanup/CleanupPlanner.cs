using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Cleanup;

public static class CleanupPlanner
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static CleanupPlanningResult CreatePlan(CleanupSelectionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var issues = new List<CleanupPlanningIssue>();
        if (intent.VerifiedResult.Discovery.WasCancelled || intent.VerifiedResult.Detection.WasCancelled)
        {
            issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.CancelledScanSnapshot));
            return Failed(issues);
        }

        var identities = new HashSet<PhysicalFileIdentity>();
        var paths = new HashSet<string>(PathComparer);
        var membership = new Dictionary<PhysicalFileIdentity, int>();
        ILookup<PhysicalFileIdentity, DiscoveredFile> discoveredByIdentity = intent.VerifiedResult.Discovery.Files
            .ToLookup(file => file.PhysicalIdentity);
        long groupReclaimableTotal = 0;
        for (int groupIndex = 0; groupIndex < intent.VerifiedResult.Detection.Groups.Count; groupIndex++)
        {
            var group = intent.VerifiedResult.Detection.Groups[groupIndex];
            if (group.Files.Count < 2 || group.Files.Any(file => file.Length < 0) || group.Files.Select(file => file.Length).Distinct().Count() != 1)
            {
                issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.InvalidVerifiedGroup));
            }

            foreach (DiscoveredFile file in group.Files)
            {
                if (file.PhysicalIdentity == default
                    || !string.Equals(file.FileName, Path.GetFileName(file.NormalizedPath), StringComparison.Ordinal)
                    || !HasSafeSnapshotAttributes(file.Attributes)
                    || !discoveredByIdentity[file.PhysicalIdentity].Any(discovered => discovered == file))
                {
                    issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.InvalidVerifiedGroup, file.NormalizedPath));
                }

                if (!identities.Add(file.PhysicalIdentity))
                {
                    issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.DuplicatePhysicalIdentity, file.NormalizedPath));
                }

                if (!paths.Add(file.NormalizedPath))
                {
                    issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.DuplicatePath, file.NormalizedPath));
                }

                if (!IsSupportedLocalPath(file.NormalizedPath))
                {
                    issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.UnsupportedPath, file.NormalizedPath));
                }

                membership[file.PhysicalIdentity] = groupIndex;
            }

            try
            {
                if (group.Files.Count >= 2)
                {
                    long expected = checked((group.Files.Count - 1L) * group.Files[0].Length);
                    if (expected != group.ReclaimableBytes)
                    {
                        issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.SnapshotArithmeticMismatch));
                    }

                    groupReclaimableTotal = checked(groupReclaimableTotal + group.ReclaimableBytes);
                }
            }
            catch (OverflowException)
            {
                issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.SnapshotArithmeticMismatch));
            }
        }

        if (groupReclaimableTotal != intent.VerifiedResult.Detection.TotalReclaimableBytes)
        {
            issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.SnapshotArithmeticMismatch));
        }

        foreach (PhysicalFileIdentity selected in intent.SelectedPhysicalMembers)
        {
            if (!membership.ContainsKey(selected))
            {
                issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.UnknownSelectedIdentity));
            }
        }

        if (issues.Count > 0)
        {
            return Failed(issues);
        }

        var selectedSet = intent.SelectedPhysicalMembers.ToHashSet();
        var planGroups = new List<CleanupPlanGroup>();
        long selectedByteTotal = 0;
        for (int groupIndex = 0; groupIndex < intent.VerifiedResult.Detection.Groups.Count; groupIndex++)
        {
            var group = intent.VerifiedResult.Detection.Groups[groupIndex];
            CleanupPlanMember[] members = group.Files
                .OrderBy(file => file.NormalizedPath, PathComparer)
                .Select(file => new CleanupPlanMember(file))
                .ToArray();
            CleanupPlanMember[] candidates = members.Where(member => selectedSet.Contains(member.ExpectedFile.PhysicalIdentity)).ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            CleanupPlanMember[] keepers = members.Where(member => !selectedSet.Contains(member.ExpectedFile.PhysicalIdentity)).ToArray();
            if (candidates.Length >= members.Length || keepers.Length == 0)
            {
                issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.AllMembersSelected));
                continue;
            }

            try
            {
                selectedByteTotal = candidates.Aggregate(selectedByteTotal, (total, candidate) => checked(total + candidate.ExpectedFile.Length));
            }
            catch (OverflowException)
            {
                issues.Add(new CleanupPlanningIssue(CleanupPlanningIssueReason.SnapshotArithmeticMismatch));
                continue;
            }

            planGroups.Add(new CleanupPlanGroup(
                groupIndex,
                Array.AsReadOnly(members),
                Array.AsReadOnly(candidates),
                Array.AsReadOnly(keepers)));
        }

        return issues.Count == 0
            ? new CleanupPlanningResult(new CleanupPlan(intent.VerifiedResult, Array.AsReadOnly(planGroups.ToArray())), [])
            : Failed(issues);
    }

    private static CleanupPlanningResult Failed(List<CleanupPlanningIssue> issues) =>
        new(null, Array.AsReadOnly(issues.ToArray()));

    private static bool IsSupportedLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return PathComparer.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar));
    }

    private static bool HasSafeSnapshotAttributes(FileAttributes attributes)
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
        return (attributes & rejected) == 0;
    }
}
