using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Detection;

public static class ExactDuplicateDetector
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static async Task<ExactDuplicateDetectionResult> DetectAsync(
        IEnumerable<DiscoveredFile> discoveredFiles,
        IContentAnalysisService contentAnalysis,
        IProgress<DuplicateDetectionProgress>? progress = null,
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
            List<DiscoveredFile> candidates = physicalFiles
                .GroupBy(file => file.Length)
                .Where(bucket => bucket.Count() > 1)
                .SelectMany(bucket => bucket)
                .ToList();
            long totalCandidateBytes = candidates.Aggregate(0L, (total, file) => checked(total + file.Length));
            int processedCandidates = 0;
            long processedBytes = 0;
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
                    processedCandidates++;
                    processedBytes = checked(processedBytes + file.Length);
                    if (!hash.Succeeded)
                    {
                        skipped.Add(new DuplicateDetectionSkippedItem(file, hash.FailureReason!.Value));
                        ReportProgress(progress, new DuplicateDetectionProgress(file.NormalizedPath, processedCandidates, processedBytes, totalCandidateBytes, verifiedGroups.Count, skipped.Count, false));
                        continue;
                    }

                    string digestKey = Convert.ToHexString(hash.Digest!.Bytes);
                    if (!digestBuckets.TryGetValue(digestKey, out List<DiscoveredFile>? bucket))
                    {
                        bucket = [];
                        digestBuckets.Add(digestKey, bucket);
                    }

                    bucket.Add(file);
                    ReportProgress(progress, new DuplicateDetectionProgress(file.NormalizedPath, processedCandidates, processedBytes, totalCandidateBytes, verifiedGroups.Count, skipped.Count, false));
                }

                foreach (List<DiscoveredFile> hashBucket in digestBuckets.Values
                             .Where(bucket => bucket.Count > 1)
                             .OrderBy(bucket => bucket[0].NormalizedPath, PathComparer))
                {
                    var equalSets = new List<List<DiscoveredFile>>();
                    foreach (DiscoveredFile candidate in hashBucket)
                    {
                        bool placed = false;
                        for (int setIndex = 0; setIndex < equalSets.Count; setIndex++)
                        {
                            List<DiscoveredFile> equalSet = equalSets[setIndex];
                            ContentComparisonOutcome comparison = await contentAnalysis
                                .CompareAsync(equalSet[0], candidate, cancellationToken)
                                .ConfigureAwait(false);
                            ReportProgress(progress, new DuplicateDetectionProgress(candidate.NormalizedPath, processedCandidates, processedBytes, totalCandidateBytes, verifiedGroups.Count, skipped.Count, true));
                            if (!comparison.Succeeded)
                            {
                                ContentAnalysisFailureReason reason = comparison.FailureReason!.Value;
                                foreach (DiscoveredFile uncertainMember in equalSet.Append(candidate))
                                {
                                    if (!skipped.Any(item => item.File.PhysicalIdentity == uncertainMember.PhysicalIdentity))
                                    {
                                        skipped.Add(new DuplicateDetectionSkippedItem(uncertainMember, reason));
                                    }
                                }

                                // A failure involving the representative invalidates every earlier
                                // comparison in this set. Uncertainty must never survive as a group.
                                equalSets.RemoveAt(setIndex);
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

                    foreach (List<DiscoveredFile> equalSet in equalSets.Where(group => group.Count > 1))
                    {
                        bool remainsValid = true;
                        foreach (DiscoveredFile member in equalSet)
                        {
                            ContentValidationOutcome validation = await contentAnalysis.ValidateAsync(member, cancellationToken).ConfigureAwait(false);
                            if (validation.Succeeded)
                            {
                                continue;
                            }

                            remainsValid = false;
                            foreach (DiscoveredFile uncertainMember in equalSet)
                            {
                                if (!skipped.Any(item => item.File.PhysicalIdentity == uncertainMember.PhysicalIdentity))
                                {
                                    skipped.Add(new DuplicateDetectionSkippedItem(uncertainMember, validation.FailureReason!.Value));
                                }
                            }

                            break;
                        }

                        if (remainsValid)
                        {
                            verifiedGroups.Add(equalSet);
                        }
                    }
                }
            }

            List<DuplicateFileGroup> groups = verifiedGroups
                .Select(group => group.OrderBy(file => file.NormalizedPath, PathComparer).ToList())
                .OrderBy(group => group[0].NormalizedPath, PathComparer)
                .Select(group => new DuplicateFileGroup(Array.AsReadOnly(group.ToArray()), checked((group.Count - 1) * group[0].Length)))
                .ToList();

            ReportProgress(progress, new DuplicateDetectionProgress(string.Empty, processedCandidates, processedBytes, totalCandidateBytes, groups.Count, skipped.Count, true));

            long totalReclaimableBytes = groups.Aggregate(0L, (total, group) => checked(total + group.ReclaimableBytes));
            return new ExactDuplicateDetectionResult(Array.AsReadOnly(groups.ToArray()), Array.AsReadOnly(skipped.ToArray()), totalReclaimableBytes, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ExactDuplicateDetectionResult([], Array.AsReadOnly(skipped.ToArray()), 0, true);
        }
    }

    private static void ReportProgress(IProgress<DuplicateDetectionProgress>? progress, DuplicateDetectionProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception)
        {
            // Progress is observational and must not affect duplicate proof.
        }
    }
}
