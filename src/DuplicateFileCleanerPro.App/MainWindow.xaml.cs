using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.UI;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.Resources;

namespace DuplicateFileCleanerPro.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int MinimumWindowWidth = 800;
    private const int MinimumWindowHeight = 600;

    private readonly NativeWindowProcedure _windowProcedure;
    private static readonly ResourceLoader ResourceLoader = ResourceLoader.GetForViewIndependentUse();
    private static readonly CompositeFormat CompletionSummaryFormat = CompositeFormat.Parse(ResourceLoader.GetString("CompletionSummary"));
    private static readonly CompositeFormat CompletionDetailFormat = CompositeFormat.Parse(ResourceLoader.GetString("CompletionDetail"));
    private static readonly CompositeFormat ReviewWithManyRootsFormat = CompositeFormat.Parse(ResourceLoader.GetString("ReviewWithManyRoots"));
    private readonly WindowsScanRootNormalizer rootNormalizer = new();
    private readonly ObservableCollection<SelectedScanRoot> selectedRoots = [];
    private readonly ScanWorkflowController scanWorkflow = new(new ScanSessionService(new WindowsFileDiscoveryService(), new WindowsContentAnalysisService()));
    private readonly Stopwatch scanStopwatch = new();
    private readonly DispatcherQueueTimer elapsedTimer;
    private string? setupNotice;
    private long activeScanGeneration;
    private bool isDisposed;
    private IntPtr _previousWindowProcedure;

    public MainWindow()
    {
        InitializeComponent();
        LocationsList.ItemsSource = selectedRoots;
        _windowProcedure = WindowProcedure;
        ConfigureMinimumWindowSize();
        ShellNavigation.SelectionChanged += OnNavigationSelectionChanged;
        ShellNavigation.ActualThemeChanged += OnActualThemeChanged;
        Title = "Duplicate File Cleaner Pro";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ShellNavigation);
        ConfigureCaptionButtons();
        elapsedTimer = DispatcherQueue.CreateTimer();
        elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        elapsedTimer.Tick += (_, _) => ElapsedMetricText.Text = scanStopwatch.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        Closed += OnClosed;
        UpdateSetupState();
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

    private async void OnAddFolderClick(object sender, RoutedEventArgs args)
    {
        FolderPicker picker = new();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        RootNormalizationResult normalization = rootNormalizer.Normalize(selectedRoots.Select(root => root.NormalizedPath).Append(folder.Path));
        selectedRoots.Clear();
        foreach (ScanRoot root in normalization.Roots)
        {
            string name = Path.GetFileName(root.NormalizedPath.TrimEnd(Path.DirectorySeparatorChar));
            selectedRoots.Add(new SelectedScanRoot(string.IsNullOrEmpty(name) ? root.NormalizedPath : name, root.NormalizedPath));
        }

        UpdateSetupState();
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: SelectedScanRoot root })
        {
            selectedRoots.Remove(root);
            UpdateSetupState();
        }
    }

    private async void OnStartScanClick(object sender, RoutedEventArgs args)
    {
        if (selectedRoots.Count == 0 || scanWorkflow.IsRunning)
        {
            return;
        }

        long generation = ++activeScanGeneration;
        setupNotice = null;
        ScanPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Collapsed;
        ScanningPage.Visibility = Visibility.Visible;
        ShellNavigation.IsPaneToggleButtonVisible = false;
        scanStopwatch.Restart();
        elapsedTimer.Start();
        var progress = new CoalescingUiProgress<ScanSessionProgress>(DispatcherQueue, update =>
        {
            if (!isDisposed && generation == activeScanGeneration)
            {
                UpdateScanProgress(update);
            }
        });
        ScanSessionResult result = await scanWorkflow.StartAsync(selectedRoots.Select(root => new ScanRoot(root.NormalizedPath)), new DiscoveryPolicy(), progress);
        if (isDisposed || generation != activeScanGeneration)
        {
            return;
        }

        activeScanGeneration++;
        elapsedTimer.Stop();
        ShellNavigation.IsPaneToggleButtonVisible = true;
        if (result.State == ScanSessionState.Completed && result.CompletedResult is not null)
        {
            ShowCompletion(result.CompletedResult);
        }
        else if (result.State == ScanSessionState.Cancelled)
        {
            ScanningPage.Visibility = Visibility.Collapsed;
            ScanPage.Visibility = Visibility.Visible;
            UpdateSetupState();
        }
        else
        {
            ScanningPage.Visibility = Visibility.Collapsed;
            ScanPage.Visibility = Visibility.Visible;
            setupNotice = Text("ScanFailed");
            UpdateSetupState();
        }
    }

    private void OnCancelScanClick(object sender, RoutedEventArgs args) => scanWorkflow.Cancel();

    private void OnNewScanClick(object sender, RoutedEventArgs args)
    {
        CompletedPage.Visibility = Visibility.Collapsed;
        ScanPage.Visibility = Visibility.Visible;
        UpdateSetupState();
    }

    private void UpdateScanProgress(ScanSessionProgress progress)
    {
        bool determinateAnalysis = progress.State == ScanSessionState.Analyzing && !progress.IsVerifying;
        ScanProgressBar.IsIndeterminate = !determinateAnalysis;
        ScanProgressBar.Value = progress.TotalCandidateBytes == 0 ? 0 : 100d * progress.BytesProcessed / progress.TotalCandidateBytes;
        ScanStageTitle.Text = progress.State == ScanSessionState.Discovering ? Text("ScanDiscovering") : Text("ScanAnalyzing");
        CurrentActivityText.Text = string.IsNullOrWhiteSpace(progress.CurrentPath) ? Text("ScanActivityFallback") : progress.CurrentPath;
        FilesMetricText.Text = progress.FilesDiscovered.ToString(CultureInfo.CurrentCulture);
        AnalyzedMetricText.Text = progress.FilesAnalyzed.ToString(CultureInfo.CurrentCulture);
        GroupsMetricText.Text = progress.VerifiedGroupCount.ToString(CultureInfo.CurrentCulture);
    }

    private void ShowCompletion(CompletedScanResult result)
    {
        ScanningPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Visible;
        int duplicateFiles = result.Detection.Groups.Sum(group => group.Files.Count);
        CompletionSummaryText.Text = string.Format(CultureInfo.CurrentCulture, CompletionSummaryFormat, result.Detection.Groups.Count);
        CompletionDetailText.Text = string.Format(CultureInfo.CurrentCulture, CompletionDetailFormat, duplicateFiles, result.Detection.TotalReclaimableBytes, result.Discovery.SkippedItems.Count + result.Detection.SkippedItems.Count);
    }

    private void UpdateSetupState()
    {
        bool hasRoots = selectedRoots.Count > 0;
        ScanLocationsEmptyText.Visibility = hasRoots ? Visibility.Collapsed : Visibility.Visible;
        LocationsList.Visibility = hasRoots ? Visibility.Visible : Visibility.Collapsed;
        StartScanButton.IsEnabled = hasRoots && !scanWorkflow.IsRunning;
        ReviewSummaryText.Text = setupNotice ?? (selectedRoots.Count switch
        {
            0 => Text("ReviewEmpty"),
            1 => Text("ReviewWithOneRoot"),
            _ => string.Format(CultureInfo.CurrentCulture, ReviewWithManyRootsFormat, selectedRoots.Count),
        });
    }

    private static string Text(string key) => ResourceLoader.GetString(key);

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        activeScanGeneration++;
        elapsedTimer.Stop();
        scanWorkflow.Dispose();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        Dispose();
    }

    private sealed class CoalescingUiProgress<T>(DispatcherQueue dispatcher, Action<T> callback) : IProgress<T>
        where T : class
    {
        private readonly object gate = new();
        private T? latest;
        private bool scheduled;

        public void Report(T value)
        {
            lock (gate)
            {
                latest = value;
                if (scheduled)
                {
                    return;
                }

                scheduled = true;
            }

            if (!dispatcher.TryEnqueue(Drain))
            {
                lock (gate)
                {
                    scheduled = false;
                    latest = null;
                }
            }
        }

        private void Drain()
        {
            T? value;
            lock (gate)
            {
                value = latest;
                latest = null;
                scheduled = false;
            }

            if (value is not null)
            {
                callback(value);
            }
        }
    }

    private sealed record SelectedScanRoot(string DisplayName, string NormalizedPath);

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
