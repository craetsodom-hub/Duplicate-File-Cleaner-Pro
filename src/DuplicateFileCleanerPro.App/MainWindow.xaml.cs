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
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.Resources;
using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.App.Cleanup;
using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Infrastructure.Windows.Cleanup;

namespace DuplicateFileCleanerPro.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int MinimumWindowWidth = 800;
    private const int MinimumWindowHeight = 600;

    private readonly NativeWindowProcedure _windowProcedure;
    private static readonly ResourceLoader ResourceLoader = ResourceLoader.GetForViewIndependentUse();
    private static readonly CompositeFormat ReviewWithManyRootsFormat = CompositeFormat.Parse(ResourceLoader.GetString("ReviewWithManyRoots"));
    private static readonly CompositeFormat ResultCandidatesFormat = CompositeFormat.Parse(ResourceLoader.GetString("ResultCandidatesFormat"));
    private static readonly CompositeFormat CleanupReviewSummaryFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupReviewSummaryFormat"));
    private static readonly CompositeFormat CleanupConfirmTitleFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupConfirmTitleFormat"));
    private static readonly CompositeFormat CleanupConfirmDescriptionFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupConfirmDescriptionFormat"));
    private static readonly CompositeFormat CleanupProcessedFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupProcessedFormat"));
    private static readonly CompositeFormat CleanupCompletedSummaryFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupCompletedSummaryFormat"));
    private readonly WindowsScanRootNormalizer rootNormalizer = new();
    private readonly ObservableCollection<SelectedScanRoot> selectedRoots = [];
    private readonly SafetyOperationCoordinator safetyOperations = new();
    private readonly ScanWorkflowController scanWorkflow;
    private readonly CleanupWorkflowViewModel cleanupWorkflow;
    private readonly Stopwatch scanStopwatch = new();
    private readonly DispatcherQueueTimer elapsedTimer;
    private string? setupNotice;
    private long activeScanGeneration;
    private bool isDisposed;
    private IntPtr _previousWindowProcedure;

    public ResultsReviewViewModel? ResultsViewModel { get; private set; }

    public MainWindow()
    {
        scanWorkflow = new ScanWorkflowController(
            new ScanSessionService(new WindowsFileDiscoveryService(), new WindowsContentAnalysisService()),
            safetyOperations);
        cleanupWorkflow = new CleanupWorkflowViewModel(
            new CleanupEngine(new WindowsCleanupPlatformService(), safetyOperations));
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
        if (CleanupPage.Visibility == Visibility.Visible)
        {
            if (!ReferenceEquals(args.SelectedItem, ResultsNavigationItem))
            {
                ShellNavigation.SelectedItem = ResultsNavigationItem;
            }

            return;
        }

        bool resultsSelected = ReferenceEquals(args.SelectedItem, ResultsNavigationItem);
        bool settingsSelected = ReferenceEquals(args.SelectedItem, SettingsNavigationItem);
        ScanScrollViewer.Visibility = resultsSelected ? Visibility.Collapsed : Visibility.Visible;
        ResultsPage.Visibility = resultsSelected ? Visibility.Visible : Visibility.Collapsed;
        ScanPage.Visibility = settingsSelected ? Visibility.Collapsed : Visibility.Visible;
        ScanningPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = settingsSelected ? Visibility.Visible : Visibility.Collapsed;
        if (!resultsSelected && !settingsSelected && scanWorkflow.IsRunning)
        {
            ScanPage.Visibility = Visibility.Collapsed;
            ScanningPage.Visibility = Visibility.Visible;
        }
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
        if (selectedRoots.Count == 0 || scanWorkflow.IsRunning || cleanupWorkflow.IsActive)
        {
            return;
        }

        long generation = ++activeScanGeneration;
        setupNotice = null;
        ClearResultsForNewScan();
        ResultsPage.Visibility = Visibility.Collapsed;
        ScanScrollViewer.Visibility = Visibility.Visible;
        ScanPage.Visibility = Visibility.Collapsed;
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
            ShowResults(result.CompletedResult);
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
        if (cleanupWorkflow.IsActive)
        {
            return;
        }

        cleanupWorkflow.ResetForNewScan();
        ShellNavigation.SelectedItem = ScanNavigationItem;
        ResultsPage.Visibility = Visibility.Collapsed;
        CleanupPage.Visibility = Visibility.Collapsed;
        ScanScrollViewer.Visibility = Visibility.Visible;
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

    private void ShowResults(CompletedScanResult result)
    {
        ScanningPage.Visibility = Visibility.Collapsed;
        if (ResultsViewModel is not null)
        {
            ResultsViewModel.PropertyChanged -= OnResultsViewModelPropertyChanged;
        }

        ResultsViewModel = new ResultsReviewViewModel(result);
        ResultsViewModel.PropertyChanged += OnResultsViewModelPropertyChanged;
        ResultsPage.DataContext = ResultsViewModel;
        ResultsNavigationItem.IsEnabled = true;
        ResultGroupsText.Text = ResultsViewModel.DuplicateGroupCount.ToString(CultureInfo.CurrentCulture);
        ResultFilesText.Text = ResultsViewModel.VerifiedMemberCount.ToString(CultureInfo.CurrentCulture);
        ResultReclaimableText.Text = ResultDisplayFormatter.FormatBytes(ResultsViewModel.ReclaimableBytes);
        UpdateCandidateSummary();
        ResultSkippedText.Text = ResultsViewModel.SkippedItemCount.ToString(CultureInfo.CurrentCulture);
        ResultsEmptyTitle.Text = ResultsViewModel.HasResults ? Text("ResultsNoMatchesTitle") : Text("ResultsNoDuplicatesTitle");
        ResultsEmptyDescription.Text = ResultsViewModel.HasResults ? Text("ResultsNoMatchesDescription") : Text("ResultsNoDuplicatesDescription");
        UpdateResultsEmptyState();
        ResultsStaleNotice.IsOpen = false;
        CleanupPage.Visibility = Visibility.Collapsed;
        ScanScrollViewer.Visibility = Visibility.Collapsed;
        ResultsPage.Visibility = Visibility.Visible;
        ShellNavigation.SelectedItem = ResultsNavigationItem;
    }

    private void ClearResultsForNewScan()
    {
        if (ResultsViewModel is not null)
        {
            ResultsViewModel.PropertyChanged -= OnResultsViewModelPropertyChanged;
        }

        ResultsViewModel = null;
        ResultsPage.DataContext = null;
        ResultsNavigationItem.IsEnabled = false;
        cleanupWorkflow.ResetForNewScan();
        ResultsStaleNotice.IsOpen = false;
    }

    private void OnResultsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ResultsReviewViewModel.SelectedCandidateCount) or nameof(ResultsReviewViewModel.SelectedCandidateBytes))
        {
            UpdateCandidateSummary();
        }

        if (args.PropertyName == nameof(ResultsReviewViewModel.HasVisibleGroups))
        {
            UpdateResultsEmptyState();
        }
    }

    private void UpdateCandidateSummary()
    {
        if (ResultsViewModel is null) return;
        ResultCandidatesText.Text = string.Format(CultureInfo.CurrentCulture, ResultCandidatesFormat,
            ResultsViewModel.SelectedCandidateCount,
            ResultDisplayFormatter.FormatBytes(ResultsViewModel.SelectedCandidateBytes));
        ReviewCleanupButton.IsEnabled = ResultsViewModel.SelectedCandidateCount > 0 && !cleanupWorkflow.RequiresRescan;
    }

    private void OnResultsSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (ResultsViewModel is null) return;
        ResultsViewModel.SearchText = sender.Text;
        UpdateResultsEmptyState();
    }

    private void OnResultsSortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ResultsViewModel is null || ResultsSortComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        ResultsViewModel.SortOption = Enum.Parse<ResultSortOption>(tag, ignoreCase: false);
    }

    private void OnResultsFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ResultsViewModel is null || ResultsFilterComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        ResultsViewModel.FilterOption = Enum.Parse<ResultFilterOption>(tag, ignoreCase: false);
        UpdateResultsEmptyState();
    }

    private void OnResultsDirectionClick(object sender, RoutedEventArgs args)
    {
        if (ResultsViewModel is null) return;
        ResultsViewModel.SortDescending = !ResultsViewModel.SortDescending;
        ResultsDirectionButton.Content = Text(ResultsViewModel.SortDescending ? "ResultsDescending" : "ResultsAscending");
    }

    private void OnExpandAllClick(object sender, RoutedEventArgs args) => ResultsViewModel?.ExpandAll();

    private void OnCollapseAllClick(object sender, RoutedEventArgs args) => ResultsViewModel?.CollapseAll();

    private void OnReviewCleanupClick(object sender, RoutedEventArgs args)
    {
        if (ResultsViewModel is null || !cleanupWorkflow.BeginReview(ResultsViewModel))
        {
            return;
        }

        ShowCleanupReview();
    }

    private void ShowCleanupReview()
    {
        CleanupPageTitle.Text = Text("CleanupReviewTitle");
        CleanupPageDescription.Text = Text("CleanupReviewDescription");
        CleanupReviewSummaryText.Text = string.Format(
            CultureInfo.CurrentCulture,
            CleanupReviewSummaryFormat,
            cleanupWorkflow.SelectedCandidateCount,
            ResultDisplayFormatter.FormatBytes(cleanupWorkflow.SelectedCandidateBytes),
            cleanupWorkflow.AffectedGroupCount);
        CleanupCandidateFilesText.Text = cleanupWorkflow.SelectedCandidateCount.ToString(CultureInfo.CurrentCulture);
        CleanupCandidateSpaceText.Text = ResultDisplayFormatter.FormatBytes(cleanupWorkflow.SelectedCandidateBytes);
        CleanupAffectedGroupsText.Text = cleanupWorkflow.AffectedGroupCount.ToString(CultureInfo.CurrentCulture);
        CleanupReviewList.ItemsSource = cleanupWorkflow.ReviewCandidates;
        CleanupProgressBar.Maximum = Math.Max(1, cleanupWorkflow.SelectedCandidateCount);
        CleanupProgressBar.Value = 0;
        CleanupReviewPanel.Visibility = Visibility.Visible;
        CleanupProgressPanel.Visibility = Visibility.Collapsed;
        CleanupCompletionPanel.Visibility = Visibility.Collapsed;
        CleanupBackButton.Visibility = Visibility.Visible;
        CleanupRescanButton.Visibility = Visibility.Collapsed;
        ScanScrollViewer.Visibility = Visibility.Collapsed;
        ResultsPage.Visibility = Visibility.Collapsed;
        CleanupPage.Visibility = Visibility.Visible;
        ShellNavigation.SelectedItem = ResultsNavigationItem;
    }

    private async void OnConfirmCleanupClick(object sender, RoutedEventArgs args)
    {
        if (!cleanupWorkflow.IsReviewing)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = ShellNavigation.XamlRoot,
            Title = string.Format(CultureInfo.CurrentCulture, CleanupConfirmTitleFormat, cleanupWorkflow.SelectedCandidateCount),
            Content = string.Format(CultureInfo.CurrentCulture, CleanupConfirmDescriptionFormat, ResultDisplayFormatter.FormatBytes(cleanupWorkflow.SelectedCandidateBytes)),
            PrimaryButtonText = Text("MoveToRecycleBinButton"),
            CloseButtonText = Text("CleanupConfirmCancelButton"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || isDisposed)
        {
            return;
        }

        ShowCleanupProgress();
        var progress = new CoalescingUiProgress<CleanupProgress>(DispatcherQueue, UpdateCleanupProgress);
        CleanupResult? result = await cleanupWorkflow.ExecuteConfirmedAsync(progress);
        if (isDisposed)
        {
            return;
        }

        ShowCleanupCompletion(result);
    }

    private void ShowCleanupProgress()
    {
        CleanupPageTitle.Text = Text("CleanupProgressTitle");
        CleanupPageDescription.Text = Text("CleanupProgressDescription");
        CleanupActivityText.Text = Text("CleanupPreparing");
        CleanupProcessedText.Text = string.Format(CultureInfo.CurrentCulture, CleanupProcessedFormat, 0, cleanupWorkflow.SelectedCandidateCount);
        CleanupMovedText.Text = 0.ToString(CultureInfo.CurrentCulture);
        CleanupSkippedText.Text = 0.ToString(CultureInfo.CurrentCulture);
        CleanupReclaimedText.Text = ResultDisplayFormatter.FormatBytes(0);
        CleanupReviewPanel.Visibility = Visibility.Collapsed;
        CleanupProgressPanel.Visibility = Visibility.Visible;
        CleanupCompletionPanel.Visibility = Visibility.Collapsed;
        CleanupBackButton.Visibility = Visibility.Collapsed;
        CleanupRescanButton.Visibility = Visibility.Collapsed;
        CancelCleanupButton.IsEnabled = true;
        SetCleanupNavigationActive(true);
    }

    private void UpdateCleanupProgress(CleanupProgress progress)
    {
        if (isDisposed || !cleanupWorkflow.IsActive)
        {
            return;
        }

        CleanupProgressBar.Value = progress.CandidatesProcessed;
        CleanupActivityText.Text = Text("CleanupVerifyingActivity");
        CleanupProcessedText.Text = string.Format(CultureInfo.CurrentCulture, CleanupProcessedFormat, progress.CandidatesProcessed, progress.CandidatesTotal);
        CleanupMovedText.Text = progress.RecycledCount.ToString(CultureInfo.CurrentCulture);
        CleanupSkippedText.Text = progress.SkippedCount.ToString(CultureInfo.CurrentCulture);
        CleanupReclaimedText.Text = ResultDisplayFormatter.FormatBytes(progress.ActuallyReclaimedBytes);
    }

    private void OnCancelCleanupClick(object sender, RoutedEventArgs args)
    {
        CancelCleanupButton.IsEnabled = false;
        CleanupActivityText.Text = Text("CleanupCancelling");
        cleanupWorkflow.Cancel();
    }

    private void ShowCleanupCompletion(CleanupResult? result)
    {
        SetCleanupNavigationActive(false);
        CleanupReviewPanel.Visibility = Visibility.Collapsed;
        CleanupProgressPanel.Visibility = Visibility.Collapsed;
        CleanupCompletionPanel.Visibility = Visibility.Visible;
        CleanupBackButton.Visibility = Visibility.Visible;
        CleanupRescanButton.Visibility = Visibility.Visible;

        if (result is null)
        {
            CleanupPageTitle.Text = Text("CleanupFailedTitle");
            CleanupPageDescription.Text = Text(cleanupWorkflow.PlanningFailureKey ?? "CleanupUnexpectedFailure");
            CleanupCompletionTitle.Text = Text("CleanupFailedTitle");
            CleanupCompletionSummary.Text = Text("CleanupNoChangesSummary");
            CleanupCompletionMovedText.Text = 0.ToString(CultureInfo.CurrentCulture);
            CleanupCompletionReclaimedText.Text = ResultDisplayFormatter.FormatBytes(0);
            CleanupCompletionSkippedText.Text = 0.ToString(CultureInfo.CurrentCulture);
            CleanupCompletionFailedText.Text = 0.ToString(CultureInfo.CurrentCulture);
            CleanupOutcomeList.Visibility = Visibility.Collapsed;
            CleanupOutcomesTitle.Visibility = Visibility.Collapsed;
            UpdateResultsCleanupState();
            return;
        }

        CleanupPageTitle.Text = result.WasCancelled ? Text("CleanupCancelledTitle") : Text("CleanupCompletedTitle");
        CleanupPageDescription.Text = Text("CleanupCompletionDescription");
        CleanupCompletionTitle.Text = result.WasCancelled ? Text("CleanupCancelledTitle") : Text("CleanupCompletedTitle");
        CleanupCompletionSummary.Text = result.RecycledFileCount == 0
            ? Text("CleanupNoChangesSummary")
            : string.Format(CultureInfo.CurrentCulture, CleanupCompletedSummaryFormat, result.RecycledFileCount, ResultDisplayFormatter.FormatBytes(result.ActuallyReclaimedBytes));
        CleanupCompletionMovedText.Text = result.RecycledFileCount.ToString(CultureInfo.CurrentCulture);
        CleanupCompletionReclaimedText.Text = ResultDisplayFormatter.FormatBytes(result.ActuallyReclaimedBytes);
        CleanupCompletionSkippedText.Text = result.SkippedFileCount.ToString(CultureInfo.CurrentCulture);
        CleanupCompletionFailedText.Text = result.FailedFileCount.ToString(CultureInfo.CurrentCulture);

        var details = result.Groups.SelectMany(group => group.Outcomes)
            .Where(outcome => outcome.Status != CleanupCandidateOutcomeStatus.Recycled)
            .Select(outcome =>
            {
                CleanupOutcomePresentation presentation = CleanupOutcomePresentationMapper.Map(outcome.Status);
                return new CleanupOutcomeDisplay(
                    outcome.Candidate.ExpectedFile.FileName,
                    outcome.Candidate.ExpectedFile.NormalizedPath,
                    outcome.Candidate.ExpectedFile.Length,
                    Text(presentation.MessageKey));
            })
            .ToArray();
        CleanupOutcomeList.ItemsSource = details;
        CleanupOutcomeList.Visibility = details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CleanupOutcomesTitle.Visibility = details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateResultsCleanupState();
    }

    private void UpdateResultsCleanupState()
    {
        ResultsStaleNotice.IsOpen = cleanupWorkflow.RequiresRescan;
        ReviewCleanupButton.IsEnabled = ResultsViewModel is not null
            && ResultsViewModel.SelectedCandidateCount > 0
            && !cleanupWorkflow.RequiresRescan;
    }

    private void OnCleanupBackClick(object sender, RoutedEventArgs args)
    {
        if (cleanupWorkflow.IsActive)
        {
            return;
        }

        cleanupWorkflow.ReturnToResults();
        CleanupPage.Visibility = Visibility.Collapsed;
        ResultsPage.Visibility = Visibility.Visible;
        ScanScrollViewer.Visibility = Visibility.Collapsed;
        ShellNavigation.SelectedItem = ResultsNavigationItem;
        UpdateResultsCleanupState();
    }

    private void OnCleanupRescanClick(object sender, RoutedEventArgs args) => OnNewScanClick(sender, args);

    private void SetCleanupNavigationActive(bool isActive)
    {
        ScanNavigationItem.IsEnabled = !isActive;
        ResultsNavigationItem.IsEnabled = !isActive && ResultsViewModel is not null;
        SettingsNavigationItem.IsEnabled = !isActive;
        ShellNavigation.IsPaneToggleButtonVisible = !isActive;
    }

    private void UpdateResultsEmptyState()
    {
        if (ResultsViewModel is null) return;
        ResultsGroupsList.Visibility = ResultsViewModel.HasVisibleGroups ? Visibility.Visible : Visibility.Collapsed;
        ResultsEmptyPanel.Visibility = ResultsViewModel.HasVisibleGroups ? Visibility.Collapsed : Visibility.Visible;
        if (ResultsViewModel.HasResults && !ResultsViewModel.HasVisibleGroups)
        {
            ResultsEmptyTitle.Text = Text("ResultsNoMatchesTitle");
            ResultsEmptyDescription.Text = Text("ResultsNoMatchesDescription");
        }
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
        cleanupWorkflow.Dispose();
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

    private sealed record CleanupOutcomeDisplay(string FileName, string Path, long Size, string Message);

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
