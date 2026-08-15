namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class LocalizationResourceAuditTests
{
    [TestMethod]
    public void ImportantProductionSurfacesUseSemanticResourceKeys()
    {
        string root = FindRepositoryRoot();
        string resources = File.ReadAllText(Path.Combine(root, "src", "DuplicateFileCleanerPro.App", "Strings", "en-US", "Resources.resw"));
        string xaml = File.ReadAllText(Path.Combine(root, "src", "DuplicateFileCleanerPro.App", "MainWindow.xaml"));

        foreach (string key in new[]
        {
            "ScanSetupTitle.Text", "ResultsTitle.Text", "CleanupReviewTitle", "SettingsAppearanceTitle.Text",
            "SettingsPrivacyTitle.Text", "SettingsSafetyTitle.Text", "SettingsAboutTitle.Text",
        })
        {
            StringAssert.Contains(resources, $"name=\"{key}\"");
        }

        foreach (string uid in new[] { "SettingsAppearanceTitle", "SettingsPrivacyTitle", "SettingsSafetyTitle", "SettingsAboutTitle" })
        {
            StringAssert.Contains(xaml, $"x:Uid=\"{uid}\"");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DuplicateFileCleanerPro.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
