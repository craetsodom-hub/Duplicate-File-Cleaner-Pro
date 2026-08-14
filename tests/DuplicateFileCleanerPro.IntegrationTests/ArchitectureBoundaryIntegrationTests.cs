using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

namespace DuplicateFileCleanerPro.IntegrationTests;

[TestClass]
public sealed class ArchitectureBoundaryIntegrationTests
{
    [TestMethod]
    public void WindowsInfrastructureReferencesCoreButNotAppOrWinUi()
    {
        string[] references = typeof(WindowsFileDiscoveryService).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        CollectionAssert.Contains(references, "DuplicateFileCleanerPro.Core");
        CollectionAssert.DoesNotContain(references, "DuplicateFileCleanerPro.App");
        CollectionAssert.DoesNotContain(references, "Microsoft.UI.Xaml");
    }
}
