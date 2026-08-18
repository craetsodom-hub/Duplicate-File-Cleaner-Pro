using System.Reflection;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
    private static readonly string[] RecycleBinBoundarySource = ["WindowsShellRecycleBin.cs"];
    [TestMethod]
    public void CoreAssemblyDoesNotReferenceUiOrWindowsInfrastructure()
    {
        Assembly coreAssembly = Assembly.Load("DuplicateFileCleanerPro.Core");

        CollectionAssert.DoesNotContain(
            coreAssembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray(),
            "DuplicateFileCleanerPro.App");
        CollectionAssert.DoesNotContain(
            coreAssembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray(),
            "DuplicateFileCleanerPro.Infrastructure.Windows");
        CollectionAssert.DoesNotContain(
            coreAssembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray(),
            "Microsoft.UI.Xaml");
    }

    [TestMethod]
    public void SimilarPhotosSourcesAreIsolatedFromCleanupAndDestructiveApis()
    {
        _ = typeof(SimilarPhotoEngine);
        string root = FindRepositoryRoot();
        string[] files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Similarity{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.IsNotEmpty(files);
        string source = string.Join('\n', files.Select(File.ReadAllText));
        foreach (string forbidden in new[] { "CleanupEngine", "WindowsShellRecycleBin", "File.Delete", "File.Move", "Directory.Delete", "SHFileOperation", "Recycle" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SimilarRemovalIsDedicatedAndCannotInvokeExactCleanup()
    {
        string root = FindRepositoryRoot();
        string[] files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}SimilarRemoval{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("SimilarPhotoRemovalWorkflowViewModel.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.IsNotEmpty(files);
        string source = string.Join('\n', files.Select(File.ReadAllText));
        foreach (string forbidden in new[] { "CleanupEngine", "CleanupPlanner", "File.Delete", "File.Move", "Directory.Delete", "SHFileOperation" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        StringAssert.Contains(source, "SimilarPhotoRemovalEngine");
    }

    [TestMethod]
    public void FolderIntelligenceIsReadOnlyAndCannotInvokeCleanupBoundaries()
    {
        string root = FindRepositoryRoot();
        string[] files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}FolderIntelligence{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.IsNotEmpty(files);
        string source = string.Join('\n', files.Select(File.ReadAllText));
        foreach (string forbidden in new[] { "CleanupEngine", "CleanupPlanner", "WindowsShellRecycleBin", "File.Delete", "File.Move", "Directory.Delete", "SHFileOperation", "Recycle" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RecycleBinComBoundaryRemainsTheOnlyProductionShellDeletionBoundary()
    {
        string root = FindRepositoryRoot();
        string sourceRoot = Path.Combine(root, "src");
        string[] destructiveSources = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("DeleteItem(", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CollectionAssert.AreEquivalent(RecycleBinBoundarySource, destructiveSources);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DuplicateFileCleanerPro.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
