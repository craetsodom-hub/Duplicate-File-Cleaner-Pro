using DuplicateFileCleanerPro.Core.Services;
using DuplicateFileCleanerPro.Infrastructure.Settings;
using DuplicateFileCleanerPro.Infrastructure.TemporaryData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace DuplicateFileCleanerPro.App;

public partial class App : Application
{
    private Window? _window;
    public static IServiceProvider Services { get; } = ConfigureServices();

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var temporaryData = Services.GetRequiredService<IOwnedTempDirectoryService>();
        await temporaryData.CleanupStaleSessionsAsync();
        _window = new MainWindow();
        _window.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuplicateFileCleanerPro");
        return new ServiceCollection()
            .AddSingleton<ISettingsService>(_ => new JsonSettingsService(Path.Combine(appData, "Settings")))
            .AddSingleton<IOwnedTempDirectoryService>(_ => new OwnedTempDirectoryService(Path.Combine(appData, "Temp")))
            .BuildServiceProvider(validateScopes: true);
    }
}
