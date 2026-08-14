using DuplicateFileCleanerPro.Core.Models;
using Xunit;

namespace DuplicateFileCleanerPro.Tests.Unit;

public sealed class ArchitectureTests
{
    [Fact]
    public void Core_does_not_depend_on_WinUI()
    {
        var references = typeof(AppSettings).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();
        Assert.DoesNotContain(references, name => string.Equals(name, "Microsoft.UI.Xaml", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_scan_policy_excludes_empty_files()
    {
        Assert.Equal(1, ScanOptions.Default.MinimumFileSizeBytes);
    }
}
