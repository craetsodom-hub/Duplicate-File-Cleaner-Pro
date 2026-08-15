using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Similarity;

public sealed class SimilarPhotoEngine
{
    private const int AnalysisDimension = 64;
    private const int MaxCandidatesPerPhoto = 64;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif",
    };

    private readonly ISimilarPhotoDecoder decoder;

    public SimilarPhotoEngine(ISimilarPhotoDecoder decoder) => this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));

    public async Task<SimilarPhotoAnalysisResult> AnalyzeAsync(
        IEnumerable<DiscoveredFile> discoveredFiles,
        SimilarPhotoSensitivity sensitivity = SimilarPhotoSensitivity.Balanced,
        IProgress<SimilarPhotoProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discoveredFiles);
        var skipped = new List<SimilarPhotoSkippedItem>();
        try
        {
            var inputFiles = new List<DiscoveredFile>();
            foreach (DiscoveredFile file in discoveredFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inputFiles.Add(file);
                Report(progress, new(SimilarPhotoProgressStage.FindingPhotos, file.NormalizedPath, inputFiles.Count, null, 0, 0, 0, skipped.Count));
            }
            List<DiscoveredFile> physicalFiles = inputFiles.OrderBy(file => file.NormalizedPath, PathComparer)
                .GroupBy(file => file.PhysicalIdentity).Select(group => group.First()).ToList();
            Report(progress, new(SimilarPhotoProgressStage.FindingPhotos, string.Empty, physicalFiles.Count, physicalFiles.Count, 0, 0, 0, 0));

            var analyzed = new List<AnalyzedPhoto>();
            foreach (DiscoveredFile file in physicalFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SupportedExtensions.Contains(file.Extension))
                {
                    skipped.Add(new(file, SimilarPhotoSkipReason.UnsupportedFormat));
                    continue;
                }

                PhotoDecodeOutcome decoded = await decoder.DecodeAsync(file, AnalysisDimension, cancellationToken).ConfigureAwait(false);
                if (!decoded.Succeeded)
                {
                    skipped.Add(new(file, decoded.FailureReason!.Value));
                }
                else
                {
                    analyzed.Add(new(file, SimilarPhotoFingerprintBuilder.Create(decoded.Image!, cancellationToken)));
                }
                Report(progress, new(SimilarPhotoProgressStage.AnalyzingPhotos, file.NormalizedPath, analyzed.Count + skipped.Count, physicalFiles.Count, 0, 0, 0, skipped.Count));
            }

            List<(int First, int Second)> candidates = BuildCandidates(analyzed, cancellationToken);
            var evidenceByPair = new Dictionary<(int, int), SimilarityEvidence>();
            var relationships = new List<SimilarPhotoRelationship>();
            SimilarPhotoThresholds thresholds = SimilarPhotoThresholds.For(sensitivity);
            int comparisons = 0;
            foreach ((int first, int second) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SimilarityEvidence evidence = SimilarPhotoComparer.Compare(analyzed[first].Fingerprint, analyzed[second].Fingerprint);
                evidenceByPair[(first, second)] = evidence;
                comparisons++;
                if (SimilarPhotoComparer.Meets(evidence, thresholds))
                    relationships.Add(new(analyzed[first].File, analyzed[second].File, SimilarPhotoComparer.Tier(evidence.CompositeStrength), evidence));
                Report(progress, new(SimilarPhotoProgressStage.ComparingSimilarities, analyzed[second].File.NormalizedPath, comparisons, candidates.Count, candidates.Count, comparisons, 0, skipped.Count));
            }

            SimilarPhotoGroup[] groups = BuildCompleteLinkGroups(analyzed, evidenceByPair, relationships, thresholds, ref comparisons, cancellationToken);
            Report(progress, new(SimilarPhotoProgressStage.BuildingGroups, string.Empty, groups.Length, groups.Length, candidates.Count, comparisons, groups.Length, skipped.Count));
            return new(groups, relationships.AsReadOnly(), skipped.AsReadOnly(), analyzed.Count, candidates.Count, comparisons, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new([], [], skipped.AsReadOnly(), 0, 0, 0, true);
        }
    }

    private static List<(int First, int Second)> BuildCandidates(IReadOnlyList<AnalyzedPhoto> photos, CancellationToken token)
    {
        if (photos.Count <= 256)
        {
            var allPairs = new List<(int, int)>(photos.Count * Math.Max(0, photos.Count - 1) / 2);
            for (int second = 1; second < photos.Count; second++)
            for (int first = 0; first < second; first++)
            {
                token.ThrowIfCancellationRequested();
                allPairs.Add((first, second));
            }
            return allPairs;
        }

        var index = new Dictionary<(int Aspect, int Segment, ushort Value), List<int>>();
        var exactRepresentatives = new Dictionary<(ulong, ulong, ulong), int>();
        var pairs = new HashSet<(int, int)>();
        for (int current = 0; current < photos.Count; current++)
        {
            token.ThrowIfCancellationRequested();
            SimilarPhotoFingerprint fingerprint = photos[current].Fingerprint;
            if (exactRepresentatives.TryGetValue((fingerprint.DifferenceHash, fingerprint.VerticalDifferenceHash, fingerprint.AverageHash), out int exact)) pairs.Add((exact, current));
            else exactRepresentatives[(fingerprint.DifferenceHash, fingerprint.VerticalDifferenceHash, fingerprint.AverageHash)] = current;

            int aspect = AspectBucket(fingerprint.AspectRatio);
            var possible = new HashSet<int>();
            for (int delta = -1; delta <= 1; delta++)
            for (int segment = 0; segment < 12; segment++)
            {
                ushort value = Segment(fingerprint, segment);
                if (index.TryGetValue((aspect + delta, segment, value), out List<int>? bucket)) foreach (int item in bucket) possible.Add(item);
            }

            foreach (int prior in possible.OrderByDescending(candidate => SharedSegments(fingerprint, photos[candidate].Fingerprint)).ThenBy(candidate => candidate).Take(MaxCandidatesPerPhoto))
                pairs.Add((prior, current));

            for (int segment = 0; segment < 12; segment++)
            {
                var key = (aspect, segment, Segment(fingerprint, segment));
                if (!index.TryGetValue(key, out List<int>? bucket)) index[key] = bucket = [];
                bucket.Add(current);
                if (bucket.Count > MaxCandidatesPerPhoto) bucket.RemoveAt(0);
            }
        }
        return pairs.OrderBy(pair => pair.Item1).ThenBy(pair => pair.Item2).ToList();
    }

    private static SimilarPhotoGroup[] BuildCompleteLinkGroups(
        IReadOnlyList<AnalyzedPhoto> photos,
        Dictionary<(int, int), SimilarityEvidence> evidence,
        IReadOnlyList<SimilarPhotoRelationship> relationships,
        SimilarPhotoThresholds thresholds,
        ref int comparisons,
        CancellationToken token)
    {
        var relationPairs = relationships.Select(relationship => (IndexOf(photos, relationship.First), IndexOf(photos, relationship.Second))).ToList();
        var assigned = new HashSet<int>();
        var groups = new List<SimilarPhotoGroup>();
        IEnumerable<int> anchors = Enumerable.Range(0, photos.Count).OrderByDescending(index => relationPairs.Count(pair => pair.Item1 == index || pair.Item2 == index)).ThenBy(index => photos[index].File.NormalizedPath, PathComparer);
        foreach (int anchor in anchors)
        {
            token.ThrowIfCancellationRequested();
            if (assigned.Contains(anchor)) continue;
            List<int> neighbors = relationPairs.Where(pair => pair.Item1 == anchor || pair.Item2 == anchor).Select(pair => pair.Item1 == anchor ? pair.Item2 : pair.Item1).Where(index => !assigned.Contains(index)).Distinct().OrderBy(index => photos[index].File.NormalizedPath, PathComparer).ToList();
            var members = new List<int> { anchor };
            foreach (int candidate in neighbors)
            {
                bool compatible = true;
                foreach (int member in members)
                {
                    (int first, int second) = member < candidate ? (member, candidate) : (candidate, member);
                    if (!evidence.TryGetValue((first, second), out SimilarityEvidence? comparison))
                    {
                        comparison = SimilarPhotoComparer.Compare(photos[first].Fingerprint, photos[second].Fingerprint);
                        evidence[(first, second)] = comparison;
                        comparisons++;
                    }
                    if (!SimilarPhotoComparer.Meets(comparison, thresholds)) { compatible = false; break; }
                }
                if (compatible) members.Add(candidate);
            }
            if (members.Count < 2) continue;
            foreach (int member in members) assigned.Add(member);
            double weakest = members.Skip(1).Select(member => evidence[(Math.Min(anchor, member), Math.Max(anchor, member))].CompositeStrength).Min();
            groups.Add(new(photos[anchor].File, members.Select(index => photos[index].File).OrderBy(file => file.NormalizedPath, PathComparer).ToArray(), SimilarPhotoComparer.Tier(weakest)));
        }
        return groups.OrderBy(group => group.Representative.NormalizedPath, PathComparer).ToArray();
    }

    private static int IndexOf(IReadOnlyList<AnalyzedPhoto> photos, DiscoveredFile file)
    {
        for (int index = 0; index < photos.Count; index++) if (ReferenceEquals(photos[index].File, file) || photos[index].File == file) return index;
        throw new InvalidOperationException("Relationship referenced an unknown photo.");
    }

    private static ushort Segment(SimilarPhotoFingerprint fingerprint, int segment) => segment switch
    {
        < 4 => (ushort)(fingerprint.DifferenceHash >> (segment * 16)),
        < 8 => (ushort)(fingerprint.VerticalDifferenceHash >> ((segment - 4) * 16)),
        _ => (ushort)(fingerprint.AverageHash >> ((segment - 8) * 16)),
    };

    private static int SharedSegments(SimilarPhotoFingerprint first, SimilarPhotoFingerprint second)
    {
        int count = 0;
        for (int segment = 0; segment < 12; segment++) if (Segment(first, segment) == Segment(second, segment)) count++;
        return count;
    }

    private static int AspectBucket(double aspectRatio) => (int)Math.Round(Math.Log(aspectRatio, 2) * 8);
    private static void Report(IProgress<SimilarPhotoProgress>? progress, SimilarPhotoProgress value) { try { progress?.Report(value); } catch (Exception) { } }
    private sealed record AnalyzedPhoto(DiscoveredFile File, SimilarPhotoFingerprint Fingerprint);
}
