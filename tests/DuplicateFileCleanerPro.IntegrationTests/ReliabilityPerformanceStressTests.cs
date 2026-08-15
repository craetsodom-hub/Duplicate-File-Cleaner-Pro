using System.Diagnostics;
using System.Security.Cryptography;
using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class ReliabilityPerformanceStressTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Stress")]
    public async Task DisposableCorporaRemainCorrectAcrossDiscoveryDetectionCancellationAndResultsScale()
    {
        using var corpus = new StressCorpus();
        corpus.CreateManyUnique(25_000);
        Measurement unique = await MeasureAsync(corpus.UniqueRoot, detect: false);
        Assert.AreEqual(25_000, unique.Files);

        corpus.CreateSameSizeNegatives(2_000);
        Measurement negatives = await MeasureAsync(corpus.NegativeRoot, detect: true);
        Assert.AreEqual(0, negatives.Groups);

        corpus.CreateDuplicateGroups(2_000);
        Measurement duplicates = await MeasureAsync(corpus.DuplicateRoot, detect: true);
        Assert.AreEqual(2_000, duplicates.Groups);
        Assert.AreEqual(4_000, duplicates.Members);

        corpus.CreateLargeFiles();
        Measurement large = await MeasureAsync(corpus.LargeRoot, detect: true);
        Assert.AreEqual(1, large.Groups);
        Assert.AreEqual(2, large.Members);

        long cancellationMs = await AssertCancellationAsync(corpus.UniqueRoot);
        int handleDelta = await AssertRepeatedScansAsync(corpus.NegativeRoot, cycles: 10);
        AssertResultsScale();

        TestContext.WriteLine($"A unique files={unique.Files}; bytes={unique.Bytes}; discoveryMs={unique.DiscoveryMs}; managed={unique.ManagedBytes}; handles={unique.Handles}");
        TestContext.WriteLine($"B negatives files={negatives.Files}; bytes={negatives.Bytes}; discoveryMs={negatives.DiscoveryMs}; analysisMs={negatives.AnalysisMs}; groups={negatives.Groups}");
        TestContext.WriteLine($"C duplicate files={duplicates.Files}; bytes={duplicates.Bytes}; discoveryMs={duplicates.DiscoveryMs}; analysisMs={duplicates.AnalysisMs}; groups={duplicates.Groups}; members={duplicates.Members}; reclaimable={duplicates.ReclaimableBytes}");
        TestContext.WriteLine($"D large files={large.Files}; bytes={large.Bytes}; discoveryMs={large.DiscoveryMs}; analysisMs={large.AnalysisMs}; groups={large.Groups}; peakWorkingSet={large.PeakWorkingSet}");
        TestContext.WriteLine($"cancellationMs={cancellationMs}; repeatedScanCycles=10; repeatedHandleDelta={handleDelta}");
    }

    private static async Task<Measurement> MeasureAsync(string root, bool detect)
    {
        using Process process = Process.GetCurrentProcess();
        long memoryBefore = GC.GetTotalMemory(true);
        int handlesBefore = process.HandleCount;
        var discoveryWatch = Stopwatch.StartNew();
        DiscoveryResult discovery = await new WindowsFileDiscoveryService().DiscoverAsync([new ScanRoot(root)], new DiscoveryPolicy());
        discoveryWatch.Stop();
        ExactDuplicateDetectionResult? detection = null;
        var analysisWatch = Stopwatch.StartNew();
        if (detect) detection = await ExactDuplicateDetector.DetectAsync(discovery.Files, new WindowsContentAnalysisService());
        analysisWatch.Stop();
        process.Refresh();
        return new Measurement(discovery.Files.Count, discovery.Files.Sum(file => file.Length), detection?.Groups.Count ?? 0,
            detection?.Groups.Sum(group => group.Files.Count) ?? 0, detection?.TotalReclaimableBytes ?? 0,
            discoveryWatch.ElapsedMilliseconds, detect ? analysisWatch.ElapsedMilliseconds : 0,
            GC.GetTotalMemory(true) - memoryBefore, process.HandleCount - handlesBefore, process.PeakWorkingSet64);
    }

    private static async Task<long> AssertCancellationAsync(string root)
    {
        using var cancellation = new CancellationTokenSource();
        var firstProgress = new CancellationProgress(cancellation);
        var watch = Stopwatch.StartNew();
        DiscoveryResult result = await new WindowsFileDiscoveryService().DiscoverAsync([new ScanRoot(root)], new DiscoveryPolicy(), firstProgress, cancellation.Token);
        watch.Stop();
        Assert.IsTrue(result.WasCancelled);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(30), watch.Elapsed);
        return watch.ElapsedMilliseconds;
    }

    private static async Task<int> AssertRepeatedScansAsync(string root, int cycles)
    {
        using Process process = Process.GetCurrentProcess();
        int baseline = process.HandleCount;
        for (int index = 0; index < cycles; index++)
        {
            ScanSessionResult result = await new ScanSessionService(new WindowsFileDiscoveryService(), new WindowsContentAnalysisService())
                .RunAsync([new ScanRoot(root)], new DiscoveryPolicy());
            Assert.AreEqual(ScanSessionState.Completed, result.State);
        }
        process.Refresh();
        Assert.IsLessThanOrEqualTo(baseline + 16, process.HandleCount, "Repeated scans accumulated OS handles.");
        return process.HandleCount - baseline;
    }

    private static void AssertResultsScale()
    {
        var groups = new List<DuplicateFileGroup>(5_000);
        ulong id = 1;
        for (int group = 0; group < 5_000; group++)
        {
            var files = new List<DiscoveredFile>(10);
            for (int member = 0; member < 10; member++) files.Add(File($"C:\\stress\\{group:D5}\\{member:D2}.bin", group + 1, id++));
            groups.Add(new DuplicateFileGroup(files, 9L * (group + 1)));
        }
        long reclaimable = groups.Sum(group => group.ReclaimableBytes);
        var result = new CompletedScanResult(new DiscoveryResult([], [], false), new ExactDuplicateDetectionResult(groups, [], reclaimable, false));
        var view = new ResultsReviewViewModel(result);
        view.SearchText = "05.bin";
        view.SortOption = ResultSortOption.Name;
        view.SortDescending = false;
        view.FilterOption = ResultFilterOption.AllGroups;
        foreach (ResultGroupViewModel group in view.AllGroups.Take(500)) group.Members[0].IsSelected = true;
        view.ExpandAll(); view.CollapseAll(); view.SearchText = string.Empty;
        Assert.AreEqual(50_000, view.VerifiedMemberCount);
        Assert.AreEqual(500, view.SelectedCandidateCount);
    }

    private static DiscoveredFile File(string path, long length, ulong id) => new(path, Path.GetFileName(path), ".bin", length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id, 0), FileAttributes.Normal);
    private sealed class CancellationProgress(CancellationTokenSource source) : IProgress<DiscoveryProgress> { public void Report(DiscoveryProgress value) => source.Cancel(); }
    private sealed record Measurement(int Files, long Bytes, int Groups, int Members, long ReclaimableBytes, long DiscoveryMs, long AnalysisMs, long ManagedBytes, int Handles, long PeakWorkingSet);

    private sealed class StressCorpus : IDisposable
    {
        public StressCorpus() { Root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Phase10", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public string Root { get; }
        public string UniqueRoot => Path.Combine(Root, "A-unique");
        public string NegativeRoot => Path.Combine(Root, "B-negatives");
        public string DuplicateRoot => Path.Combine(Root, "C-duplicates");
        public string LargeRoot => Path.Combine(Root, "D-large");
        public void CreateManyUnique(int count) { for (int i = 0; i < count; i++) Write(UniqueRoot, $"d{i % 100:D3}/u{i:D5}.bin", [(byte)i, (byte)(i >> 8), (byte)(i >> 16)]); }
        public void CreateSameSizeNegatives(int count) { for (int i = 0; i < count; i++) { byte[] bytes = new byte[512]; BitConverter.TryWriteBytes(bytes, i); Write(NegativeRoot, $"n{i % 40:D2}/n{i:D4}.bin", bytes); } }
        public void CreateDuplicateGroups(int count) { for (int i = 0; i < count; i++) { byte[] bytes = SHA256.HashData(BitConverter.GetBytes(i)); Write(DuplicateRoot, $"left/{i:D4}-one.bin", bytes); Write(DuplicateRoot, $"right/{i:D4}-renamed.bin", bytes); } }
        public void CreateLargeFiles() { WriteLarge("dup-one.bin", 32, 0x2A); WriteLarge("dup-two.bin", 32, 0x2A); WriteLarge("negative-one.bin", 32, 0x1B); WriteLarge("negative-two.bin", 32, 0x3C); }
        private void WriteLarge(string name, int mib, byte fill) { Directory.CreateDirectory(LargeRoot); byte[] block = new byte[1024 * 1024]; Array.Fill(block, fill); using var stream = new FileStream(Path.Combine(LargeRoot, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, block.Length, FileOptions.SequentialScan); for (int i = 0; i < mib; i++) stream.Write(block); }
        private static void Write(string root, string relative, byte[] bytes) { string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllBytes(path, bytes); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
