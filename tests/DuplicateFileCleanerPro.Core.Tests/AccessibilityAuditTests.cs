using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.App.Accessibility;
using DuplicateFileCleanerPro.Core.Detection;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class AccessibilityAuditTests
{
    [TestMethod]
    public void ImportantInteractiveControlsHaveLocalizedAccessibilityResources()
    {
        string root = FindRepositoryRoot();
        string resources = System.IO.File.ReadAllText(Path.Combine(root, "src", "DuplicateFileCleanerPro.App", "Strings", "en-US", "Resources.resw"));
        string xaml = System.IO.File.ReadAllText(Path.Combine(root, "src", "DuplicateFileCleanerPro.App", "MainWindow.xaml"));
        string codeBehind = System.IO.File.ReadAllText(Path.Combine(root, "src", "DuplicateFileCleanerPro.App", "MainWindow.xaml.cs"));

        foreach (string key in new[]
        {
            "ResultsSearchBox.AutomationProperties.Name",
            "ResultsDescendingButton.AutomationProperties.Name",
            "ConfirmCleanupButton.AutomationProperties.Name",
            "CancelCleanupButton.AutomationProperties.Name",
            "AppearanceComboBox.AutomationProperties.Name",
            "ResultsSelectionNotice.Message",
            "ProfileComboBox.AutomationProperties.Name",
            "LocationsList.AutomationProperties.Name",
            "AvailableDrivesComboBox.AutomationProperties.Name",
            "IncludeSubfoldersToggle.AutomationProperties.Name",
            "CustomExtensionTextBox.AutomationProperties.Name",
            "MinimumSizeNumberBox.AutomationProperties.Name",
            "ExcludedFoldersList.AutomationProperties.Name",
            "ExcludedExtensionTextBox.AutomationProperties.Name",
        })
        {
            StringAssert.Contains(resources, $"name=\"{key}\"");
        }

        StringAssert.Contains(xaml, "AutomationProperties.LiveSetting=\"Polite\"");
        StringAssert.Contains(xaml, "x:Name=\"ResultsSelectionNotice\"");
        StringAssert.Contains(xaml, "x:Name=\"ScanSetupNotice\"");
        StringAssert.Contains(xaml, "x:Name=\"ProfileComboBox\"");
        StringAssert.Contains(xaml, "x:Name=\"IncludeSubfoldersToggle\"");
        StringAssert.Contains(xaml, "AutomationProperties.HeadingLevel=\"Level2\"");
        StringAssert.Contains(codeBehind, "DefaultButton = ContentDialogButton.Close");
        StringAssert.Contains(codeBehind, "AccessibilitySettings().HighContrast");
        StringAssert.Contains(codeBehind, "AutomationProperties.SetLiveSetting(CleanupActivityText");
    }

    [TestMethod]
    public void PreventedFinalSelectionRaisesAccessibleFeedbackSignal()
    {
        DiscoveredFile first = CreateFile("C:\\Accessible\\one.txt", 7, 1);
        DiscoveredFile second = CreateFile("C:\\Accessible\\two.txt", 7, 2);
        var group = new DuplicateFileGroup([first, second], 7);
        var result = new CompletedScanResult(
            new DiscoveryResult([], [], false),
            new ExactDuplicateDetectionResult([group], [], 7, false));
        ResultsReviewViewModel viewModel = new(result);
        int rejected = 0;
        viewModel.SelectionRejected += (_, _) => rejected++;

        viewModel.AllGroups[0].Members[0].IsSelected = true;
        viewModel.AllGroups[0].Members[1].IsSelected = true;

        Assert.AreEqual(1, rejected);
        Assert.AreEqual(1, viewModel.SelectedCandidateCount);
        Assert.AreEqual("one.txt", viewModel.AllGroups[0].Members[0].AccessibleName);
        Assert.AreEqual("C:\\Accessible\\one.txt", viewModel.AllGroups[0].Members[0].AccessiblePath);
    }

    [TestMethod]
    public void OperationAnnouncementGateSuppressesRepeatedProgressButAnnouncesStageChanges()
    {
        var gate = new OperationAnnouncementGate<string>();
        Assert.IsTrue(gate.ShouldAnnounce("Discovering files"));
        Assert.IsFalse(gate.ShouldAnnounce("Discovering files"));
        Assert.IsTrue(gate.ShouldAnnounce("Analyzing exact duplicates"));
        gate.Reset();
        Assert.IsTrue(gate.ShouldAnnounce("Discovering files"));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "DuplicateFileCleanerPro.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static DiscoveredFile CreateFile(string path, long length, ulong identity) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path), length, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new PhysicalFileIdentity(1, identity, 0), FileAttributes.Normal);
}
