using System.Reflection;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
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
}
