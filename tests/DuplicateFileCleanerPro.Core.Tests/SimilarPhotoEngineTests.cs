using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class SimilarPhotoEngineTests
{
    [TestMethod]
    public async Task DetectsKnownTransformationsWithoutGroupingAdversarialScenes()
    {
        PhotoAnalysisImage original = Scene(64, 48, 0, 1.0, 0);
        var images = new Dictionary<string, PhotoAnalysisImage>(StringComparer.OrdinalIgnoreCase)
        {
            ["original.png"] = original,
            ["renamed.jpg"] = original,
            ["resized.png"] = Resize(original, 40, 30),
            ["brighter.bmp"] = Scene(64, 48, 12, 1.0, 0),
            ["contrast.tif"] = Scene(64, 48, 0, 1.08, 0),
            ["cropped.webp"] = Scene(64, 48, 0, 1.0, 2),
            ["slightly-rotated.png"] = Rotate(original, 2),
            ["unrelated-same-colors.png"] = AdversarialScene(64, 48, false),
            ["unrelated-same-layout.png"] = AdversarialScene(64, 48, true),
        };
        SimilarPhotoAnalysisResult result = await AnalyzeAsync(images);

        Assert.IsFalse(result.WasCancelled);
        string[] detected = result.Groups.SelectMany(group => group.Photos).Select(photo => photo.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEquivalent(KnownSimilarNames.ToArray(), detected, string.Join("; ", result.Groups.Select(GroupKey)));
        Assert.IsFalse(detected.Any(name => name.StartsWith("unrelated", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UnsupportedAndDecodeFailuresAreStructuredAndDoNotAbort()
    {
        DiscoveredFile unsupported = File("notes.txt", 1);
        DiscoveredFile corrupt = File("corrupt.png", 2);
        DiscoveredFile valid = File("valid.png", 3);
        var decoder = new FakeDecoder(new Dictionary<string, PhotoAnalysisImage> { [valid.NormalizedPath] = Scene(32, 32, 0, 1, 0) })
        {
            Failures = { [corrupt.NormalizedPath] = SimilarPhotoSkipReason.CorruptImage },
        };

        SimilarPhotoAnalysisResult result = await new SimilarPhotoEngine(decoder).AnalyzeAsync([valid, corrupt, unsupported]);

        Assert.HasCount(2, result.SkippedItems);
        Assert.IsTrue(result.SkippedItems.Any(item => item.Reason == SimilarPhotoSkipReason.UnsupportedFormat));
        Assert.IsTrue(result.SkippedItems.Any(item => item.Reason == SimilarPhotoSkipReason.CorruptImage));
    }

    [TestMethod]
    public async Task CancellationDuringDecodingReturnsCancelledWithoutGroups()
    {
        using var source = new CancellationTokenSource();
        var decoder = new FakeDecoder(Enumerable.Range(0, 20).ToDictionary(index => $"C:\\photos\\{index}.png", _ => Scene(32, 32, 0, 1, 0))) { CancelAfter = 3, Cancellation = source };
        SimilarPhotoAnalysisResult result = await new SimilarPhotoEngine(decoder).AnalyzeAsync(decoder.Images.Keys.Select((path, index) => File(path, (ulong)index + 1)), cancellationToken: source.Token);
        Assert.IsTrue(result.WasCancelled);
        Assert.IsEmpty(result.Groups);
    }

    [TestMethod]
    public async Task CancellationDuringComparisonIsObservedAndProgressUsesTruthfulStages()
    {
        var images = Enumerable.Range(0, 20).ToDictionary(index => $"C:\\photos\\compare-{index}.png", index => Scene(32, 32, index, 1, 0));
        var decoder = new FakeDecoder(images);
        using var source = new CancellationTokenSource();
        var progress = new CancellingProgress(source, SimilarPhotoProgressStage.ComparingSimilarities);
        SimilarPhotoAnalysisResult result = await new SimilarPhotoEngine(decoder).AnalyzeAsync(images.Keys.Select((path, index) => File(path, (ulong)index + 1)), progress: progress, cancellationToken: source.Token);
        Assert.IsTrue(result.WasCancelled);
        CollectionAssert.Contains(progress.Stages, SimilarPhotoProgressStage.FindingPhotos);
        CollectionAssert.Contains(progress.Stages, SimilarPhotoProgressStage.AnalyzingPhotos);
        CollectionAssert.Contains(progress.Stages, SimilarPhotoProgressStage.ComparingSimilarities);
    }

    [TestMethod]
    public async Task ResultsAndOrderingAreDeterministic()
    {
        var images = Enumerable.Range(0, 12).ToDictionary(index => $"scene-{index:D2}.png", index => Scene(48, 32, index % 3, 1, index % 2));
        SimilarPhotoAnalysisResult first = await AnalyzeAsync(images);
        SimilarPhotoAnalysisResult second = await AnalyzeAsync(images.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value));
        CollectionAssert.AreEqual(first.Groups.Select(GroupKey).ToArray(), second.Groups.Select(GroupKey).ToArray());
        Assert.AreEqual(first.CandidatePairCount, second.CandidatePairCount);
    }

    [TestMethod]
    public void SimilarityTiersAndThresholdsAvoidFalsePrecisionAtThePublicBoundary()
    {
        Assert.AreEqual(SimilarityTier.VerySimilar, SimilarPhotoComparer.Tier(0.95));
        Assert.AreEqual(SimilarityTier.Similar, SimilarPhotoComparer.Tier(0.88));
        Assert.AreEqual(SimilarityTier.LooselySimilar, SimilarPhotoComparer.Tier(0.80));
        Assert.IsGreaterThan(SimilarPhotoThresholds.For(SimilarPhotoSensitivity.Broad).MinimumCompositeStrength, SimilarPhotoThresholds.For(SimilarPhotoSensitivity.Balanced).MinimumCompositeStrength);
        Assert.IsGreaterThan(SimilarPhotoThresholds.For(SimilarPhotoSensitivity.Balanced).MinimumCompositeStrength, SimilarPhotoThresholds.For(SimilarPhotoSensitivity.Strict).MinimumCompositeStrength);
    }

    [TestMethod]
    public async Task CompleteLinkGroupingDoesNotCreateAConnectedComponentChain()
    {
        PhotoAnalysisImage a = StripeScene(64, 48, 10);
        PhotoAnalysisImage b = StripeScene(64, 48, 12);
        PhotoAnalysisImage c = StripeScene(64, 48, 18);
        SimilarPhotoAnalysisResult result = await AnalyzeAsync(new Dictionary<string, PhotoAnalysisImage> { ["a.png"] = a, ["b.png"] = b, ["c.png"] = c }, SimilarPhotoSensitivity.Broad);
        Assert.IsFalse(result.Groups.Any(group => group.Photos.Count == 3 && !AllPairsMeet(group, result.Relationships)));
    }

    [TestMethod]
    [TestCategory("SimilarPhotoStress")]
    public async Task CandidateIndexRemainsLinearBoundedForThousandsOfFingerprints()
    {
        const int count = 5_000;
        var images = new Dictionary<string, PhotoAnalysisImage>(count);
        for (int index = 0; index < count; index++) images[$"C:\\scale\\{index:D5}.png"] = NoiseScene(24, 24, index);
        long memoryBefore = GC.GetTotalMemory(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SimilarPhotoAnalysisResult result = await AnalyzeAsync(images, SimilarPhotoSensitivity.Strict);
        watch.Stop();
        long memoryDelta = GC.GetTotalMemory(true) - memoryBefore;
        Assert.IsLessThanOrEqualTo(count * 65, result.CandidatePairCount);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(30), watch.Elapsed);
        TestContext.WriteLine($"images={count}; candidates={result.CandidatePairCount}; comparisons={result.FinalComparisonCount}; groups={result.Groups.Count}; elapsedMs={watch.ElapsedMilliseconds}; managedDelta={memoryDelta}");
    }

    public TestContext TestContext { get; set; } = null!;
    private static readonly HashSet<string> KnownSimilarNames = ["original.png", "renamed.jpg", "resized.png", "brighter.bmp", "contrast.tif", "cropped.webp", "slightly-rotated.png"];

    private static async Task<SimilarPhotoAnalysisResult> AnalyzeAsync(Dictionary<string, PhotoAnalysisImage> images, SimilarPhotoSensitivity sensitivity = SimilarPhotoSensitivity.Balanced)
    {
        var decoder = new FakeDecoder(images.ToDictionary(pair => File(pair.Key, (ulong)images.Keys.ToList().IndexOf(pair.Key) + 1).NormalizedPath, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        return await new SimilarPhotoEngine(decoder).AnalyzeAsync(decoder.Images.Keys.Select((path, index) => File(path, (ulong)index + 1)), sensitivity);
    }

    private static string GroupKey(SimilarPhotoGroup group) => string.Join('|', group.Photos.Select(photo => photo.NormalizedPath));
    private static bool AllPairsMeet(SimilarPhotoGroup group, IReadOnlyList<SimilarPhotoRelationship> relationships)
    {
        for (int first = 0; first < group.Photos.Count; first++)
        for (int second = first + 1; second < group.Photos.Count; second++)
            if (!relationships.Any(match => (match.First == group.Photos[first] && match.Second == group.Photos[second]) || (match.Second == group.Photos[first] && match.First == group.Photos[second]))) return false;
        return true;
    }

    private static DiscoveredFile File(string path, ulong id) => new(path.Contains(':') ? path : $"C:\\photos\\{path}", Path.GetFileName(path), Path.GetExtension(path), 100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id, 0), FileAttributes.Normal);

    private static PhotoAnalysisImage Scene(int width, int height, int brightness, double contrast, int crop)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int sx = Math.Clamp(crop + x * (width - 2 * crop) / width, 0, width - 1);
            int sy = Math.Clamp(crop + y * (height - 2 * crop) / height, 0, height - 1);
            int baseValue = ((sx * 5 + sy * 3) ^ (sx / 7 * 29)) & 255;
            int shape = (sx - width / 3) * (sx - width / 3) + (sy - height / 2) * (sy - height / 2) < Math.Min(width, height) * Math.Min(width, height) / 12 ? 70 : 0;
            Set(pixels, width, x, y, Adjust(baseValue + shape, brightness, contrast), Adjust(baseValue / 2 + 60, brightness, contrast), Adjust(220 - baseValue / 3, brightness, contrast));
        }
        return new(width, height, pixels);
    }

    private static PhotoAnalysisImage AdversarialScene(int width, int height, bool vertical)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int cell = vertical ? x / 6 : y / 5; byte value = (byte)(cell % 2 == 0 ? 210 : 40); Set(pixels, width, x, y, value, (byte)(230 - value / 2), (byte)(80 + value / 3)); }
        return new(width, height, pixels);
    }

    private static PhotoAnalysisImage StripeScene(int width, int height, int stripeWidth)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { byte value = (byte)(((x / stripeWidth) % 2) * 180 + 30); Set(pixels, width, x, y, value, (byte)(255 - value), 120); }
        return new(width, height, pixels);
    }

    private static PhotoAnalysisImage NoiseScene(int width, int height, int seed)
    {
        var random = new Random(seed); byte[] pixels = new byte[width * height * 4]; random.NextBytes(pixels); for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255; return new(width, height, pixels);
    }

    private static PhotoAnalysisImage Resize(PhotoAnalysisImage source, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4]; ReadOnlySpan<byte> input = source.Pixels.Span;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int sourceX = x * source.Width / width; int sourceY = y * source.Height / height; input.Slice((sourceY * source.Width + sourceX) * 4, 4).CopyTo(pixels.AsSpan((y * width + x) * 4, 4)); }
        return new(width, height, pixels);
    }

    private static PhotoAnalysisImage Rotate(PhotoAnalysisImage source, double degrees)
    {
        byte[] pixels = new byte[source.Width * source.Height * 4];
        ReadOnlySpan<byte> input = source.Pixels.Span;
        double radians = degrees * Math.PI / 180;
        double cosine = Math.Cos(radians); double sine = Math.Sin(radians);
        double centerX = (source.Width - 1) / 2.0; double centerY = (source.Height - 1) / 2.0;
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            int sourceX = (int)Math.Round(centerX + (x - centerX) * cosine + (y - centerY) * sine);
            int sourceY = (int)Math.Round(centerY - (x - centerX) * sine + (y - centerY) * cosine);
            int target = (y * source.Width + x) * 4;
            if (sourceX >= 0 && sourceX < source.Width && sourceY >= 0 && sourceY < source.Height) input.Slice((sourceY * source.Width + sourceX) * 4, 4).CopyTo(pixels.AsSpan(target, 4));
            else pixels[target + 3] = 255;
        }
        return new(source.Width, source.Height, pixels);
    }

    private static byte Adjust(int value, int brightness, double contrast) => (byte)Math.Clamp((int)Math.Round((value - 128) * contrast + 128 + brightness), 0, 255);
    private static void Set(byte[] pixels, int width, int x, int y, byte r, byte g, byte b) { int offset = (y * width + x) * 4; pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255; }

    private sealed class FakeDecoder(IReadOnlyDictionary<string, PhotoAnalysisImage> images) : ISimilarPhotoDecoder
    {
        public IReadOnlyDictionary<string, PhotoAnalysisImage> Images { get; } = images;
        public Dictionary<string, SimilarPhotoSkipReason> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CancelAfter { get; init; } = int.MaxValue;
        public CancellationTokenSource? Cancellation { get; init; }
        private int calls;
        public Task<PhotoDecodeOutcome> DecodeAsync(DiscoveredFile file, int maximumDimension, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++calls >= CancelAfter) Cancellation?.Cancel();
            if (Failures.TryGetValue(file.NormalizedPath, out SimilarPhotoSkipReason reason)) return Task.FromResult(PhotoDecodeOutcome.Failure(reason));
            return Task.FromResult(PhotoDecodeOutcome.Success(Images[file.NormalizedPath]));
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource source, SimilarPhotoProgressStage cancelAt) : IProgress<SimilarPhotoProgress>
    {
        public List<SimilarPhotoProgressStage> Stages { get; } = [];
        public void Report(SimilarPhotoProgress value) { Stages.Add(value.Stage); if (value.Stage == cancelAt) source.Cancel(); }
    }
}
