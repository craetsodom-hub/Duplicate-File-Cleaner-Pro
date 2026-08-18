using System.Text;
using System.Runtime.InteropServices;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Core.SimilarRemoval;
using DuplicateFileCleanerPro.Core.Similarity;
using DuplicateFileCleanerPro.Infrastructure.Windows.Cleanup;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;
using DuplicateFileCleanerPro.Infrastructure.Windows.SimilarRemoval;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class SimilarPhotoRemovalIntegrationTests
{
    [TestMethod]
    [TestCategory("SimilarPhotoRemovalRealRecycleBin")]
    public async Task ExplicitlyReviewedPhotosMoveToRealRecycleBinAndIndependentKeeperSurvives()
    {
        using var corpus = new RemovalCorpus("real-recycle");
        DiscoveredFile keeper = corpus.Write("keeper.png", "visually related keeper");
        DiscoveredFile first = corpus.Write("variant-bright.png", "different bright variant");
        DiscoveredFile second = corpus.Write("variant-crop.png", "different cropped variant");
        SimilarPhotoRemovalPlan plan = Plan([Group(keeper, first, second)], first.PhysicalIdentity, second.PhysicalIdentity);

        SimilarPhotoRemovalResult result = await new SimilarPhotoRemovalEngine(new WindowsSimilarPhotoRemovalPlatform()).ExecuteAsync(plan);

        Assert.AreEqual(2, result.RecycledPhotoCount);
        Assert.AreEqual(0, result.SkippedPhotoCount);
        Assert.AreEqual(0, result.FailedPhotoCount);
        Assert.IsTrue(File.Exists(keeper.NormalizedPath));
        Assert.IsFalse(File.Exists(first.NormalizedPath));
        Assert.IsFalse(File.Exists(second.NormalizedPath));
    }

    [TestMethod]
    public async Task KeeperReplacementBetweenPlanAndExecutionSkipsCandidate()
    {
        using var corpus = new RemovalCorpus("keeper-replaced");
        DiscoveredFile keeper = corpus.Write("keeper.png", "keeper");
        DiscoveredFile candidate = corpus.Write("candidate.png", "candidate differs");
        SimilarPhotoRemovalPlan plan = Plan([Group(keeper, candidate)], candidate.PhysicalIdentity);
        var recycle = new RecordingRecycleBin();
        var observer = new Observer((_, _) => RemovalCorpus.Replace(keeper.NormalizedPath, "replacement keeper"));

        SimilarPhotoRemovalResult result = await new SimilarPhotoRemovalEngine(new WindowsSimilarPhotoRemovalPlatform(recycle, observer)).ExecuteAsync(plan);

        Assert.AreEqual(SimilarPhotoRemovalOutcomeStatus.SkippedSurvivorUnavailable, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycle.CallCount);
        Assert.IsTrue(File.Exists(candidate.NormalizedPath));
    }

    [TestMethod]
    public async Task CandidateReplacementAtFinalRaceBoundaryIsSkipped()
    {
        using var corpus = new RemovalCorpus("candidate-replaced");
        DiscoveredFile keeper = corpus.Write("keeper.png", "keeper");
        DiscoveredFile candidate = corpus.Write("candidate.png", "candidate differs");
        SimilarPhotoRemovalPlan plan = Plan([Group(keeper, candidate)], candidate.PhysicalIdentity);
        var recycle = new RecordingRecycleBin();
        var observer = new Observer((_, _) => RemovalCorpus.Replace(candidate.NormalizedPath, "replacement candidate"));

        SimilarPhotoRemovalResult result = await new SimilarPhotoRemovalEngine(new WindowsSimilarPhotoRemovalPlatform(recycle, observer)).ExecuteAsync(plan);

        Assert.AreEqual(SimilarPhotoRemovalOutcomeStatus.SkippedIdentityMismatch, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycle.CallCount);
        Assert.IsTrue(File.Exists(candidate.NormalizedPath));
    }

    [TestMethod]
    public async Task CandidateAndKeeperDisappearanceProduceIndependentFactualOutcomes()
    {
        using var corpus = new RemovalCorpus("multiple-groups");
        DiscoveredFile keepA = corpus.Write("keep-a.png", "a");
        DiscoveredFile removeA = corpus.Write("remove-a.png", "aa");
        DiscoveredFile keepB = corpus.Write("keep-b.png", "b");
        DiscoveredFile removeB = corpus.Write("remove-b.png", "bb");
        SimilarPhotoRemovalPlan plan = Plan([Group(keepA, removeA), Group(keepB, removeB)], removeA.PhysicalIdentity, removeB.PhysicalIdentity);
        File.Delete(removeA.NormalizedPath);
        File.Delete(keepB.NormalizedPath);
        var recycle = new RecordingRecycleBin();

        SimilarPhotoRemovalResult result = await new SimilarPhotoRemovalEngine(new WindowsSimilarPhotoRemovalPlatform(recycle)).ExecuteAsync(plan);

        Assert.AreEqual(SimilarPhotoRemovalOutcomeStatus.SkippedMissing, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(SimilarPhotoRemovalOutcomeStatus.SkippedSurvivorUnavailable, result.Groups[1].Outcomes[0].Status);
        Assert.AreEqual(0, recycle.CallCount);
    }

    [TestMethod]
    public async Task HardLinkCreatedAfterReviewMakesCandidateAmbiguousAndSkipsIt()
    {
        using var corpus = new RemovalCorpus("hard-link-change");
        DiscoveredFile keeper = corpus.Write("keeper.png", "keeper");
        DiscoveredFile candidate = corpus.Write("candidate.png", "candidate differs");
        SimilarPhotoRemovalPlan plan = Plan([Group(keeper, candidate)], candidate.PhysicalIdentity);
        Assert.IsTrue(CreateHardLink(Path.Combine(corpus.Root, "candidate-alias.png"), candidate.NormalizedPath, IntPtr.Zero));
        var recycle = new RecordingRecycleBin();

        SimilarPhotoRemovalResult result = await new SimilarPhotoRemovalEngine(new WindowsSimilarPhotoRemovalPlatform(recycle)).ExecuteAsync(plan);

        Assert.AreEqual(SimilarPhotoRemovalOutcomeStatus.SkippedAmbiguousHardLinks, result.Groups[0].Outcomes[0].Status);
        Assert.AreEqual(0, recycle.CallCount);
    }

    private static SimilarPhotoRemovalPlan Plan(IEnumerable<SimilarPhotoGroup> groups, params PhysicalFileIdentity[] selected)
    {
        SimilarPhotoGroup[] snapshot = groups.ToArray();
        DiscoveredFile[] files = snapshot.SelectMany(group => group.Photos).ToArray();
        var completed = new CompletedSimilarPhotoScanResult(
            new DiscoveryResult(files, [], false),
            new SimilarPhotoAnalysisResult(snapshot, [], [], files.Length, 0, 0, false),
            SimilarPhotoSensitivity.Balanced,
            [Path.GetDirectoryName(files[0].NormalizedPath)!]);
        return SimilarPhotoRemovalPlanner.CreatePlan(new(completed, selected)).Plan!;
    }

    private static SimilarPhotoGroup Group(params DiscoveredFile[] files) => new(files[0], files, SimilarityTier.Similar);

    private sealed class RemovalCorpus : IDisposable
    {
        public RemovalCorpus(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro.Phase18", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public DiscoveredFile Write(string name, string content)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            Assert.IsTrue(WindowsFileInspector.TryInspect(path, out WindowsFileInspector.FileSnapshot? snapshot));
            Assert.IsNotNull(snapshot);
            return new(path, name, Path.GetExtension(name), snapshot.Length, snapshot.LastWriteTimeUtc, snapshot.ChangeTimeUtc, snapshot.Identity, snapshot.Attributes);
        }

        public static void Replace(string path, string content)
        {
            string displaced = path + ".analyzed";
            File.Move(path, displaced);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class RecordingRecycleBin : IWindowsRecycleBin
    {
        public int CallCount { get; private set; }
        public Task<WindowsRecycleBinResult> RecycleAsync(string absolutePath, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new WindowsRecycleBinResult(true));
        }
    }

    private sealed class Observer(Action<SimilarPhotoRemovalPlanMember, IReadOnlyList<SimilarPhotoRemovalPlanMember>> action)
        : ISimilarPhotoRemovalExecutionObserver
    {
        public void BeforeFinalRecycleValidation(SimilarPhotoRemovalPlanMember candidate, IReadOnlyList<SimilarPhotoRemovalPlanMember> survivors) => action(candidate, survivors);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
