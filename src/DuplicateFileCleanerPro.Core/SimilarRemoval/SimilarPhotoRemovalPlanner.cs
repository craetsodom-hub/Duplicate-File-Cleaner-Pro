using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.SimilarRemoval;

public static class SimilarPhotoRemovalPlanner
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static SimilarPhotoRemovalPlanningResult CreatePlan(SimilarPhotoRemovalIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var issues = new List<SimilarPhotoRemovalPlanningIssue>();
        if (intent.AnalyzedResult.Discovery.WasCancelled || intent.AnalyzedResult.Analysis.WasCancelled)
        {
            return Failed(new SimilarPhotoRemovalPlanningIssue(SimilarPhotoRemovalPlanningIssueReason.CancelledAnalysis));
        }

        if (intent.ExplicitlyMarkedForRemoval.Count == 0)
        {
            return Failed(new SimilarPhotoRemovalPlanningIssue(SimilarPhotoRemovalPlanningIssueReason.EmptyIntent));
        }

        if (intent.ExplicitlyMarkedForRemoval.Distinct().Count() != intent.ExplicitlyMarkedForRemoval.Count)
        {
            issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.DuplicateCandidateIntent));
        }

        var membership = new Dictionary<PhysicalFileIdentity, int>();
        var allPaths = new HashSet<string>(PathComparer);
        var planSource = new List<(int Index, SimilarPhotoGroup Group, SimilarPhotoRemovalPlanMember[] Members)>();
        for (int groupIndex = 0; groupIndex < intent.AnalyzedResult.Analysis.Groups.Count; groupIndex++)
        {
            SimilarPhotoGroup group = intent.AnalyzedResult.Analysis.Groups[groupIndex];
            if (group.Photos.Count < 2 || !group.Photos.Contains(group.Representative))
            {
                issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.InvalidGroup));
                continue;
            }

            var groupIdentities = new HashSet<PhysicalFileIdentity>();
            var members = new List<SimilarPhotoRemovalPlanMember>(group.Photos.Count);
            foreach (DiscoveredFile file in group.Photos)
            {
                if (file.PhysicalIdentity == default
                    || file.Length < 0
                    || !string.Equals(file.FileName, Path.GetFileName(file.NormalizedPath), StringComparison.Ordinal)
                    || !HasSafeSnapshotAttributes(file.Attributes))
                {
                    issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.InvalidGroup, file.NormalizedPath));
                }

                if (!groupIdentities.Add(file.PhysicalIdentity) || membership.ContainsKey(file.PhysicalIdentity))
                {
                    issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.DuplicatePhysicalIdentity, file.NormalizedPath));
                }
                else
                {
                    membership[file.PhysicalIdentity] = groupIndex;
                }

                if (!allPaths.Add(file.NormalizedPath)) issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.DuplicatePath, file.NormalizedPath));
                if (!IsSupportedLocalPath(file.NormalizedPath)) issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.UnsupportedPath, file.NormalizedPath));
                members.Add(new(file));
            }

            planSource.Add((groupIndex, group, members.OrderBy(member => member.ExpectedFile.NormalizedPath, PathComparer).ToArray()));
        }

        foreach (PhysicalFileIdentity candidate in intent.ExplicitlyMarkedForRemoval)
        {
            if (!membership.ContainsKey(candidate)) issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.UnknownCandidate));
        }

        if (issues.Count > 0) return Failed(issues);

        HashSet<PhysicalFileIdentity> selected = intent.ExplicitlyMarkedForRemoval.ToHashSet();
        var groups = new List<SimilarPhotoRemovalPlanGroup>();
        long selectedBytes = 0;
        foreach ((int groupIndex, SimilarPhotoGroup group, SimilarPhotoRemovalPlanMember[] members) in planSource)
        {
            SimilarPhotoRemovalPlanMember[] candidates = members.Where(member => selected.Contains(member.ExpectedFile.PhysicalIdentity)).ToArray();
            if (candidates.Length == 0) continue;
            SimilarPhotoRemovalPlanMember[] survivors = members.Where(member => !selected.Contains(member.ExpectedFile.PhysicalIdentity)).ToArray();
            if (survivors.Length == 0)
            {
                issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.AllIndependentMembersSelected));
                continue;
            }

            try
            {
                selectedBytes = candidates.Aggregate(selectedBytes, (total, member) => checked(total + member.ExpectedFile.Length));
            }
            catch (OverflowException)
            {
                issues.Add(new(SimilarPhotoRemovalPlanningIssueReason.ArithmeticOverflow));
                continue;
            }

            groups.Add(new(groupIndex, group.Tier, Array.AsReadOnly(members), Array.AsReadOnly(candidates), Array.AsReadOnly(survivors)));
        }

        return issues.Count == 0
            ? new(new(intent.AnalyzedResult, Array.AsReadOnly(groups.ToArray()), selectedBytes), [])
            : Failed(issues);
    }

    private static SimilarPhotoRemovalPlanningResult Failed(SimilarPhotoRemovalPlanningIssue issue) => Failed([issue]);
    private static SimilarPhotoRemovalPlanningResult Failed(IEnumerable<SimilarPhotoRemovalPlanningIssue> issues) =>
        new(null, Array.AsReadOnly(issues.ToArray()));

    private static bool IsSupportedLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            string fullPath = Path.GetFullPath(path);
            return PathComparer.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasSafeSnapshotAttributes(FileAttributes attributes)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        FileAttributes rejected = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device
            | FileAttributes.Offline | FileAttributes.System | FileAttributes.Hidden | FileAttributes.Encrypted
            | recallOnOpen | recallOnDataAccess;
        return (attributes & rejected) == 0;
    }
}
