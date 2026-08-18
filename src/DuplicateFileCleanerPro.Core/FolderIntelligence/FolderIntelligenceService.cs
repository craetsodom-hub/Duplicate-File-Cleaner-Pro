using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.FolderIntelligence;

/// <summary>
/// Read-only folder analysis built on the established safe discovery and exact-content boundaries.
/// Folder signatures are candidate evidence only; every published equivalence is byte verified.
/// </summary>
public sealed class FolderIntelligenceService(
    IFileDiscoveryService discovery,
    IContentAnalysisService contentAnalysis)
{
    private static readonly StringComparer PathComparer = StringComparer.Ordinal;
    private const int HashCacheLimit = 100_000;

    public async Task<DuplicateFolderScanResult> FindDuplicateFoldersAsync(
        IEnumerable<string> folderRoots,
        DiscoveryPolicy policy,
        IProgress<FolderIntelligenceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderRoots);
        ArgumentNullException.ThrowIfNull(policy);
        var roots = CollapseOverlappingRoots(folderRoots);
        var skipped = new List<SkippedDiscoveryItem>();
        var snapshots = new List<FolderTreeSnapshot>();

        try
        {
            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, new(FolderIntelligenceStage.DiscoveringFolders, roots[rootIndex], rootIndex, roots.Count, 0, false));
                DiscoveryResult discovered = await discovery.DiscoverAsync([new ScanRoot(roots[rootIndex])], policy, cancellationToken: cancellationToken).ConfigureAwait(false);
                skipped.AddRange(discovered.SkippedItems);
                if (discovered.WasCancelled || cancellationToken.IsCancellationRequested)
                {
                    return new([], skipped.AsReadOnly(), true, roots.AsReadOnly());
                }

                snapshots.AddRange(BuildSnapshots(roots[rootIndex], discovered.Files, policy.IncludeSubfolders));
            }

            Report(progress, new(FolderIntelligenceStage.BuildingCandidates, string.Empty, 0, snapshots.Count, 0, false));
            var structuralCandidates = snapshots
                .Where(snapshot => !snapshot.IsEmpty)
                .GroupBy(StructuralSignature, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .OrderBy(group => group.Min(snapshot => snapshot.RootPath), PathComparer)
                .ToList();
            var hashCache = new SessionHashCache(HashCacheLimit);
            List<VerifiedDuplicateFolderGroup> verified = [];
            for (int index = 0; index < structuralCandidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<FolderTreeSnapshot> structuralGroup = structuralCandidates[index].OrderBy(snapshot => snapshot.RootPath, PathComparer).ToList();
                FolderTreeSnapshot candidate = structuralGroup[0];
                Report(progress, new(FolderIntelligenceStage.VerifyingFileTrees, candidate.RootPath, index, structuralCandidates.Count, verified.Count, true));
                List<FolderTreeSnapshot> equivalent = [candidate];
                foreach (FolderTreeSnapshot existing in structuralGroup.Skip(1))
                {
                    if (await AreTreesExactlyEqualAsync(candidate, existing, hashCache, cancellationToken).ConfigureAwait(false))
                    {
                        equivalent.Add(existing);
                    }
                }

                if (equivalent.Count > 1)
                {
                    verified.Add(CreateGroup(equivalent));
                }
            }

            List<VerifiedDuplicateFolderGroup> groups = SuppressNestedGroups(verified);
            Report(progress, new(FolderIntelligenceStage.BuildingDuplicateFolderGroups, string.Empty, structuralCandidates.Count, structuralCandidates.Count, groups.Count, false));
            return new(groups, skipped.AsReadOnly(), false, roots.AsReadOnly());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new([], skipped.AsReadOnly(), true, roots.AsReadOnly());
        }
    }

    public async Task<MasterFolderComparisonResult> CompareMasterFolderAsync(
        string masterFolder,
        IEnumerable<string> targetFolders,
        DiscoveryPolicy policy,
        IProgress<FolderIntelligenceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterFolder);
        ArgumentNullException.ThrowIfNull(targetFolders);
        ArgumentNullException.ThrowIfNull(policy);
        string masterRoot = NormalizeFolder(masterFolder);
        List<string> targets = targetFolders.Select(NormalizeFolder).Where(path => !path.Equals(masterRoot, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, PathComparer).ToList();
        var hashCache = new SessionHashCache(HashCacheLimit);
        try
        {
            Report(progress, new(FolderIntelligenceStage.ReadingMaster, masterRoot, 0, 1, 0, false));
            FolderTreeSnapshot master = await ReadRootSnapshotAsync(masterRoot, policy, cancellationToken).ConfigureAwait(false);
            var results = new List<FolderComparisonTargetResult>();
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = targets[targetIndex];
                Report(progress, new(FolderIntelligenceStage.ReadingComparisonFolder, target, targetIndex, targets.Count, results.Count, false));
                (FolderTreeSnapshot snapshot, IReadOnlyList<SkippedDiscoveryItem> skipped) = await ReadRootSnapshotWithSkipsAsync(target, policy, cancellationToken).ConfigureAwait(false);
                FolderComparisonTargetResult comparison = await CompareSnapshotsAsync(master, snapshot, skipped, hashCache, progress, cancellationToken).ConfigureAwait(false);
                results.Add(comparison);
            }

            Report(progress, new(FolderIntelligenceStage.Completed, string.Empty, results.Count, results.Count, results.Count, false));
            return new(masterRoot, results.AsReadOnly(), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(masterRoot, [], true);
        }
    }

    private async Task<FolderComparisonTargetResult> CompareSnapshotsAsync(
        FolderTreeSnapshot master,
        FolderTreeSnapshot target,
        IReadOnlyList<SkippedDiscoveryItem> skipped,
        SessionHashCache hashCache,
        IProgress<FolderIntelligenceProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, new(FolderIntelligenceStage.MatchingPaths, target.RootPath, 0, master.Files.Count + target.Files.Count, 0, false));
        var masterByPath = master.Files.ToDictionary(file => file.RelativePath, PathComparer);
        var targetByPath = target.Files.ToDictionary(file => file.RelativePath, PathComparer);
        var rows = new List<FolderComparisonRow>();
        var movedTargetPaths = new HashSet<string>(PathComparer);
        var unmatchedMaster = new List<FolderFileEntry>();
        foreach (FolderFileEntry masterFile in master.Files.OrderBy(file => file.RelativePath, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targetByPath.TryGetValue(masterFile.RelativePath, out FolderFileEntry? targetFile))
            {
                unmatchedMaster.Add(masterFile);
                continue;
            }

            Report(progress, new(FolderIntelligenceStage.VerifyingContent, masterFile.RelativePath, rows.Count, master.Files.Count + target.Files.Count, rows.Count, true));
            ContentComparisonOutcome comparison = masterFile.File.Length == targetFile.File.Length
                ? await contentAnalysis.CompareAsync(masterFile.File, targetFile.File, cancellationToken).ConfigureAwait(false)
                : ContentComparisonOutcome.Different();
            rows.Add(comparison.Succeeded && comparison.AreEqual!.Value
                ? CreateRow(target.RootPath, masterFile.RelativePath, FolderComparisonStatus.Identical, masterFile, [targetFile])
                : CreateRow(target.RootPath, masterFile.RelativePath, FolderComparisonStatus.Different, masterFile, [targetFile]));
        }

        var targetCandidates = target.Files.Where(file => !masterByPath.ContainsKey(file.RelativePath)).ToList();
        var targetHashIndex = await BuildHashIndexAsync(targetCandidates, hashCache, cancellationToken).ConfigureAwait(false);
        foreach (FolderFileEntry masterFile in unmatchedMaster)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new List<FolderFileEntry>();
            if (await TryGetHashAsync(masterFile.File, hashCache, cancellationToken).ConfigureAwait(false) is ContentDigest masterDigest
                && targetHashIndex.TryGetValue(HashKey(masterFile.File.Length, masterDigest), out List<FolderFileEntry>? candidates))
            {
                Report(progress, new(FolderIntelligenceStage.FindingMovedRenamedMatches, masterFile.RelativePath, rows.Count, unmatchedMaster.Count, rows.Count, true));
                foreach (FolderFileEntry candidate in candidates)
                {
                    ContentComparisonOutcome comparison = await contentAnalysis.CompareAsync(masterFile.File, candidate.File, cancellationToken).ConfigureAwait(false);
                    if (comparison.Succeeded && comparison.AreEqual!.Value)
                    {
                        matches.Add(candidate);
                        movedTargetPaths.Add(candidate.RelativePath);
                    }
                }
            }

            rows.Add(matches.Count > 0
                ? CreateRow(target.RootPath, masterFile.RelativePath, FolderComparisonStatus.MovedRenamedExactMatch, masterFile, matches)
                : CreateRow(target.RootPath, masterFile.RelativePath, FolderComparisonStatus.OnlyInMaster, masterFile, []));
        }

        foreach (FolderFileEntry targetFile in targetCandidates.OrderBy(file => file.RelativePath, PathComparer))
        {
            if (!movedTargetPaths.Contains(targetFile.RelativePath))
            {
                rows.Add(CreateRow(target.RootPath, targetFile.RelativePath, FolderComparisonStatus.OnlyInTarget, null, [targetFile]));
            }
        }

        rows = rows.OrderBy(row => row.RelativePath, PathComparer).ThenBy(row => row.Status).ToList();
        var summary = new FolderComparisonSummary(
            master.Files.Count,
            target.Files.Count,
            rows.Count(row => row.Status == FolderComparisonStatus.Identical),
            rows.Count(row => row.Status == FolderComparisonStatus.Different),
            rows.Count(row => row.Status == FolderComparisonStatus.OnlyInMaster),
            rows.Count(row => row.Status == FolderComparisonStatus.OnlyInTarget),
            rows.Count(row => row.Status == FolderComparisonStatus.MovedRenamedExactMatch),
            rows.Where(row => row.Status == FolderComparisonStatus.Identical).Sum(row => row.MasterSize ?? 0));
        Report(progress, new(FolderIntelligenceStage.BuildingComparison, target.RootPath, rows.Count, rows.Count, rows.Count, false));
        return new(master.RootPath, target.RootPath, summary, rows.AsReadOnly(), skipped);
    }

    private async Task<Dictionary<string, List<FolderFileEntry>>> BuildHashIndexAsync(IEnumerable<FolderFileEntry> files, SessionHashCache cache, CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, List<FolderFileEntry>>(StringComparer.Ordinal);
        foreach (FolderFileEntry entry in files.OrderBy(file => file.RelativePath, PathComparer))
        {
            ContentDigest? digest = await TryGetHashAsync(entry.File, cache, cancellationToken).ConfigureAwait(false);
            if (digest is null)
            {
                continue;
            }

            string key = HashKey(entry.File.Length, digest);
            if (!index.TryGetValue(key, out List<FolderFileEntry>? bucket))
            {
                bucket = [];
                index.Add(key, bucket);
            }

            bucket.Add(entry);
        }

        return index;
    }

    private async Task<bool> AreTreesExactlyEqualAsync(FolderTreeSnapshot left, FolderTreeSnapshot right, SessionHashCache cache, CancellationToken cancellationToken)
    {
        if (left.LogicalFileCount != right.LogicalFileCount || left.TotalLogicalBytes != right.TotalLogicalBytes)
        {
            return false;
        }

        var rightByPath = right.Files.ToDictionary(file => file.RelativePath, PathComparer);
        foreach (FolderFileEntry leftFile in left.Files.OrderBy(file => file.RelativePath, PathComparer))
        {
            if (!rightByPath.TryGetValue(leftFile.RelativePath, out FolderFileEntry? rightFile)
                || leftFile.File.Length != rightFile.File.Length)
            {
                return false;
            }

            ContentDigest? leftDigest = await TryGetHashAsync(leftFile.File, cache, cancellationToken).ConfigureAwait(false);
            ContentDigest? rightDigest = await TryGetHashAsync(rightFile.File, cache, cancellationToken).ConfigureAwait(false);
            if (leftDigest is null || rightDigest is null || !leftDigest.ToArray().AsSpan().SequenceEqual(rightDigest.ToArray()))
            {
                return false;
            }

            ContentComparisonOutcome comparison = await contentAnalysis.CompareAsync(leftFile.File, rightFile.File, cancellationToken).ConfigureAwait(false);
            if (!comparison.Succeeded || !comparison.AreEqual!.Value)
            {
                return false;
            }
        }

        foreach (FolderFileEntry entry in left.Files.Concat(right.Files))
        {
            ContentValidationOutcome validation = await contentAnalysis.ValidateAsync(entry.File, cancellationToken).ConfigureAwait(false);
            if (!validation.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<FolderTreeSnapshot> ReadRootSnapshotAsync(string root, DiscoveryPolicy policy, CancellationToken cancellationToken) =>
        (await ReadRootSnapshotWithSkipsAsync(root, policy, cancellationToken).ConfigureAwait(false)).Snapshot;

    private async Task<(FolderTreeSnapshot Snapshot, IReadOnlyList<SkippedDiscoveryItem> Skipped)> ReadRootSnapshotWithSkipsAsync(string root, DiscoveryPolicy policy, CancellationToken cancellationToken)
    {
        DiscoveryResult discovered = await discovery.DiscoverAsync([new ScanRoot(root)], policy, cancellationToken: cancellationToken).ConfigureAwait(false);
        FolderTreeSnapshot[] snapshots = BuildSnapshots(root, discovered.Files, includeSubfolders: false);
        return (snapshots.Length == 0 ? EmptySnapshot(root) : snapshots[0], discovered.SkippedItems);
    }

    private static FolderTreeSnapshot[] BuildSnapshots(string root, IReadOnlyList<DiscoveredFile> files, bool includeSubfolders)
    {
        string normalizedRoot = NormalizeFolder(root);
        var byDirectory = new Dictionary<string, List<FolderFileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (DiscoveredFile file in files.OrderBy(file => file.NormalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            string directory = Path.GetDirectoryName(file.NormalizedPath) ?? normalizedRoot;
            string current = includeSubfolders ? directory : normalizedRoot;
            while (true)
            {
                if (!byDirectory.TryGetValue(current, out List<FolderFileEntry>? entries))
                {
                    entries = [];
                    byDirectory.Add(current, entries);
                }

                entries.Add(new FolderFileEntry(current, NormalizeRelativePath(Path.GetRelativePath(current, file.NormalizedPath)), file));
                if (!includeSubfolders || current.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase) || !IsUnderRoot(parent, normalizedRoot))
                {
                    break;
                }

                current = parent;
            }
        }

        return byDirectory.OrderBy(pair => pair.Key, PathComparer).Select(pair => CreateSnapshot(pair.Key, pair.Value)).ToArray();
    }

    private static FolderTreeSnapshot CreateSnapshot(string root, IEnumerable<FolderFileEntry> entries)
    {
        FolderFileEntry[] files = entries.OrderBy(file => file.RelativePath, PathComparer).ToArray();
        int physicalCount = files.Select(file => file.File.PhysicalIdentity).Distinct().Count();
        long physicalBytes = files.GroupBy(file => file.File.PhysicalIdentity).Sum(group => group.First().File.Length);
        return new(root, files, files.Length, physicalCount, files.Sum(file => file.File.Length), physicalBytes);
    }

    private static VerifiedDuplicateFolderGroup CreateGroup(IReadOnlyList<FolderTreeSnapshot> members)
    {
        FolderTreeSnapshot retained = members.OrderBy(folder => folder.RootPath, PathComparer).First();
        HashSet<PhysicalFileIdentity> retainedIdentities = retained.Files.Select(file => file.File.PhysicalIdentity).ToHashSet();
        long reclaimable = members.SelectMany(folder => folder.Files)
            .GroupBy(file => file.File.PhysicalIdentity)
            .Where(group => !retainedIdentities.Contains(group.Key))
            .Sum(group => group.First().File.Length);
        return new(
            members.OrderBy(folder => folder.RootPath, PathComparer).ToArray(),
            retained.LogicalFileCount,
            members.SelectMany(folder => folder.Files).Select(file => file.File.PhysicalIdentity).Distinct().Count(),
            retained.TotalLogicalBytes,
            reclaimable);
    }

    private static List<VerifiedDuplicateFolderGroup> SuppressNestedGroups(IEnumerable<VerifiedDuplicateFolderGroup> groups)
    {
        var ordered = groups.OrderBy(group => group.MemberFolders.Min(folder => PathDepth(folder.RootPath))).ThenBy(group => group.MemberFolders[0].RootPath, PathComparer).ToList();
        var kept = new List<VerifiedDuplicateFolderGroup>();
        foreach (VerifiedDuplicateFolderGroup group in ordered)
        {
            bool redundant = kept.Any(parent => group.MemberFolders.All(candidate => parent.MemberFolders.Any(existing => IsStrictDescendant(candidate.RootPath, existing.RootPath))));
            if (!redundant)
            {
                kept.Add(group);
            }
        }

        return kept;
    }

    private static string StructuralSignature(FolderTreeSnapshot snapshot) =>
        string.Join("\n", snapshot.Files.OrderBy(file => file.RelativePath, PathComparer).Select(file => string.Create(CultureInfo.InvariantCulture, $"{file.RelativePath}\0{file.File.Length}")));

    private async Task<ContentDigest?> TryGetHashAsync(DiscoveredFile file, SessionHashCache cache, CancellationToken cancellationToken)
    {
        HashCacheKey key = new(file.PhysicalIdentity, file.Length, file.LastWriteTimeUtc, file.ChangeTimeUtc);
        if (cache.TryGet(key, out ContentDigest? cached))
        {
            return cached;
        }

        ContentHashOutcome outcome = await contentAnalysis.HashAsync(file, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return null;
        }

        cache.Set(key, outcome.Digest!);
        return outcome.Digest;
    }

    private static FolderComparisonRow CreateRow(string targetRoot, string relativePath, FolderComparisonStatus status, FolderFileEntry? master, List<FolderFileEntry> compared) =>
        new(targetRoot, relativePath, status, master, compared, master?.File.Length, compared.Count == 0 ? null : compared[0].File.Length, master?.File.LastWriteTimeUtc, compared.Count == 0 ? null : compared[0].File.LastWriteTimeUtc);

    private static string HashKey(long length, ContentDigest digest) => $"{length}:{Convert.ToHexString(digest.ToArray())}";

    private static string NormalizeFolder(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(path))
        {
            throw new ArgumentException("Folder must be an existing local directory.", nameof(path));
        }

        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static List<string> CollapseOverlappingRoots(IEnumerable<string> roots)
    {
        var normalized = roots.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizeFolder).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length).ThenBy(path => path, PathComparer).ToList();
        var collapsed = new List<string>();
        foreach (string root in normalized)
        {
            if (!collapsed.Any(existing => existing.Equals(root, StringComparison.OrdinalIgnoreCase) || IsUnderRoot(root, existing)))
            {
                collapsed.Add(root);
            }
        }

        return collapsed;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsStrictDescendant(string path, string root) => !path.Equals(root, StringComparison.OrdinalIgnoreCase) && IsUnderRoot(path, root);

    private static int PathDepth(string path) => path.Count(character => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);

    private static string NormalizeRelativePath(string path) => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static FolderTreeSnapshot EmptySnapshot(string root) => new(root, [], 0, 0, 0, 0);

    private static void Report(IProgress<FolderIntelligenceProgress>? progress, FolderIntelligenceProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception)
        {
        }
    }

    private readonly record struct HashCacheKey(PhysicalFileIdentity Identity, long Length, DateTimeOffset LastWriteUtc, DateTimeOffset ChangeTimeUtc);

    private sealed class SessionHashCache(int limit)
    {
        private readonly ConcurrentDictionary<HashCacheKey, ContentDigest> entries = new();
        private readonly ConcurrentQueue<HashCacheKey> order = new();

        public bool TryGet(HashCacheKey key, out ContentDigest? digest) => entries.TryGetValue(key, out digest);

        public void Set(HashCacheKey key, ContentDigest digest)
        {
            if (!entries.TryAdd(key, digest))
            {
                return;
            }

            order.Enqueue(key);
            while (entries.Count > limit && order.TryDequeue(out HashCacheKey evicted))
            {
                entries.TryRemove(evicted, out _);
            }
        }
    }
}
