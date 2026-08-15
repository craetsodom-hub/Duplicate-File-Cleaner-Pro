using System.Diagnostics;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Similarity;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;
using DuplicateFileCleanerPro.Infrastructure.Windows.Similarity;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class SimilarPhotoWindowsIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NativeDecoderHandlesCommonFormatsAndIsolatesCorruptUnsupportedAndReplacedFiles()
    {
        using var corpus = new SimilarPhotoCorpus();
        byte[] scene = CreateScene(80, 60, 0, false);
        await corpus.WriteImageAsync("scene.jpg", BitmapEncoder.JpegEncoderId, 80, 60, scene);
        await corpus.WriteImageAsync("scene.png", BitmapEncoder.PngEncoderId, 80, 60, scene);
        await corpus.WriteImageAsync("scene.bmp", BitmapEncoder.BmpEncoderId, 80, 60, scene);
        await corpus.WriteImageAsync("scene.gif", BitmapEncoder.GifEncoderId, 80, 60, scene);
        await corpus.WriteImageAsync("scene.tiff", BitmapEncoder.TiffEncoderId, 80, 60, scene);
        corpus.WriteBmp("replace.bmp", 80, 60, scene);
        corpus.WriteBytes("corrupt.webp", [1, 2, 3, 4, 5]);
        corpus.WriteBytes("unsupported.xyz", [6, 7, 8]);

        DiscoveryResult discovery = await DiscoverAsync(corpus.Root);
        var decoder = new WindowsSimilarPhotoDecoder();
        foreach (DiscoveredFile photo in discovery.Files.Where(file => file.FileName.StartsWith("scene", StringComparison.Ordinal)))
        {
            PhotoDecodeOutcome decoded = await decoder.DecodeAsync(photo, 64);
            Assert.IsTrue(decoded.Succeeded, $"{photo.Extension}: {decoded.FailureReason}");
            Assert.IsLessThanOrEqualTo(64, Math.Max(decoded.Image!.Width, decoded.Image.Height));
        }

        SimilarPhotoAnalysisResult result = await new SimilarPhotoEngine(decoder).AnalyzeAsync(discovery.Files);
        Assert.IsTrue(result.SkippedItems.Any(item => item.File.FileName == "corrupt.webp" && item.Reason is SimilarPhotoSkipReason.CorruptImage or SimilarPhotoSkipReason.CodecUnavailable));
        Assert.IsTrue(result.SkippedItems.Any(item => item.File.FileName == "unsupported.xyz" && item.Reason == SimilarPhotoSkipReason.UnsupportedFormat));

        DiscoveredFile replaceCandidate = discovery.Files.Single(file => file.FileName == "replace.bmp");
        corpus.WriteBytesReplacing("replace.bmp", new byte[(int)replaceCandidate.Length]);
        PhotoDecodeOutcome replaced = await decoder.DecodeAsync(replaceCandidate, 64);
        Assert.AreEqual(SimilarPhotoSkipReason.ChangedDuringAnalysis, replaced.FailureReason);
    }

    [TestMethod]
    public void ExifOrientationMappingsCoverRotationsAndMirrorsWithoutSourceMutation()
    {
        var transform = new BitmapTransform();
        WindowsSimilarPhotoDecoder.ApplyOrientation(transform, 6);
        Assert.AreEqual(BitmapRotation.Clockwise90Degrees, transform.Rotation);

        transform = new BitmapTransform();
        WindowsSimilarPhotoDecoder.ApplyOrientation(transform, 8);
        Assert.AreEqual(BitmapRotation.Clockwise270Degrees, transform.Rotation);

        transform = new BitmapTransform();
        WindowsSimilarPhotoDecoder.ApplyOrientation(transform, 2);
        Assert.AreEqual(BitmapFlip.Horizontal, transform.Flip);
    }

    [TestMethod]
    public async Task GeneratedCorpusFindsTransformedScenesWithoutAdversarialFalsePositives()
    {
        using var corpus = new SimilarPhotoCorpus();
        byte[] original = CreateScene(96, 72, 0, false);
        await corpus.WriteImageAsync("original.png", BitmapEncoder.PngEncoderId, 96, 72, original);
        await corpus.WriteImageAsync("renamed-exact.png", BitmapEncoder.PngEncoderId, 96, 72, original);
        await corpus.WriteImageAsync("recompressed.jpg", BitmapEncoder.JpegEncoderId, 96, 72, original);
        await corpus.WriteImageAsync("brighter-üñîçødé.png", BitmapEncoder.PngEncoderId, 96, 72, CreateScene(96, 72, 10, false));
        await corpus.WriteImageAsync("resized.png", BitmapEncoder.PngEncoderId, 48, 36, Resize(original, 96, 72, 48, 36));
        await corpus.WriteImageAsync("minor-crop.png", BitmapEncoder.PngEncoderId, 88, 64, Crop(original, 96, 72, 4));
        await corpus.WriteImageAsync("same-colors-different-layout.png", BitmapEncoder.PngEncoderId, 96, 72, CreateScene(96, 72, 0, true));
        await corpus.WriteImageAsync("repetitive-a.png", BitmapEncoder.PngEncoderId, 96, 72, Repetitive(96, 72, true));
        await corpus.WriteImageAsync("repetitive-b.png", BitmapEncoder.PngEncoderId, 96, 72, Repetitive(96, 72, false));
        corpus.WriteBytes("broken.jpg", [0xFF, 0xD8, 0x00]);

        DiscoveryResult discovery = await DiscoverAsync(corpus.Root);
        using Process process = Process.GetCurrentProcess();
        long memoryBefore = GC.GetTotalMemory(true);
        var watch = Stopwatch.StartNew();
        SimilarPhotoAnalysisResult result = await new SimilarPhotoEngine(new WindowsSimilarPhotoDecoder()).AnalyzeAsync(discovery.Files);
        watch.Stop();
        process.Refresh();

        string[] groupedNames = result.Groups.SelectMany(group => group.Photos).Select(file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string expected in new[] { "original.png", "renamed-exact.png", "recompressed.jpg", "brighter-üñîçødé.png", "resized.png", "minor-crop.png" })
            CollectionAssert.Contains(groupedNames, expected, $"Grouped: {string.Join(", ", groupedNames)}; relationships: {string.Join(" | ", result.Relationships.Select(match => $"{match.First.FileName}/{match.Second.FileName}={match.Evidence.CompositeStrength:F3},{match.Evidence.StructuralSimilarity:F3},{match.Evidence.DifferenceHashSimilarity:F3}"))}");
        Assert.IsFalse(groupedNames.Contains("same-colors-different-layout.png", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(groupedNames.Contains("repetitive-a.png", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(groupedNames.Contains("repetitive-b.png", StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(result.SkippedItems.Any(item => item.File.FileName == "broken.jpg"));
        TestContext.WriteLine($"eligible={result.EligiblePhotoCount}; candidates={result.CandidatePairCount}; comparisons={result.FinalComparisonCount}; groups={result.Groups.Count}; falsePositives=0; falseNegatives=0; elapsedMs={watch.ElapsedMilliseconds}; managedDelta={GC.GetTotalMemory(true) - memoryBefore}; peakWorkingSet={process.PeakWorkingSet64}");
    }

    private static Task<DiscoveryResult> DiscoverAsync(string root) => new WindowsFileDiscoveryService().DiscoverAsync([new ScanRoot(root)], new DiscoveryPolicy());

    private static byte[] CreateScene(int width, int height, int brightness, bool adversarial)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int value = adversarial ? ((x / 8 + y / 8) % 2) * 170 + 30 : (x * 3 + y * 2 + (x / 16) * 20 + (y / 12) * 15);
            bool circle = (x - width / 3) * (x - width / 3) + (y - height / 2) * (y - height / 2) < height * height / 10;
            byte r = (byte)Math.Clamp((value & 255) + brightness + (circle ? 40 : 0), 0, 255);
            byte g = (byte)Math.Clamp(60 + ((value * 3) & 127) + brightness, 0, 255);
            byte b = (byte)Math.Clamp(220 - ((value * 2) & 127) + brightness, 0, 255);
            Set(pixels, width, x, y, r, g, b);
        }
        return pixels;
    }

    private static byte[] Repetitive(int width, int height, bool vertical)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { byte value = (byte)(((vertical ? x : y) / 6 % 2) * 190 + 25); Set(pixels, width, x, y, value, (byte)(240 - value / 2), 100); }
        return pixels;
    }

    private static byte[] Resize(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        byte[] output = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) source.AsSpan(((y * sourceHeight / height) * sourceWidth + x * sourceWidth / width) * 4, 4).CopyTo(output.AsSpan((y * width + x) * 4, 4));
        return output;
    }

    private static byte[] Crop(byte[] source, int sourceWidth, int sourceHeight, int amount)
    {
        int width = sourceWidth - 2 * amount; int height = sourceHeight - 2 * amount; byte[] output = new byte[width * height * 4];
        for (int y = 0; y < height; y++) source.AsSpan(((y + amount) * sourceWidth + amount) * 4, width * 4).CopyTo(output.AsSpan(y * width * 4, width * 4));
        return output;
    }

    private static void Set(byte[] pixels, int width, int x, int y, byte r, byte g, byte b) { int offset = (y * width + x) * 4; pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255; }

    private sealed class SimilarPhotoCorpus : IDisposable
    {
        public SimilarPhotoCorpus() { Root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Phase16", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public string Root { get; }
        public async Task WriteImageAsync(string name, Guid encoderId, int width, int height, byte[] pixels)
        {
            string path = Path.Combine(Root, name); await File.WriteAllBytesAsync(path, []);
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        public void WriteBytes(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(Root, name), bytes);
        public void WriteBytesReplacing(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(Root, name), bytes);
        public void WriteBmp(string name, int width, int height, byte[] bgraPixels)
        {
            int pixelBytes = checked(width * height * 4);
            byte[] file = new byte[54 + pixelBytes];
            file[0] = (byte)'B'; file[1] = (byte)'M';
            BitConverter.GetBytes(file.Length).CopyTo(file, 2);
            BitConverter.GetBytes(54).CopyTo(file, 10);
            BitConverter.GetBytes(40).CopyTo(file, 14);
            BitConverter.GetBytes(width).CopyTo(file, 18);
            BitConverter.GetBytes(height).CopyTo(file, 22);
            BitConverter.GetBytes((short)1).CopyTo(file, 26);
            BitConverter.GetBytes((short)32).CopyTo(file, 28);
            BitConverter.GetBytes(pixelBytes).CopyTo(file, 34);
            for (int y = 0; y < height; y++) bgraPixels.AsSpan(y * width * 4, width * 4).CopyTo(file.AsSpan(54 + (height - 1 - y) * width * 4, width * 4));
            File.WriteAllBytes(Path.Combine(Root, name), file);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
