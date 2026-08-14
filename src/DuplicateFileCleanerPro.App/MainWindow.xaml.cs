using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DuplicateFileCleanerPro.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Content = new Grid();
    }
}
