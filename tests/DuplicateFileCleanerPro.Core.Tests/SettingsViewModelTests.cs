using DuplicateFileCleanerPro.App.Settings;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
    private static readonly string[] ExpectedRawExtension = [".rawx"];
    private static readonly string[] ExpectedPictureSource = [@"C:\Pictures"];
    private static readonly string[] ExpectedTemporaryExtension = [".tmp"];
    private static readonly string[] ExpectedScanSetupKey = [AppSettingsService.ScanSetupKey];
    private static readonly string[] ExpectedOkayExtension = [".ok"];

    [TestMethod]
    public void MissingOrMalformedAppearanceFallsBackToSystem()
    {
        var values = new MemoryStore();
        Assert.AreEqual(AppearancePreference.System, new AppSettingsService(values).LoadAppearance());
        values.Write(AppSettingsService.AppearanceKey, "unexpected-value");
        Assert.AreEqual(AppearancePreference.System, new AppSettingsService(values).LoadAppearance());
    }

    [TestMethod]
    public void AppearancePersistsAndResetToSystemIsARealPreferenceChange()
    {
        var values = new MemoryStore();
        var service = new AppSettingsService(values);
        service.SaveAppearance(AppearancePreference.Light);
        Assert.AreEqual(AppearancePreference.Light, new AppSettingsService(values).LoadAppearance());
        service.SaveAppearance(AppearancePreference.Dark);
        Assert.AreEqual(AppearancePreference.Dark, new AppSettingsService(values).LoadAppearance());
        service.SaveAppearance(AppearancePreference.System);
        Assert.AreEqual(AppearancePreference.System, new AppSettingsService(values).LoadAppearance());
    }

    [TestMethod]
    public void AppearanceViewModelAppliesOnlyRealChanges()
    {
        var values = new MemoryStore();
        var applied = new List<AppearancePreference>();
        var viewModel = new SettingsViewModel(new AppSettingsService(values), applied.Add);

        Assert.IsFalse(viewModel.SetAppearance(AppearancePreference.System));
        Assert.IsTrue(viewModel.SetAppearance(AppearancePreference.Dark));
        Assert.IsFalse(viewModel.SetAppearance(AppearancePreference.Dark));
        Assert.AreEqual(AppearancePreference.Dark, viewModel.Appearance);
        CollectionAssert.AreEqual(new[] { AppearancePreference.Dark }, applied);
        CollectionAssert.AreEquivalent(new[] { AppSettingsService.AppearanceKey }, values.Keys.ToArray());
    }

    [TestMethod]
    public void VersionFormattingUsesPackageComponentsWithoutHardcodedProductVersion()
    {
        Assert.AreEqual("2.5.7.11", AppVersionFormatter.Format(2, 5, 7, 11));
    }

    [TestMethod]
    public void PremiumScanSetupRoundTripsWithoutPersistingResultsOrCleanupHistory()
    {
        var values = new MemoryStore();
        var service = new AppSettingsService(values);
        var saved = new SavedScanProfile(
            "saved:photos",
            "Photo pass",
            true,
            ScanFileType.Images,
            [".rawx"],
            1024,
            4096,
            [@"C:\Cache"],
            [".tmp"]);
        var setup = new ScanSetupSettings(
            saved.Id,
            false,
            ScanFileType.Images | ScanFileType.Video,
            ["RAWX", ".rawx"],
            10,
            100,
            [@"C:\Pictures", @"c:\pictures"],
            [@"C:\Pictures\Exports"],
            ["tmp", ".TMP"],
            [saved]);

        service.SaveScanSetup(setup);
        ScanSetupSettings loaded = new AppSettingsService(values).LoadScanSetup();

        Assert.AreEqual(saved.Id, loaded.ActiveProfileId);
        Assert.IsFalse(loaded.IncludeSubfolders);
        Assert.AreEqual(ScanFileType.Images | ScanFileType.Video, loaded.FileTypes);
        CollectionAssert.AreEqual(ExpectedRawExtension, loaded.CustomExtensions.ToArray());
        CollectionAssert.AreEqual(ExpectedPictureSource, loaded.Sources.ToArray());
        CollectionAssert.AreEqual(ExpectedTemporaryExtension, loaded.ExcludedExtensions.ToArray());
        Assert.HasCount(1, loaded.SavedProfiles);
        CollectionAssert.AreEquivalent(
            ExpectedScanSetupKey,
            values.Keys.ToArray());
    }

    [TestMethod]
    public void CorruptOrUntrustedPersistedScanSetupFallsBackAndNormalizesFailClosed()
    {
        var values = new MemoryStore();
        values.Write(AppSettingsService.ScanSetupKey, "{not-json");
        Assert.AreEqual(ScanSetupSettings.Default, new AppSettingsService(values).LoadScanSetup());

        var untrusted = new ScanSetupSettings(
            "unknown-profile",
            true,
            (ScanFileType)int.MaxValue,
            ["*.bad", ".OK"],
            -10,
            -20,
            Enumerable.Range(0, 80).Select(index => $@"C:\Root{index}").ToArray(),
            [],
            [],
            []);
        ScanSetupSettings normalized = AppSettingsService.Normalize(untrusted);

        Assert.AreEqual(PremiumScanProfiles.CustomId, normalized.ActiveProfileId);
        Assert.AreEqual(ScanFileType.All, normalized.FileTypes);
        Assert.AreEqual(0, normalized.MinimumSizeBytes);
        Assert.IsNull(normalized.MaximumSizeBytes);
        Assert.HasCount(64, normalized.Sources);
        CollectionAssert.AreEqual(ExpectedOkayExtension, normalized.CustomExtensions.ToArray());
    }

    private sealed class MemoryStore : IAppSettingsStore
    {
        private readonly Dictionary<string, string> values = [];
        public IEnumerable<string> Keys => values.Keys;
        public string? Read(string key) => values.TryGetValue(key, out string? value) ? value : null;
        public void Write(string key, string value) => values[key] = value;
    }
}
