using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class EndToEndSafetyCorpusIntegrationTests
{
    private const int UniqueFileCount = 2000;
    private const int Mebibyte = 1024 * 1024;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("SafetyCorpus")]
    public async Task KnownLargeCorpusProducesOnlyExpectedIndependentDuplicateGroups()
    {
        using var corpus = new SafetyCorpus();
        for (int index = 1; index <= UniqueFileCount; index++)
        {
            corpus.WriteUnique($"nested/bucket-{index % 37:D2}/unique-{index:D4}.bin", index, (byte)(index % 251));
        }

        string largeFirst = corpus.WriteRepeated("large/group-a-one.bin", 32 * Mebibyte, 0x31);
        corpus.WriteRepeated("large/group-a-two.bin", 32 * Mebibyte, 0x31);
        corpus.WriteRepeated("large/group-b-one.bin", 16 * Mebibyte, 0x42);
        corpus.WriteRepeated("large/group-b-two.bin", 16 * Mebibyte, 0x42);
        corpus.WriteRepeated("large/group-b-three.bin", 16 * Mebibyte, 0x42);
        corpus.WriteRepeated("negative/same-size-one.bin", 24 * Mebibyte, 0x51);
        corpus.WriteRepeated("negative/same-size-two.bin", 24 * Mebibyte, 0x52);
        corpus.WriteRepeated("unique-large.bin", 32 * Mebibyte, 0x61);
        corpus.WriteUnique("empty/one.bin", 0, 0);
        corpus.WriteUnique("empty/two.bin", 0, 0);
        const string unicodeContent = "résumé duplicate ✓";
        corpus.WriteText("Unicode/résumé.txt", unicodeContent);
        corpus.WriteText("Unicode/副本.bin", unicodeContent);
        string hidden = corpus.WriteText("policy-hidden.txt", "excluded");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        string alias = Path.Combine(corpus.Root, "large", "group-a-hard-link.bin");
        if (!CreateHardLink(alias, largeFirst, IntPtr.Zero))
        {
            Assert.Inconclusive($"Hard-link creation is unavailable: {Marshal.GetLastWin32Error()}.");
        }

        long corpusBytes = Directory.EnumerateFiles(corpus.Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        using Process process = Process.GetCurrentProcess();
        int handleCountBefore = process.HandleCount;
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var totalTimer = Stopwatch.StartNew();
        var discoveryTimer = Stopwatch.StartNew();
        RootNormalizationResult roots = new WindowsScanRootNormalizer().Normalize([corpus.Root]);
        DiscoveryResult discovery = await new WindowsFileDiscoveryService().DiscoverAsync(roots.Roots, new DiscoveryPolicy());
        discoveryTimer.Stop();
        var analysisTimer = Stopwatch.StartNew();
        ExactDuplicateDetectionResult detection = await ExactDuplicateDetector.DetectAsync(discovery.Files, new WindowsContentAnalysisService());
        analysisTimer.Stop();
        totalTimer.Stop();
        long managedAfter = GC.GetTotalMemory(forceFullCollection: true);
        process.Refresh();
        int handleCountAfter = process.HandleCount;
        long peakWorkingSet = process.PeakWorkingSet64;

        long expectedUnicodeBytes = Encoding.UTF8.GetByteCount(unicodeContent);
        long expectedReclaimable = 64L * Mebibyte + expectedUnicodeBytes;
        Assert.IsFalse(discovery.WasCancelled);
        Assert.HasCount(UniqueFileCount + 13, discovery.Files);
        Assert.IsTrue(discovery.SkippedItems.Any(item => item.Path == hidden && item.Reason == DiscoverySkipReason.HiddenByPolicy));
        Assert.IsFalse(detection.WasCancelled);
        Assert.HasCount(4, detection.Groups);
        Assert.AreEqual(9, detection.Groups.Sum(group => group.Files.Count));
        Assert.AreEqual(expectedReclaimable, detection.TotalReclaimableBytes);
        PhysicalFileIdentity largeIdentity = discovery.Files.Single(item => item.FileName == Path.GetFileName(largeFirst)).PhysicalIdentity;
        Assert.AreEqual(1, detection.Groups.SelectMany(group => group.Files).Count(file => file.PhysicalIdentity == largeIdentity));

        TestContext.WriteLine($"files={discovery.Files.Count}; bytes={corpusBytes}; groups={detection.Groups.Count}; members={detection.Groups.Sum(group => group.Files.Count)}; reclaimable={detection.TotalReclaimableBytes}");
        TestContext.WriteLine($"discoveryMs={discoveryTimer.ElapsedMilliseconds}; analysisMs={analysisTimer.ElapsedMilliseconds}; totalMs={totalTimer.ElapsedMilliseconds}");
        TestContext.WriteLine($"managedBefore={managedBefore}; managedAfter={managedAfter}; peakWorkingSet={peakWorkingSet}; handlesBefore={handleCountBefore}; handlesAfter={handleCountAfter}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class SafetyCorpus : IDisposable
    {
        public SafetyCorpus()
        {
            Root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.SafetyCorpus", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string WriteText(string relativePath, string content)
        {
            string path = Prepare(relativePath);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        public string WriteUnique(string relativePath, int length, byte seed)
        {
            string path = Prepare(relativePath);
            byte[] bytes = new byte[length];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = unchecked((byte)(seed + index));
            }

            File.WriteAllBytes(path, bytes);
            return path;
        }

        public string WriteRepeated(string relativePath, int length, byte value)
        {
            string path = Prepare(relativePath);
            byte[] block = new byte[Mebibyte];
            Array.Fill(block, value);
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, block.Length, FileOptions.SequentialScan);
            for (int remaining = length; remaining > 0; remaining -= block.Length)
            {
                stream.Write(block, 0, Math.Min(block.Length, remaining));
            }

            return path;
        }

        private string Prepare(string relativePath)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
