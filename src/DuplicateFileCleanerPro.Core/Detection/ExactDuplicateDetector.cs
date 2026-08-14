using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Detection;

public static class ExactDuplicateDetector
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static async Task<ExactDuplicateDetectionResult> DetectAsync(
        IEnumerable<DiscoveredFile> discoveredFiles,
        IContentAnalysisService contentAnalysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discoveredFiles);
        ArgumentNullException.ThrowIfNull(contentAnalysis);

        var skipped = new List<DuplicateDetectionSkippedItem>();
        try
        {
            // One physical object may have several names through hard links. Retaining only the
            // stable first pathname prevents aliases from becoming a false duplicate group.
            List<DiscoveredFile> physicalFiles = discoveredFiles
                .OrderBy(file => file.NormalizedPath, PathComparer)
                .GroupBy(file => file.PhysicalIdentity)
                .Select(group => group.First())
                .ToList();

            var verifiedGroups = new List<List<DiscoveredFile>>();
            foreach (IGrouping<long, DiscoveredFile> sizeBucket in physicalFiles
                         .GroupBy(file => file.Length)
                         .OrderBy(bucket => bucket.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sizeBucket.Count() < 2)
                {
                    continue;
                }

                var digestBuckets = new Dictionary<string, List<DiscoveredFile>>(StringComparer.Ordinal);
                foreach (DiscoveredFile file in sizeBucket.OrderBy(file => file.NormalizedPath, PathComparer))
                {
                    ContentHashOutcome hash = await contentAnalysis.HashAsync(file, cancellationToken).ConfigureAwait(false);
                    if (!hash.Succeeded)
                    {
                        skipped.Add(new DuplicateDetectionSkippedItem(file, hash.FailureReason!.Value));
                        continue;
                    }

                    string digestKey = Convert.ToHexString(hash.Digest!.Bytes);
                    if (!digestBuckets.TryGetValue(digestKey, out List<DiscoveredFile>? bucket))
                    {
                        bucket = [];
                        digestBuckets.Add(digestKey, bucket);
                    }

                    bucket.Add(file);
                }

                foreach (List<DiscoveredFile> hashBucket in digestBuckets.Values
                             .Where(bucket => bucket.Count > 1)
                             .OrderBy(bucket => bucket[0].NormalizedPath, PathComparer))
                {
                    var equalSets = new List<List<DiscoveredFile>>();
                    foreach (DiscoveredFile candidate in hashBucket)
                    {
                        bool placed = false;
                        foreach (List<DiscoveredFile> equalSet in equalSets)
                        {
                            ContentComparisonOutcome comparison = await contentAnalysis
                                .CompareAsync(equalSet[0], candidate, cancellationToken)
                                .ConfigureAwait(false);
                            if (!comparison.Succeeded)
                            {
                                skipped.Add(new DuplicateDetectionSkippedItem(candidate, comparison.FailureReason!.Value));
                                placed = true;
                                break;
                            }

                            if (comparison.AreEqual!.Value)
                            {
                                equalSet.Add(candidate);
                                placed = true;
                                break;
                            }
                        }

                        if (!placed)
                        {
                            equalSets.Add([candidate]);
                        }
                    }

                    verifiedGroups.AddRange(equalSets.Where(group => group.Count > 1));
                }
            }

            List<DuplicateFileGroup> groups = verifiedGroups
                .Select(group => group.OrderBy(file => file.NormalizedPath, PathComparer).ToList())
                .OrderBy(group => group[0].NormalizedPath, PathComparer)
                .Select(group => new DuplicateFileGroup(group, checked((group.Count - 1) * group[0].Length)))
                .ToList();

            long totalReclaimableBytes = groups.Aggregate(0L, (total, group) => checked(total + group.ReclaimableBytes));
            return new ExactDuplicateDetectionResult(groups, skipped, totalReclaimableBytes, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ExactDuplicateDetectionResult([], skipped, 0, true);
        }
    }
}
