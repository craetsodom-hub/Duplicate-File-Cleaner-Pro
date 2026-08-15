using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class PremiumScanConfigurationIntegrationTests
{
    private static readonly string[] ExpectedPhotoFiles = ["portrait-original.jpg", "portrait-copy.JPG"];
    private static readonly string[] ExpectedTopLevelFiles = ["top-original.txt", "top-copy.txt"];
    private static readonly string[] ExpectedCustomFiles = ["scene-original.blend", "scene-copy.BLEND"];
    private static readonly string[] ExpectedSizedFiles = ["scratch-original.tmp", "scratch-copy.tmp"];

    [TestMethod]
    public async Task BuiltInPhotoProfileFiltersTheRealPipelineBeforeExactDetection()
    {
        using Phase14Corpus corpus = new();
        PremiumScanProfile profile = PremiumScanProfiles.Find(PremiumScanProfiles.PhotosAndVideosId)!;

        ScanSessionResult result = await RunAsync(
            corpus,
            new DiscoveryPolicy(Criteria: profile.CreateCriteria()));

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
        CollectionAssert.AreEquivalent(
            ExpectedPhotoFiles,
            result.CompletedResult.Discovery.Files.Select(file => file.FileName).ToArray());
        Assert.HasCount(1, result.CompletedResult.Detection.Groups);
        Assert.IsTrue(result.CompletedResult.Discovery.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.FileTypeExcluded));
    }

    [TestMethod]
    public async Task IncludeSubfoldersOffFindsOnlyTopLevelPairAcrossRealSources()
    {
        using Phase14Corpus corpus = new();

        ScanSessionResult result = await RunAsync(
            corpus,
            new DiscoveryPolicy(IncludeSubfolders: false));

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
        CollectionAssert.AreEquivalent(
            ExpectedTopLevelFiles,
            result.CompletedResult.Discovery.Files.Select(file => file.FileName).ToArray());
        Assert.HasCount(1, result.CompletedResult.Detection.Groups);
        Assert.IsTrue(result.CompletedResult.Discovery.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.SubfolderExcluded));
    }

    [TestMethod]
    public async Task CustomExtensionSizeAndReusableExclusionsComposeInDiscovery()
    {
        using Phase14Corpus corpus = new();
        var criteria = new ScanCriteria(ScanFileType.None, ["blend"], minimumSizeBytes: 20, maximumSizeBytes: 1024);
        var policy = new DiscoveryPolicy(
            Criteria: criteria,
            ExcludedFolders:
            [
                Path.Combine(corpus.FirstRoot, "excluded-folder"),
                Path.Combine(corpus.SecondRoot, "excluded-folder"),
            ],
            ExcludedExtensions: [".tmp"]);

        ScanSessionResult result = await RunAsync(corpus, policy);

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
        CollectionAssert.AreEquivalent(
            ExpectedCustomFiles,
            result.CompletedResult.Discovery.Files.Select(file => file.FileName).ToArray());
        Assert.HasCount(1, result.CompletedResult.Detection.Groups);
        Assert.IsGreaterThanOrEqualTo(result.CompletedResult.Discovery.SkippedItems.Count(item => item.Reason == DiscoverySkipReason.FolderExcluded), 2);
        Assert.IsTrue(result.CompletedResult.Discovery.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.ExtensionExcluded));
        Assert.IsTrue(result.CompletedResult.Discovery.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.FileTypeExcluded));
    }

    [TestMethod]
    public async Task ExcludedExtensionOverridesCustomIncludeInRealPipeline()
    {
        using Phase14Corpus corpus = new();
        var policy = new DiscoveryPolicy(
            Criteria: new ScanCriteria(ScanFileType.None, [".blend"]),
            ExcludedExtensions: ["BLEND"]);

        ScanSessionResult result = await RunAsync(corpus, policy);

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
        Assert.IsEmpty(result.CompletedResult.Discovery.Files);
        Assert.IsEmpty(result.CompletedResult.Detection.Groups);
        Assert.AreEqual(2, result.CompletedResult.Discovery.SkippedItems.Count(item => item.Reason == DiscoverySkipReason.ExtensionExcluded));
    }

    [TestMethod]
    public async Task InclusiveSizeRangeIsAppliedByRealDiscovery()
    {
        using Phase14Corpus corpus = new();
        var policy = new DiscoveryPolicy(
            Criteria: new ScanCriteria(ScanFileType.All, minimumSizeBytes: 40, maximumSizeBytes: 42));

        ScanSessionResult result = await RunAsync(corpus, policy);

        Assert.AreEqual(ScanSessionState.Completed, result.State);
        Assert.IsNotNull(result.CompletedResult);
        CollectionAssert.AreEquivalent(
            ExpectedSizedFiles,
            result.CompletedResult.Discovery.Files.Select(file => file.FileName).ToArray());
        Assert.HasCount(1, result.CompletedResult.Detection.Groups);
        Assert.IsTrue(result.CompletedResult.Discovery.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.BelowMinimumSize));
        Assert.IsTrue(result.CompletedResult.Discovery.SkippedItems.Any(item => item.Reason == DiscoverySkipReason.AboveMaximumSize));
    }

    [TestMethod]
    public void DriveCatalogReturnsOnlyReadyLocalStorageWithFactualCapacity()
    {
        IReadOnlyList<LocalDriveSource> drives = WindowsLocalDriveCatalog.GetAvailableDrives();

        Assert.IsNotEmpty(drives);
        Assert.IsTrue(drives.All(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram));
        Assert.IsTrue(drives.All(drive => Path.IsPathRooted(drive.RootPath) && drive.TotalSize > 0));
        Assert.IsTrue(drives.All(drive => drive.AvailableFreeSpace >= 0 && drive.AvailableFreeSpace <= drive.TotalSize));
    }

    private static async Task<ScanSessionResult> RunAsync(Phase14Corpus corpus, DiscoveryPolicy policy)
    {
        using var session = new ScanSessionService(new WindowsFileDiscoveryService(), new WindowsContentAnalysisService());
        return await session.RunAsync(
            [new ScanRoot(corpus.FirstRoot), new ScanRoot(corpus.SecondRoot)],
            policy);
    }

    private sealed class Phase14Corpus : IDisposable
    {
        public Phase14Corpus()
        {
            Root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Phase14", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(FindRepositoryRoot(), "tests", "DuplicateFileCleanerPro.IntegrationTests", "Corpus", "Phase14");
            CopyTree(source, Root);
            FirstRoot = Path.Combine(Root, "root-a");
            SecondRoot = Path.Combine(Root, "root-b");
        }

        public string Root { get; }
        public string FirstRoot { get; }
        public string SecondRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static void CopyTree(string source, string destination)
        {
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
            }
        }

        private static string FindRepositoryRoot()
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DuplicateFileCleanerPro.sln")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Repository root was not found.");
        }
    }
}
