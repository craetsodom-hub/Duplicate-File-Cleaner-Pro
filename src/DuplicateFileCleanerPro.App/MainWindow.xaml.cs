using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace DuplicateFileCleanerPro.App;

public sealed partial class MainWindow : Window
{
    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int MinimumWindowWidth = 800;
    private const int MinimumWindowHeight = 600;

    private readonly NativeWindowProcedure _windowProcedure;
    private IntPtr _previousWindowProcedure;

    public MainWindow()
    {
        InitializeComponent();
        _windowProcedure = WindowProcedure;
        ConfigureMinimumWindowSize();
        ShellNavigation.SelectionChanged += OnNavigationSelectionChanged;
        ShellNavigation.ActualThemeChanged += OnActualThemeChanged;
        Title = "Duplicate File Cleaner Pro";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ShellNavigation);
        ConfigureCaptionButtons();
    }

    private void ConfigureMinimumWindowSize()
    {
        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _previousWindowProcedure = SetWindowLongPtr(
            windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmGetMinMaxInfo)
        {
            MinMaxInfo sizingInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            uint dpi = GetDpiForWindow(windowHandle);
            sizingInfo.MinimumTrackSize.X = ScaleForDpi(MinimumWindowWidth, dpi);
            sizingInfo.MinimumTrackSize.Y = ScaleForDpi(MinimumWindowHeight, dpi);
            Marshal.StructureToPtr(sizingInfo, lParam, false);
        }

        return CallWindowProc(_previousWindowProcedure, windowHandle, message, wParam, lParam);
    }

    private static int ScaleForDpi(int pixelsAt96Dpi, uint dpi) => (int)Math.Ceiling(pixelsAt96Dpi * dpi / 96.0);

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ConfigureCaptionButtons();

    private void ConfigureCaptionButtons()
    {
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        Microsoft.UI.Windowing.AppWindowTitleBar titleBar = AppWindow.TitleBar;
        bool isDark = ShellNavigation.ActualTheme == ElementTheme.Dark;
        titleBar.ButtonForegroundColor = isDark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);
        titleBar.ButtonInactiveForegroundColor = isDark ? Color.FromArgb(255, 150, 150, 150) : Color.FromArgb(255, 96, 96, 96);
        titleBar.ButtonBackgroundColor = isDark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        titleBar.ButtonInactiveBackgroundColor = titleBar.ButtonBackgroundColor;
        titleBar.ButtonHoverBackgroundColor = isDark ? Color.FromArgb(255, 48, 48, 48) : Color.FromArgb(255, 229, 229, 229);
        titleBar.ButtonPressedBackgroundColor = isDark ? Color.FromArgb(255, 62, 62, 62) : Color.FromArgb(255, 216, 216, 216);
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        bool settingsSelected = ReferenceEquals(args.SelectedItem, SettingsNavigationItem);
        ScanPage.Visibility = settingsSelected ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settingsSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private delegate IntPtr NativeWindowProcedure(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaximumSize;
        public Point MaximumPosition;
        public Point MinimumTrackSize;
        public Point MaximumTrackSize;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure,
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr newWindowProcedure);
}
