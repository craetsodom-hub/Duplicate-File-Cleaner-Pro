using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class ContentAnalysisRaceIntegrationTests
{
    [TestMethod]
    public async Task LengthAndTimestampPreservingMutationDuringHashFailsClosed()
    {
        using var corpus = new RaceCorpus();
        string path = corpus.WriteLarge("hash.bin", 0x41);
        DiscoveredFile file = await DiscoverFileAsync(path);
        using var observer = new BlockingObserver(ContentAnalysisOperation.Hashing, path);
        var analysis = new WindowsContentAnalysisService(observer);

        Task<ContentHashOutcome> hashing = Task.Run(() => analysis.HashAsync(file));
        observer.WaitUntilEntered();
        MutatePrefixAndRestoreTimestamp(path, file.LastWriteTimeUtc.UtcDateTime);
        observer.Release();
        ContentHashOutcome result = await hashing;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ContentAnalysisFailureReason.ChangedDuringAnalysis, result.FailureReason);
    }

    [TestMethod]
    public async Task LengthAndTimestampPreservingMutationDuringComparisonFailsClosed()
    {
        using var corpus = new RaceCorpus();
        string firstPath = corpus.WriteLarge("first.bin", 0x42);
        string secondPath = corpus.WriteLarge("second.bin", 0x42);
        IReadOnlyList<DiscoveredFile> files = await DiscoverFilesAsync(corpus.Root);
        DiscoveredFile first = files.Single(file => file.NormalizedPath == firstPath);
        DiscoveredFile second = files.Single(file => file.NormalizedPath == secondPath);
        using var observer = new BlockingObserver(ContentAnalysisOperation.Comparing, firstPath);
        var analysis = new WindowsContentAnalysisService(observer);

        Task<ContentComparisonOutcome> comparing = Task.Run(() => analysis.CompareAsync(first, second));
        observer.WaitUntilEntered();
        MutatePrefixAndRestoreTimestamp(firstPath, first.LastWriteTimeUtc.UtcDateTime);
        observer.Release();
        ContentComparisonOutcome result = await comparing;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ContentAnalysisFailureReason.ChangedDuringAnalysis, result.FailureReason);
    }

    [TestMethod]
    public async Task ReplacementAfterHashingBeforeComparisonNeverProducesAGroup()
    {
        using var corpus = new RaceCorpus();
        string firstPath = corpus.WriteLarge("first.bin", 0x43);
        corpus.WriteLarge("second.bin", 0x43);
        string replacement = corpus.WriteLarge("replacement.bin", 0x43);
        IReadOnlyList<DiscoveredFile> files = (await DiscoverFilesAsync(corpus.Root)).Where(file => file.FileName != "replacement.bin").ToArray();
        var analysis = new ReplaceAfterHashesAnalysis(new WindowsContentAnalysisService(), firstPath, replacement);

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync(files, analysis);

        Assert.IsEmpty(result.Groups);
        Assert.IsNotEmpty(result.SkippedItems);
    }

    [TestMethod]
    public async Task DeletionWhileComparisonHandlesAreOpenIsRejectedByFinalValidation()
    {
        using var corpus = new RaceCorpus();
        string firstPath = corpus.WriteLarge("first.bin", 0x44);
        string secondPath = corpus.WriteLarge("second.bin", 0x44);
        IReadOnlyList<DiscoveredFile> files = await DiscoverFilesAsync(corpus.Root);
        var analysis = new WindowsContentAnalysisService(new DeletingObserver(ContentAnalysisOperation.Comparing, secondPath));

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync(files, analysis);

        Assert.IsEmpty(result.Groups);
        Assert.IsFalse(File.Exists(secondPath));
        Assert.IsNotEmpty(result.SkippedItems);
    }

    [TestMethod]
    public async Task DeletionWhileHashHandleIsOpenNeverProducesAGroup()
    {
        using var corpus = new RaceCorpus();
        string firstPath = corpus.WriteLarge("first.bin", 0x48);
        corpus.WriteLarge("second.bin", 0x48);
        IReadOnlyList<DiscoveredFile> files = await DiscoverFilesAsync(corpus.Root);
        var analysis = new WindowsContentAnalysisService(new DeletingObserver(ContentAnalysisOperation.Hashing, firstPath));

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync(files, analysis);

        Assert.IsEmpty(result.Groups);
        Assert.IsFalse(File.Exists(firstPath));
        Assert.IsNotEmpty(result.SkippedItems);
    }

    [TestMethod]
    public async Task CancellationWhileHashingIsPromptAndCannotPublishGroups()
    {
        using var corpus = new RaceCorpus();
        string firstPath = corpus.WriteLarge("first.bin", 0x45);
        corpus.WriteLarge("second.bin", 0x45);
        IReadOnlyList<DiscoveredFile> files = await DiscoverFilesAsync(corpus.Root);
        using var observer = new BlockingObserver(ContentAnalysisOperation.Hashing, firstPath);
        using var cancellation = new CancellationTokenSource();
        var analysis = new WindowsContentAnalysisService(observer);

        Task<ExactDuplicateDetectionResult> detecting = Task.Run(() => ExactDuplicateDetector.DetectAsync(files, analysis, cancellationToken: cancellation.Token));
        observer.WaitUntilEntered();
        cancellation.Cancel();
        observer.Release();
        ExactDuplicateDetectionResult result = await detecting;

        Assert.IsTrue(result.WasCancelled);
        Assert.IsEmpty(result.Groups);
    }

    [TestMethod]
    public async Task SamePathRecreatedWithSameContentButDifferentIdentityFailsClosed()
    {
        using var corpus = new RaceCorpus();
        string firstPath = corpus.WriteLarge("first.bin", 0x46);
        corpus.WriteLarge("second.bin", 0x46);
        IReadOnlyList<DiscoveredFile> files = await DiscoverFilesAsync(corpus.Root);
        string replacement = corpus.WriteLarge("replacement.bin", 0x46);
        File.Move(replacement, firstPath, overwrite: true);

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync(files.Where(file => file.FileName != "replacement.bin"), new WindowsContentAnalysisService());

        Assert.IsEmpty(result.Groups);
        Assert.IsTrue(result.SkippedItems.Any(item => item.Reason == ContentAnalysisFailureReason.ChangedDuringAnalysis));
    }

    [TestMethod]
    public async Task RepeatedReadOperationsDoNotLeakFileHandles()
    {
        using var corpus = new RaceCorpus();
        corpus.WriteLarge("first.bin", 0x47);
        corpus.WriteLarge("second.bin", 0x47);
        IReadOnlyList<DiscoveredFile> files = await DiscoverFilesAsync(corpus.Root);
        var analysis = new WindowsContentAnalysisService();
        await analysis.CompareAsync(files[0], files[1]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int before = System.Diagnostics.Process.GetCurrentProcess().HandleCount;

        for (int iteration = 0; iteration < 25; iteration++)
        {
            Assert.IsTrue((await analysis.HashAsync(files[0])).Succeeded);
            Assert.IsTrue((await analysis.CompareAsync(files[0], files[1])).Succeeded);
            Assert.IsTrue((await analysis.ValidateAsync(files[0])).Succeeded);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        int after = System.Diagnostics.Process.GetCurrentProcess().HandleCount;
        Assert.IsLessThanOrEqualTo(before + 24, after, $"Handle count grew from {before} to {after}.");
    }

    private static async Task<DiscoveredFile> DiscoverFileAsync(string path) =>
        (await DiscoverFilesAsync(Path.GetDirectoryName(path)!)).Single(file => file.NormalizedPath == path);

    private static async Task<IReadOnlyList<DiscoveredFile>> DiscoverFilesAsync(string root) =>
        (await new WindowsFileDiscoveryService().DiscoverAsync([new ScanRoot(root)], new DiscoveryPolicy())).Files;

    private static void MutatePrefixAndRestoreTimestamp(string path, DateTime lastWriteTimeUtc)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.WriteByte(0x7F);
            stream.Flush(flushToDisk: true);
        }

        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
    }

    private sealed class BlockingObserver(ContentAnalysisOperation expectedOperation, string expectedPath) : IContentAnalysisObserver, IDisposable
    {
        private readonly ManualResetEventSlim entered = new();
        private readonly ManualResetEventSlim release = new();
        private int observed;

        public void OnChunkRead(ContentAnalysisOperation operation, string path, long bytesRead)
        {
            if (operation != expectedOperation || !string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase) || Interlocked.Exchange(ref observed, 1) != 0)
            {
                return;
            }

            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)), "The deterministic mutation test did not release the content reader.");
        }

        public void WaitUntilEntered() => Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "The content reader did not reach the synchronized mutation point.");
        public void Release() => release.Set();
        public void Dispose()
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }

    private sealed class DeletingObserver(ContentAnalysisOperation expectedOperation, string pathToDelete) : IContentAnalysisObserver
    {
        private int deleted;

        public void OnChunkRead(ContentAnalysisOperation operation, string path, long bytesRead)
        {
            if (operation == expectedOperation && Interlocked.Exchange(ref deleted, 1) == 0)
            {
                File.Delete(pathToDelete);
            }
        }
    }

    private sealed class ReplaceAfterHashesAnalysis(WindowsContentAnalysisService inner, string pathToReplace, string replacement) : IContentAnalysisService
    {
        private int hashes;

        public async Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default)
        {
            ContentHashOutcome outcome = await inner.HashAsync(file, cancellationToken);
            if (Interlocked.Increment(ref hashes) == 2)
            {
                File.Move(replacement, pathToReplace, overwrite: true);
            }

            return outcome;
        }

        public Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default) => inner.CompareAsync(left, right, cancellationToken);
        public Task<ContentValidationOutcome> ValidateAsync(DiscoveredFile file, CancellationToken cancellationToken = default) => inner.ValidateAsync(file, cancellationToken);
    }

    private sealed class RaceCorpus : IDisposable
    {
        public RaceCorpus()
        {
            Root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Hardening", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string WriteLarge(string name, byte value)
        {
            string path = Path.Combine(Root, name);
            byte[] block = new byte[1024 * 1024];
            Array.Fill(block, value);
            File.WriteAllBytes(path, block);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
