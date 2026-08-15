using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class PremiumScanConfigurationTests
{
    private static readonly string[] ExpectedBlendExtension = [".blend"];
    private static readonly string[] ExpectedProfileIds = ["all-files", "large-files", "photos-videos", "documents", "music"];
    private static readonly string[] ExpectedFolderExclusion = [@"C:\One"];
    private static readonly string[] ExpectedTemporaryExtension = [".tmp"];

    [TestMethod]
    [DataRow("report.PDF", ScanFileType.Documents)]
    [DataRow("portrait.JPEG", ScanFileType.Images)]
    [DataRow("song.flac", ScanFileType.Audio)]
    [DataRow("movie.mkv", ScanFileType.Video)]
    [DataRow("backup.7z", ScanFileType.Archives)]
    [DataRow("model.blend", ScanFileType.Other)]
    [DataRow("README", ScanFileType.Other)]
    public void FileTypeClassificationIsDeterministicAndCaseInsensitive(string fileName, ScanFileType expected)
    {
        Assert.AreEqual(expected, ScanCriteria.Classify(Path.GetExtension(fileName)));
    }

    [TestMethod]
    public void CustomExtensionsAreNormalizedAndExclusionsTakePrecedence()
    {
        var criteria = new ScanCriteria(ScanFileType.Documents, [" blend ", ".BLEND", "*.bad", "folder/name"]);

        CollectionAssert.AreEqual(ExpectedBlendExtension, criteria.CustomExtensions.ToArray());
        Assert.AreEqual(ScanCriteriaRejection.None, criteria.Evaluate(".blend", 10));
        Assert.AreEqual(ScanCriteriaRejection.ExtensionExcluded, criteria.Evaluate(".BLEND", 10, [".blend"]));
        Assert.AreEqual(ScanCriteriaRejection.FileTypeExcluded, criteria.Evaluate(".png", 10));
        Assert.AreEqual(ScanCriteriaRejection.None, criteria.Evaluate(".pdf", 10));
    }

    [TestMethod]
    public void InclusiveSizeBoundsRejectOnlyFilesOutsideTheConfiguredRange()
    {
        var criteria = new ScanCriteria(ScanFileType.All, minimumSizeBytes: 10, maximumSizeBytes: 20);

        Assert.AreEqual(ScanCriteriaRejection.BelowMinimumSize, criteria.Evaluate(".bin", 9));
        Assert.AreEqual(ScanCriteriaRejection.None, criteria.Evaluate(".bin", 10));
        Assert.AreEqual(ScanCriteriaRejection.None, criteria.Evaluate(".bin", 20));
        Assert.AreEqual(ScanCriteriaRejection.AboveMaximumSize, criteria.Evaluate(".bin", 21));
    }

    [TestMethod]
    public void InvalidCriteriaFailAtConstructionInsteadOfSilentlyChangingMeaning()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ScanCriteria((ScanFileType)1024));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ScanCriteria(ScanFileType.All, minimumSizeBytes: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ScanCriteria(ScanFileType.All, minimumSizeBytes: 20, maximumSizeBytes: 10));
    }

    [TestMethod]
    public void BuiltInPremiumProfilesHaveStableIdsAndExpectedCriteria()
    {
        CollectionAssert.AreEqual(
            ExpectedProfileIds,
            PremiumScanProfiles.BuiltIn.Select(profile => profile.Id).ToArray());
        Assert.AreEqual(100L * 1024 * 1024, PremiumScanProfiles.Find(PremiumScanProfiles.LargeFilesId)!.MinimumSizeBytes);
        Assert.AreEqual(
            ScanFileType.Images | ScanFileType.Video,
            PremiumScanProfiles.Find(PremiumScanProfiles.PhotosAndVideosId)!.FileTypes);
    }

    [TestMethod]
    public void DiscoveryPolicySnapshotsAndNormalizesReusableExtensionExclusions()
    {
        string[] mutableFolders = [@"C:\One", @"c:\one"];
        string[] mutableExtensions = ["TMP", ".tmp", "*.invalid"];
        var policy = new DiscoveryPolicy(
            IncludeSubfolders: false,
            Criteria: new ScanCriteria(ScanFileType.Images),
            ExcludedFolders: mutableFolders,
            ExcludedExtensions: mutableExtensions);
        mutableFolders[0] = @"C:\Changed";
        mutableExtensions[0] = ".changed";

        Assert.IsFalse(policy.IncludeSubfolders);
        CollectionAssert.AreEqual(ExpectedFolderExclusion, policy.ExcludedFolders.ToArray());
        CollectionAssert.AreEqual(ExpectedTemporaryExtension, policy.ExcludedExtensions.ToArray());
    }
}
