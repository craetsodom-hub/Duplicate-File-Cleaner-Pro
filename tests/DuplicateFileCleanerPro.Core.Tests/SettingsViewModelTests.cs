using DuplicateFileCleanerPro.App.Settings;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
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
    public void ViewModelAppliesOnlyRealChangesAndPersistsNoProductData()
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

    private sealed class MemoryStore : IAppSettingsStore
    {
        private readonly Dictionary<string, string> values = [];
        public IEnumerable<string> Keys => values.Keys;
        public string? Read(string key) => values.TryGetValue(key, out string? value) ? value : null;
        public void Write(string key, string value) => values[key] = value;
    }
}
