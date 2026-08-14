using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DuplicateFileCleanerPro.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShellNavigation.SelectionChanged += OnNavigationSelectionChanged;
        Title = "Duplicate File Cleaner Pro";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ShellNavigation);
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        bool settingsSelected = ReferenceEquals(args.SelectedItem, SettingsNavigationItem);
        ScanPage.Visibility = settingsSelected ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settingsSelected ? Visibility.Visible : Visibility.Collapsed;
    }
}
