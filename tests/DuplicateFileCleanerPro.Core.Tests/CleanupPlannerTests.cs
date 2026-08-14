using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class CleanupPlannerTests
{
    [TestMethod]
    public void ValidSelectionCreatesImmutableCandidateAndKeeperSets()
    {
        CompletedScanResult snapshot = Result(Group(File("a.bin", 10, 1), File("b.bin", 10, 2), File("c.bin", 10, 3)));
        PhysicalFileIdentity[] selected = [Id(2), Id(3)];

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(snapshot, selected));
        selected[0] = Id(99);

        Assert.IsTrue(planning.Succeeded);
        Assert.IsNotNull(planning.Plan);
        Assert.HasCount(1, planning.Plan.Groups);
        Assert.HasCount(2, planning.Plan.Groups[0].Candidates);
        Assert.HasCount(1, planning.Plan.Groups[0].Keepers);
        Assert.AreEqual(Id(1), planning.Plan.Groups[0].Keepers[0].ExpectedFile.PhysicalIdentity);
    }

    [TestMethod]
    public void AllMembersSelectedIsRejected()
    {
        CompletedScanResult snapshot = Result(Group(File("a.bin", 10, 1), File("b.bin", 10, 2)));

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(snapshot, [Id(1), Id(2)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.AllMembersSelected));
    }

    [TestMethod]
    public void UnknownSelectionIsRejected()
    {
        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(
            Result(Group(File("a.bin", 1, 1), File("b.bin", 1, 2))),
            [Id(9)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.UnknownSelectedIdentity));
    }

    [TestMethod]
    public void OverlappingPhysicalIdentityAcrossGroupsIsRejected()
    {
        CompletedScanResult snapshot = Result(
            Group(File("a.bin", 1, 1), File("b.bin", 1, 2)),
            Group(File("c.bin", 1, 2), File("d.bin", 1, 3)));

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(snapshot, [Id(1)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.DuplicatePhysicalIdentity));
    }

    [TestMethod]
    public void HardLinkAliasesCannotBecomeIndependentMembers()
    {
        CompletedScanResult malformed = Result(Group(File("alias-a.bin", 4, 1), File("alias-b.bin", 4, 1), File("copy.bin", 4, 2)));

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(malformed, [Id(2)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.DuplicatePhysicalIdentity));
    }

    [TestMethod]
    public void DuplicatePathWithDifferentIdentityIsRejected()
    {
        DiscoveredFile first = File("same.bin", 4, 1);
        DiscoveredFile second = first with { PhysicalIdentity = Id(2) };

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(Result(Group(first, second)), [Id(2)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.DuplicatePath));
    }

    [TestMethod]
    public void NetworkPathInjectionIsRejectedBelowTheUi()
    {
        DiscoveredFile remote = File("a.bin", 4, 1) with { NormalizedPath = @"\\server\share\a.bin" };
        DiscoveredFile local = File("b.bin", 4, 2);

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(Result(Group(remote, local)), [Id(1)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.UnsupportedPath));
    }

    [TestMethod]
    [DataRow(FileAttributes.ReparsePoint)]
    [DataRow(FileAttributes.Offline)]
    [DataRow(FileAttributes.System)]
    [DataRow(FileAttributes.Hidden)]
    [DataRow(FileAttributes.Encrypted)]
    [DataRow((FileAttributes)0x00040000)]
    [DataRow((FileAttributes)0x00400000)]
    public void UnsafeOrCloudSnapshotAttributesAreRejectedBelowTheUi(FileAttributes unsafeAttribute)
    {
        DiscoveredFile unsafeFile = File("unsafe.bin", 4, 1) with { Attributes = unsafeAttribute };
        DiscoveredFile local = File("local.bin", 4, 2);

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(Result(Group(unsafeFile, local)), [Id(1)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.InvalidVerifiedGroup));
    }

    [TestMethod]
    public void CancelledOrArithmeticallyCorruptSnapshotIsRejected()
    {
        DuplicateFileGroup group = Group(File("a.bin", 4, 1), File("b.bin", 4, 2));
        var cancelled = new CompletedScanResult(
            new DiscoveryResult([], [], true),
            new ExactDuplicateDetectionResult([group], [], group.ReclaimableBytes, false));
        var corrupt = new CompletedScanResult(
            new DiscoveryResult([], [], false),
            new ExactDuplicateDetectionResult([group with { ReclaimableBytes = 99 }], [], 99, false));

        Assert.IsFalse(CleanupPlanner.CreatePlan(new CleanupSelectionIntent(cancelled, [Id(2)])).Succeeded);
        Assert.IsFalse(CleanupPlanner.CreatePlan(new CleanupSelectionIntent(corrupt, [Id(2)])).Succeeded);
    }

    [TestMethod]
    public void DetectionMemberAbsentFromDiscoverySnapshotIsRejected()
    {
        DiscoveredFile first = File("a.bin", 4, 1);
        DiscoveredFile second = File("b.bin", 4, 2);
        DuplicateFileGroup group = Group(first, second);
        var mismatched = new CompletedScanResult(
            new DiscoveryResult([first], [], false),
            new ExactDuplicateDetectionResult([group], [], group.ReclaimableBytes, false));

        CleanupPlanningResult planning = CleanupPlanner.CreatePlan(new CleanupSelectionIntent(mismatched, [Id(2)]));

        Assert.IsFalse(planning.Succeeded);
        Assert.IsTrue(planning.Issues.Any(issue => issue.Reason == CleanupPlanningIssueReason.InvalidVerifiedGroup));
    }

    internal static CleanupPlan Plan(params DuplicateFileGroup[] groups)
    {
        CompletedScanResult snapshot = Result(groups);
        PhysicalFileIdentity[] selected = groups.SelectMany(group => group.Files.Skip(1)).Select(file => file.PhysicalIdentity).ToArray();
        return CleanupPlanner.CreatePlan(new CleanupSelectionIntent(snapshot, selected)).Plan!;
    }

    internal static CompletedScanResult Result(params DuplicateFileGroup[] groups)
    {
        long reclaimable = groups.Aggregate(0L, (total, group) => checked(total + group.ReclaimableBytes));
        DiscoveredFile[] discoveredFiles = groups.SelectMany(group => group.Files).ToArray();
        return new CompletedScanResult(new DiscoveryResult(discoveredFiles, [], false), new ExactDuplicateDetectionResult(groups, [], reclaimable, false));
    }

    internal static DuplicateFileGroup Group(params DiscoveredFile[] files) =>
        new(Array.AsReadOnly(files), checked((files.Length - 1L) * files[0].Length));

    internal static DiscoveredFile File(string name, long length, ulong id) => new(
        $@"C:\CleanupTests\{id:D4}\{name}",
        name,
        Path.GetExtension(name),
        length,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        Id(id),
        FileAttributes.Normal);

    internal static PhysicalFileIdentity Id(ulong id) => new(1, id, 0);
}
