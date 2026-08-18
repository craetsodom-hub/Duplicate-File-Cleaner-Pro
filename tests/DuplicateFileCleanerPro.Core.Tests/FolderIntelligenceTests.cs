using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.FolderIntelligence;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class FolderIntelligenceTests
{
    [TestMethod]
    public async Task ExactFolderPairUsesRelativePathsAndVerifiedBytes()
    {
        using TempRoots roots = new();
        DiscoveredFile a = File(roots.A, "photos\\same.txt", 3, 1);
        DiscoveredFile b = File(roots.B, "photos\\same.txt", 3, 2);
        var discovery = new FakeDiscovery(new Dictionary<string, IReadOnlyList<DiscoveredFile>>(StringComparer.OrdinalIgnoreCase)
        {
            [roots.A] = [a], [roots.B] = [b],
        });
        var analysis = new FakeContentAnalysis(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [a.NormalizedPath] = [1, 2, 3], [b.NormalizedPath] = [1, 2, 3],
        });

        DuplicateFolderScanResult result = await new FolderIntelligenceService(discovery, analysis).FindDuplicateFoldersAsync([roots.A, roots.B], DiscoveryPolicyForAllFiles());

        Assert.IsFalse(result.WasCancelled);
        Assert.HasCount(1, result.Groups);
        CollectionAssert.AreEquivalent(new[] { roots.A, roots.B }, result.Groups[0].MemberFolders.Select(folder => folder.RootPath).ToArray());
        Assert.AreEqual(1, result.Groups[0].LogicalFileCount);
        Assert.AreEqual(3, result.Groups[0].PotentialReclaimableBytes);
    }

    [TestMethod]
    public async Task FilenameStructureAndSameSizeByteDifferencesDoNotQualify()
    {
        using TempRoots roots = new();
        DiscoveredFile a = File(roots.A, "same.txt", 4, 1);
        DiscoveredFile differentName = File(roots.B, "renamed.txt", 4, 2);
        DiscoveredFile differentBytes = File(roots.C, "same.txt", 4, 3);
        var discovery = new FakeDiscovery(new Dictionary<string, IReadOnlyList<DiscoveredFile>>(StringComparer.OrdinalIgnoreCase)
        {
            [roots.A] = [a], [roots.B] = [differentName], [roots.C] = [differentBytes],
        });
        var analysis = new FakeContentAnalysis(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [a.NormalizedPath] = [1, 2, 3, 4], [differentName.NormalizedPath] = [1, 2, 3, 4], [differentBytes.NormalizedPath] = [4, 3, 2, 1],
        });

        DuplicateFolderScanResult result = await new FolderIntelligenceService(discovery, analysis).FindDuplicateFoldersAsync([roots.A, roots.B, roots.C], DiscoveryPolicyForAllFiles());

        Assert.IsEmpty(result.Groups);
    }

    [TestMethod]
    public async Task HardLinkedMembersDoNotInflatePhysicalMetrics()
    {
        using TempRoots roots = new();
        DiscoveredFile first = File(roots.A, "file.bin", 5, 7);
        DiscoveredFile alias = File(roots.B, "file.bin", 5, 7);
        var service = new FolderIntelligenceService(
            new FakeDiscovery(new Dictionary<string, IReadOnlyList<DiscoveredFile>>(StringComparer.OrdinalIgnoreCase) { [roots.A] = [first], [roots.B] = [alias] }),
            new FakeContentAnalysis(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [first.NormalizedPath] = [1, 2, 3, 4, 5], [alias.NormalizedPath] = [1, 2, 3, 4, 5] }));

        DuplicateFolderScanResult result = await service.FindDuplicateFoldersAsync([roots.A, roots.B], DiscoveryPolicyForAllFiles());

        Assert.HasCount(1, result.Groups);
        Assert.AreEqual(1, result.Groups[0].IndependentPhysicalFileCount);
        Assert.AreEqual(0, result.Groups[0].PotentialReclaimableBytes);
    }

    [TestMethod]
    public async Task NestedEquivalentFoldersAreSuppressedWhenParentIsReported()
    {
        using TempRoots roots = new();
        string childA = Directory.CreateDirectory(Path.Combine(roots.A, "nested")).FullName;
        string childB = Directory.CreateDirectory(Path.Combine(roots.B, "nested")).FullName;
        DiscoveredFile parentA = File(roots.A, "root.txt", 2, 1);
        DiscoveredFile parentB = File(roots.B, "root.txt", 2, 2);
        DiscoveredFile nestedA = File(childA, "child.txt", 2, 3);
        DiscoveredFile nestedB = File(childB, "child.txt", 2, 4);
        var discovery = new FakeDiscovery(new Dictionary<string, IReadOnlyList<DiscoveredFile>>(StringComparer.OrdinalIgnoreCase)
        {
            [roots.A] = [parentA, nestedA], [roots.B] = [parentB, nestedB],
        });
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [parentA.NormalizedPath] = [1, 1], [parentB.NormalizedPath] = [1, 1], [nestedA.NormalizedPath] = [2, 2], [nestedB.NormalizedPath] = [2, 2],
        };

        DuplicateFolderScanResult result = await new FolderIntelligenceService(discovery, new FakeContentAnalysis(bytes)).FindDuplicateFoldersAsync([roots.A, roots.B], DiscoveryPolicyForAllFiles());

        Assert.HasCount(1, result.Groups);
        Assert.AreEqual(2, result.Groups[0].LogicalFileCount);
        Assert.IsTrue(result.Groups[0].MemberFolders.All(folder => folder.RootPath.EndsWith(Path.DirectorySeparatorChar + string.Empty, StringComparison.Ordinal) || folder.RootPath == roots.A || folder.RootPath == roots.B));
    }

    [TestMethod]
    public async Task OverlappingRootsAreCollapsedBeforeDiscovery()
    {
        using TempRoots roots = new();
        string nested = Directory.CreateDirectory(Path.Combine(roots.A, "nested")).FullName;
        var service = new FolderIntelligenceService(new FakeDiscovery(new Dictionary<string, IReadOnlyList<DiscoveredFile>>(StringComparer.OrdinalIgnoreCase)), new FakeContentAnalysis(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)));

        DuplicateFolderScanResult result = await service.FindDuplicateFoldersAsync([roots.A, nested], DiscoveryPolicyForAllFiles());

        Assert.HasCount(1, result.ScannedRoots);
        Assert.AreEqual(roots.A, result.ScannedRoots[0]);
    }

    [TestMethod]
    public async Task MasterComparisonClassifiesExactChangedUniqueAndMovedFiles()
    {
        using TempRoots roots = new();
        DiscoveredFile sameMaster = File(roots.A, "same.txt", 2, 1);
        DiscoveredFile changedMaster = File(roots.A, "changed.bin", 2, 2);
        DiscoveredFile masterOnly = File(roots.A, "master-only.txt", 1, 3);
        DiscoveredFile movedMaster = File(roots.A, "old\\moved.txt", 3, 4);
        DiscoveredFile sameTarget = File(roots.B, "same.txt", 2, 5);
        DiscoveredFile changedTarget = File(roots.B, "changed.bin", 2, 6);
        DiscoveredFile targetOnly = File(roots.B, "target-only.txt", 1, 7);
        DiscoveredFile movedTarget = File(roots.B, "renamed\\copy.txt", 3, 8);
        DiscoveredFile movedTargetAlias = File(roots.B, "another-copy.txt", 3, 9);
        var discovery = new FakeDiscovery(new Dictionary<string, IReadOnlyList<DiscoveredFile>>(StringComparer.OrdinalIgnoreCase)
        {
            [roots.A] = [sameMaster, changedMaster, masterOnly, movedMaster], [roots.B] = [sameTarget, changedTarget, targetOnly, movedTarget, movedTargetAlias],
        });
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [sameMaster.NormalizedPath] = [1, 2], [sameTarget.NormalizedPath] = [1, 2],
            [changedMaster.NormalizedPath] = [3, 4], [changedTarget.NormalizedPath] = [4, 3],
            [masterOnly.NormalizedPath] = [5], [movedMaster.NormalizedPath] = [6, 7, 8],
            [targetOnly.NormalizedPath] = [9], [movedTarget.NormalizedPath] = [6, 7, 8], [movedTargetAlias.NormalizedPath] = [6, 7, 8],
        };

        MasterFolderComparisonResult result = await new FolderIntelligenceService(discovery, new FakeContentAnalysis(bytes)).CompareMasterFolderAsync(roots.A, [roots.B], DiscoveryPolicyForAllFiles());
        FolderComparisonTargetResult target = result.Targets.Single();

        Assert.AreEqual(1, target.Summary.Identical);
        Assert.AreEqual(1, target.Summary.Different);
        Assert.AreEqual(1, target.Summary.OnlyInMaster);
        Assert.AreEqual(1, target.Summary.OnlyInCompared);
        Assert.AreEqual(1, target.Summary.MovedRenamedExactMatches);
        Assert.HasCount(2, target.Rows.Single(row => row.Status == FolderComparisonStatus.MovedRenamedExactMatch).ComparedFiles);
    }

    [TestMethod]
    public async Task CancellationDoesNotPublishFolderGroups()
    {
        using TempRoots roots = new();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        DuplicateFolderScanResult result = await new FolderIntelligenceService(new FakeDiscovery(new Dictionary<string, IReadOnlyList<DiscoveredFile>>(StringComparer.OrdinalIgnoreCase)), new FakeContentAnalysis(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase))).FindDuplicateFoldersAsync([roots.A, roots.B], DiscoveryPolicyForAllFiles(), cancellationToken: cancellation.Token);

        Assert.IsTrue(result.WasCancelled);
        Assert.IsEmpty(result.Groups);
    }

    [TestMethod]
    public void ExportsEscapeUnicodeAndCommasWithoutCryptographicInternals()
    {
        var row = new FolderComparisonRow(
            "C:\\Target,One",
            "子\\file,1.txt",
            FolderComparisonStatus.MovedRenamedExactMatch,
            null,
            new List<FolderFileEntry>(),
            null,
            null,
            null,
            null);
        var result = new FolderComparisonTargetResult("C:\\Master", "C:\\Target,One", new(1, 1, 0, 0, 1, 0, 0, 0), new[] { row }, Array.Empty<SkippedDiscoveryItem>());

        string csv = FolderIntelligenceExporter.CreateComparisonCsv(result);

        StringAssert.Contains(csv, "\"C:\\Target,One\"");
        StringAssert.Contains(csv, "\"子\\file,1.txt\"");
        Assert.DoesNotContain("SHA", csv, StringComparison.OrdinalIgnoreCase);
    }

    private static DiscoveryPolicy DiscoveryPolicyForAllFiles() => new(IncludeSubfolders: true, Criteria: ScanCriteria.AllFiles);

    private static DiscoveredFile File(string root, string relativePath, long length, ulong id) =>
        new(Path.Combine(root, relativePath), Path.GetFileName(relativePath), Path.GetExtension(relativePath), length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, id, 0), FileAttributes.Normal);

    private sealed class TempRoots : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro-Phase19-" + Guid.NewGuid().ToString("N"));
        public TempRoots()
        {
            A = Directory.CreateDirectory(Path.Combine(path, "Alpha")).FullName;
            B = Directory.CreateDirectory(Path.Combine(path, "Beta")).FullName;
            C = Directory.CreateDirectory(Path.Combine(path, "Gamma")).FullName;
        }
        public string A { get; }
        public string B { get; }
        public string C { get; }
        public void Dispose() => Directory.Delete(path, true);
    }

    private sealed class FakeDiscovery(IReadOnlyDictionary<string, IReadOnlyList<DiscoveredFile>> files) : IFileDiscoveryService
    {
        public Task<DiscoveryResult> DiscoverAsync(IEnumerable<ScanRoot> roots, DiscoveryPolicy policy, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = roots.SelectMany(root => files.TryGetValue(root.NormalizedPath, out IReadOnlyList<DiscoveredFile>? value) ? value : []).ToArray();
            return Task.FromResult(new DiscoveryResult(values, [], false));
        }
    }

    private sealed class FakeContentAnalysis(IReadOnlyDictionary<string, byte[]> bytes) : IContentAnalysisService
    {
        public Task<ContentHashOutcome> HashAsync(DiscoveredFile file, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(bytes.TryGetValue(file.NormalizedPath, out byte[]? value)
                ? ContentHashOutcome.Success(new ContentDigest(System.Security.Cryptography.SHA256.HashData(value)))
                : ContentHashOutcome.Failure(ContentAnalysisFailureReason.Unavailable));
        }

        public Task<ContentComparisonOutcome> CompareAsync(DiscoveredFile left, DiscoveredFile right, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(bytes.TryGetValue(left.NormalizedPath, out byte[]? leftBytes) && bytes.TryGetValue(right.NormalizedPath, out byte[]? rightBytes)
                ? leftBytes.SequenceEqual(rightBytes) ? ContentComparisonOutcome.Equal() : ContentComparisonOutcome.Different()
                : ContentComparisonOutcome.Failure(ContentAnalysisFailureReason.Unavailable));
        }

        public Task<ContentValidationOutcome> ValidateAsync(DiscoveredFile file, CancellationToken cancellationToken = default) =>
            Task.FromResult(bytes.ContainsKey(file.NormalizedPath) ? ContentValidationOutcome.Valid() : ContentValidationOutcome.Failure(ContentAnalysisFailureReason.Unavailable));
    }
}
