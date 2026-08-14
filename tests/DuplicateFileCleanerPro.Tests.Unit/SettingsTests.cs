using DuplicateFileCleanerPro.Core.Models;
using DuplicateFileCleanerPro.Infrastructure.Settings;
using Xunit;

namespace DuplicateFileCleanerPro.Tests.Unit;

public sealed class SettingsTests
{
    [Fact]
    public async Task Corrupt_settings_fall_back_to_safe_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dfcp-settings-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "not json");
        try
        {
            var settings = await new JsonSettingsService(directory).LoadAsync();
            Assert.Equal(AppSettings.Default, settings);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
