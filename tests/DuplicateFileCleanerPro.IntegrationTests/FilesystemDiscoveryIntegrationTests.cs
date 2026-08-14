using System.Runtime.InteropServices;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class FilesystemDiscoveryIntegrationTests
{
    private static readonly string[] ExpectedOrdinaryFileNames = ["plain.txt", "résumé.txt", "image.png"];

    [TestMethod]
    public async Task DiscoversOrdinaryFilesIncludingNestedUnicodeAndSpaces()
    {
        using TemporaryCorpus corpus = new();
        corpus.Write("plain.txt");
        corpus.Write("folder with spaces\\résumé.txt");
        corpus.Write("folder with spaces\\image.png");
        Directory.CreateDirectory(Path.Combine(corpus.Root, "empty"));

        DiscoveryResult result = await DiscoverAsync(corpus.Root);

        Assert.HasCount(3, result.Files);
        CollectionAssert.AreEquivalent(ExpectedOrdinaryFileNames, result.Files.Select(file => file.FileName).ToArray());
        Assert.IsFalse(result.WasCancelled);
    }

    [TestMethod]
    public void NormalizationDeduplicatesNestedRootsAndDoesNotMatchSimilarSiblingPrefix()
    {
        using TemporaryCorpus corpus = new();
        string data = Path.Combine(corpus.Root, "Data");
        string nested = Path.Combine(data, "Nested");
        string database = Path.Combine(corpus.Root, "Database");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(database);

        RootNormalizationResult result = new WindowsScanRootNormalizer().Normalize([data, data + Path.DirectorySeparatorChar, nested, database]);

        Assert.HasCount(2, result.Roots);
        CollectionAssert.AreEquivalent(new[] { Path.GetFullPath(data), Path.GetFullPath(database) }, result.Roots.Select(root => root.NormalizedPath).ToArray());
    }

    [TestMethod]
    public async Task HardLinksExposeTheSamePhysicalIdentity()
    {
        using TemporaryCorpus corpus = new();
        string source = corpus.Write("source.bin", "content");
        string link = Path.Combine(corpus.Root, "linked.bin");
        if (!CreateHardLink(link, source, IntPtr.Zero))
        {
            Assert.Inconclusive($"Hard-link creation is unavailable: {Marshal.GetLastWin32Error()}.");
        }

        DiscoveryResult result = await DiscoverAsync(corpus.Root);
        DiscoveredFile sourceFile = result.Files.Single(file => file.FileName == "source.bin");
        DiscoveredFile linkedFile = result.Files.Single(file => file.FileName == "linked.bin");
        Assert.AreEqual(sourceFile.PhysicalIdentity, linkedFile.PhysicalIdentity);
    }

    [TestMethod]
    public async Task ReparseDirectoriesAreNotTraversedWhenSupported()
    {
        using TemporaryCorpus corpus = new();
        string target = Path.Combine(corpus.Root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "outside.txt"), "safe");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(corpus.Root, "link"), target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Directory symbolic links are not permitted in this test environment.");
        }

        DiscoveryResult result = await DiscoverAsync(corpus.Root);
        Assert.HasCount(1, result.Files);
        Assert.IsTrue(result.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.ReparsePoint));
    }

    [TestMethod]
    public async Task NamedAlternateStreamIsSkippedOnNtfs()
    {
        using TemporaryCorpus corpus = new();
        if (!string.Equals(new DriveInfo(Path.GetPathRoot(corpus.Root)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("The disposable corpus is not on NTFS.");
        }

        string file = corpus.Write("streamed.txt");
        File.WriteAllText(file + ":phase2:$DATA", "metadata");

        DiscoveryResult result = await DiscoverAsync(corpus.Root);
        Assert.IsEmpty(result.Files);
        Assert.IsTrue(result.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.AlternateDataStream));
    }

    [TestMethod]
    public async Task CancellationStopsBeforeProcessingTheTemporaryCorpus()
    {
        using TemporaryCorpus corpus = new();
        for (int index = 0; index < 256; index++)
        {
            corpus.Write($"cancel\\{index:D4}.txt");
        }

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        DiscoveryResult result = await new WindowsFileDiscoveryService().DiscoverAsync([new ScanRoot(corpus.Root)], new DiscoveryPolicy(), cancellationToken: cancellation.Token);
        Assert.IsTrue(result.WasCancelled);
        Assert.IsEmpty(result.Files);
    }

    [TestMethod]
    public async Task ExactDetectionRejectsNearMissesAndCollapsesHardLinkAliases()
    {
        using TemporaryCorpus corpus = new();
        string exactOne = corpus.Write("exact-one.dat", "matching content");
        string exactTwo = corpus.Write("exact-two.txt", "matching content");
        corpus.Write("same-length-middle.dat", "matching Xontent");
        corpus.Write("prefix-suffix.dat", "matching contenX");
        corpus.Write("empty-one.bin", string.Empty);
        corpus.Write("empty-two.bin", string.Empty);
        corpus.WriteBytes("large-one.bin", Enumerable.Repeat((byte)0x5A, 512 * 1024).ToArray());
        corpus.WriteBytes("large-two.bin", Enumerable.Repeat((byte)0x5A, 512 * 1024).ToArray());
        string alias = Path.Combine(corpus.Root, "exact-hard-link.dat");
        if (!CreateHardLink(alias, exactOne, IntPtr.Zero))
        {
            Assert.Inconclusive($"Hard-link creation is unavailable: {Marshal.GetLastWin32Error()}.");
        }

        DiscoveryResult discovery = await DiscoverAsync(corpus.Root);
        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync(discovery.Files, new WindowsContentAnalysisService());

        Assert.IsFalse(result.WasCancelled);
        Assert.HasCount(3, result.Groups);
        Assert.IsTrue(result.Groups.Any(group => group.Files.All(file => file.Length == 0) && group.Files.Count == 2));
        Assert.IsTrue(result.Groups.Any(group => group.Files.All(file => file.Length == 512 * 1024) && group.Files.Count == 2));
        DuplicateFileGroup exactGroup = result.Groups.Single(group => group.Files.Any(file => file.FileName == "exact-two.txt"));
        Assert.HasCount(2, exactGroup.Files);
        Assert.IsTrue(exactGroup.Files.Any(file => file.FileName is "exact-one.dat" or "exact-hard-link.dat"));
        Assert.AreEqual("matching content".Length, exactGroup.ReclaimableBytes);
    }

    [TestMethod]
    public async Task ChangedOrRemovedFileIsSkippedWithoutReportingADuplicate()
    {
        using TemporaryCorpus corpus = new();
        string first = corpus.Write("first.dat", "stable");
        corpus.Write("second.dat", "stable");
        DiscoveryResult discovery = await DiscoverAsync(corpus.Root);
        File.WriteAllText(first, "changed after discovery");

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync(discovery.Files, new WindowsContentAnalysisService());

        Assert.IsEmpty(result.Groups);
        Assert.IsTrue(result.SkippedItems.Any(item => item.Reason == ContentAnalysisFailureReason.ChangedDuringAnalysis));
    }

    [TestMethod]
    public async Task RemovedFileIsIsolatedWithoutReportingADuplicate()
    {
        using TemporaryCorpus corpus = new();
        string removed = corpus.Write("removed.dat", "stable");
        corpus.Write("remaining.dat", "stable");
        DiscoveryResult discovery = await DiscoverAsync(corpus.Root);
        File.Delete(removed);

        ExactDuplicateDetectionResult result = await ExactDuplicateDetector.DetectAsync(discovery.Files, new WindowsContentAnalysisService());

        Assert.IsEmpty(result.Groups);
        Assert.HasCount(1, result.SkippedItems);
    }

    [TestMethod]
    public async Task ScanSessionRunsMultipleRootsThroughDiscoveryAndExactDetection()
    {
        using TemporaryCorpus corpus = new();
        string firstRoot = Path.Combine(corpus.Root, "first root");
        string secondRoot = Path.Combine(corpus.Root, "second root");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(firstRoot, "résumé.txt"), "same content");
        File.WriteAllText(Path.Combine(secondRoot, "copy.bin"), "same content");
        File.WriteAllText(Path.Combine(secondRoot, "different.dat"), "not the same");

        using var session = new ScanSessionService(new WindowsFileDiscoveryService(), new WindowsContentAnalysisService());
        ScanSessionResult result = await session.RunAsync([new ScanRoot(firstRoot), new ScanRoot(secondRoot)], new DiscoveryPolicy());

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
        Assert.HasCount(1, result.CompletedResult.Detection.Groups);
        Assert.AreEqual("same content".Length, result.CompletedResult.Detection.TotalReclaimableBytes);
    }

    private static async Task<DiscoveryResult> DiscoverAsync(string root)
    {
        RootNormalizationResult normalized = new WindowsScanRootNormalizer().Normalize([root]);
        Assert.HasCount(1, normalized.Roots);
        return await new WindowsFileDiscoveryService().DiscoverAsync(normalized.Roots, new DiscoveryPolicy());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class TemporaryCorpus : IDisposable
    {
        public TemporaryCorpus()
        {
            Root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Phase2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string relativePath, string content = "test")
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string WriteBytes(string relativePath, byte[] content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
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
