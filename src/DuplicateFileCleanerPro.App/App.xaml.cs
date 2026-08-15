using Microsoft.UI.Xaml;
using DuplicateFileCleanerPro.App.Settings;

namespace DuplicateFileCleanerPro.App;

public partial class App : Application
{
    private Window? window;
    private readonly AppSettingsService settings = new(new WindowsAppSettingsStore());

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(settings);
        window.Activate();
    }
}
