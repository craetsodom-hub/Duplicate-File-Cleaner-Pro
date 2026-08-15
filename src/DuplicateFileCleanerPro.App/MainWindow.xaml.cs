using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Scanning;
using DuplicateFileCleanerPro.Infrastructure.Windows.Detection;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.Storage.Pickers;
using Windows.UI;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;
using DuplicateFileCleanerPro.App.Results;
using DuplicateFileCleanerPro.App.Cleanup;
using DuplicateFileCleanerPro.Core.Cleanup;
using DuplicateFileCleanerPro.Infrastructure.Windows.Cleanup;
using DuplicateFileCleanerPro.App.Settings;
using DuplicateFileCleanerPro.App.Accessibility;
using Windows.ApplicationModel;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml.Input;
using System.Reflection;

namespace DuplicateFileCleanerPro.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int MinimumWindowWidth = 800;
    private const int MinimumWindowHeight = 600;

    private readonly NativeWindowProcedure _windowProcedure;
    private static readonly ResourceLoader ResourceLoader = new();
    private static readonly CompositeFormat ReviewWithManyRootsFormat = CompositeFormat.Parse(ResourceLoader.GetString("ReviewWithManyRoots"));
    private static readonly CompositeFormat CriteriaSummaryFormat = CompositeFormat.Parse(ResourceLoader.GetString("CriteriaSummaryFormat"));
    private static readonly CompositeFormat CriteriaSelectedTypesFormat = CompositeFormat.Parse(ResourceLoader.GetString("CriteriaSelectedTypesFormat"));
    private static readonly CompositeFormat CriteriaMinimumSizeFormat = CompositeFormat.Parse(ResourceLoader.GetString("CriteriaMinimumSizeFormat"));
    private static readonly CompositeFormat CriteriaMaximumSizeFormat = CompositeFormat.Parse(ResourceLoader.GetString("CriteriaMaximumSizeFormat"));
    private static readonly CompositeFormat CriteriaSizeRangeFormat = CompositeFormat.Parse(ResourceLoader.GetString("CriteriaSizeRangeFormat"));
    private static readonly CompositeFormat DriveCapacityFormat = CompositeFormat.Parse(ResourceLoader.GetString("DriveCapacityFormat"));
    private static readonly CompositeFormat ResultCandidatesFormat = CompositeFormat.Parse(ResourceLoader.GetString("ResultCandidatesFormat"));
    private static readonly CompositeFormat CleanupReviewSummaryFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupReviewSummaryFormat"));
    private static readonly CompositeFormat CleanupConfirmTitleFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupConfirmTitleFormat"));
    private static readonly CompositeFormat CleanupConfirmDescriptionFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupConfirmDescriptionFormat"));
    private static readonly CompositeFormat CleanupProcessedFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupProcessedFormat"));
    private static readonly CompositeFormat CleanupCompletedSummaryFormat = CompositeFormat.Parse(ResourceLoader.GetString("CleanupCompletedSummaryFormat"));
    private readonly WindowsScanRootNormalizer rootNormalizer = new();
    private readonly ObservableCollection<SelectedScanRoot> selectedRoots = [];
    private readonly ObservableCollection<DriveChoice> availableDrives = [];
    private readonly ObservableCollection<string> customExtensions = [];
    private readonly ObservableCollection<string> excludedFolders = [];
    private readonly ObservableCollection<string> excludedExtensions = [];
    private readonly ObservableCollection<ProfileChoice> profileChoices = [];
    private readonly List<SavedScanProfile> savedProfiles = [];
    private readonly SafetyOperationCoordinator safetyOperations = new();
    private readonly ScanWorkflowController scanWorkflow;
    private readonly CleanupWorkflowViewModel cleanupWorkflow;
    private readonly SettingsViewModel settingsViewModel;
    private readonly AppSettingsService appSettings;
    private readonly Stopwatch scanStopwatch = new();
    private readonly DispatcherQueueTimer elapsedTimer;
    private string? setupNotice;
    private long activeScanGeneration;
    private readonly OperationAnnouncementGate<string> scanAnnouncementGate = new();
    private bool isDisposed;
    private bool isApplyingSetup;
    private string activeProfileId = PremiumScanProfiles.AllFilesId;
    private IntPtr _previousWindowProcedure;

    public ResultsReviewViewModel? ResultsViewModel { get; private set; }

    public MainWindow(AppSettingsService settings)
    {
        appSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        scanWorkflow = new ScanWorkflowController(
            new ScanSessionService(new WindowsFileDiscoveryService(), new WindowsContentAnalysisService()),
            safetyOperations);
        cleanupWorkflow = new CleanupWorkflowViewModel(
            new CleanupEngine(new WindowsCleanupPlatformService(), safetyOperations));
        settingsViewModel = new SettingsViewModel(settings, ApplyAppearance);
        isApplyingSetup = true;
        InitializeComponent();
        AutomationProperties.SetLiveSetting(CleanupActivityText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(CleanupCompletionTitle, AutomationLiveSetting.Polite);
        LocationsList.ItemsSource = selectedRoots;
        AvailableDrivesComboBox.ItemsSource = availableDrives;
        CustomExtensionsList.ItemsSource = customExtensions;
        ExcludedFoldersList.ItemsSource = excludedFolders;
        ExcludedExtensionsList.ItemsSource = excludedExtensions;
        ProfileComboBox.ItemsSource = profileChoices;
        PageHost.SizeChanged += OnPageHostSizeChanged;
        _windowProcedure = WindowProcedure;
        ConfigureMinimumWindowSize();
        ShellNavigation.SelectionChanged += OnNavigationSelectionChanged;
        ShellNavigation.ActualThemeChanged += OnActualThemeChanged;
        ApplyAppearance(settingsViewModel.Appearance);
        AppearanceComboBox.SelectedIndex = (int)settingsViewModel.Appearance;
        (string displayName, Version version) = GetPackagePresentation();
        AppVersionText.Text = AppVersionFormatter.Format(
            (ushort)Math.Max(0, version.Major),
            (ushort)Math.Max(0, version.Minor),
            (ushort)Math.Max(0, version.Build),
            (ushort)Math.Max(0, version.Revision));
        Title = displayName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureCaptionButtons();
        elapsedTimer = DispatcherQueue.CreateTimer();
        elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        elapsedTimer.Tick += (_, _) => ElapsedMetricText.Text = scanStopwatch.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        Closed += OnClosed;
        LoadPersistedScanSetup();
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

    private void OnPageHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool isWide = args.NewSize.Width >= 900;
        bool isToolbarWide = args.NewSize.Width >= 1000;

        ScanPrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        ScanReviewColumn.Width = isWide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ScanWorkspaceSecondaryRow.Height = isWide ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(ScanReviewSurface, isWide ? 0 : 1);
        Grid.SetColumn(ScanReviewSurface, isWide ? 1 : 0);
        ScanReviewSurface.Margin = new Thickness(0);

        ResultsToolbarSearchColumn.Width = isToolbarWide ? new GridLength(280) : new GridLength(1, GridUnitType.Star);
        ResultsToolbarSortColumn.Width = GridLength.Auto;
        ResultsToolbarDirectionColumn.Width = GridLength.Auto;
        ResultsToolbarFilterColumn.Width = GridLength.Auto;
        ResultsToolbarActionsColumn.Width = isToolbarWide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ResultsSearchBox, 0);
        Grid.SetColumn(ResultsSearchBox, 0);
        Grid.SetColumnSpan(ResultsSearchBox, isToolbarWide ? 1 : 5);
        ResultsSearchBox.Margin = isToolbarWide ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 0, 8);
        Grid.SetRow(ResultsSortComboBox, isToolbarWide ? 0 : 1);
        Grid.SetColumn(ResultsSortComboBox, isToolbarWide ? 1 : 0);
        ResultsSortComboBox.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetRow(ResultsDirectionButton, isToolbarWide ? 0 : 1);
        Grid.SetColumn(ResultsDirectionButton, isToolbarWide ? 2 : 1);
        ResultsDirectionButton.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetRow(ResultsFilterComboBox, isToolbarWide ? 0 : 1);
        Grid.SetColumn(ResultsFilterComboBox, isToolbarWide ? 3 : 2);
        ResultsFilterComboBox.Margin = isToolbarWide ? new Thickness(0, 0, 8, 0) : new Thickness(0);
        Grid.SetRow(ResultsExpandPanel, isToolbarWide ? 0 : 1);
        Grid.SetColumn(ResultsExpandPanel, isToolbarWide ? 4 : 3);
        Grid.SetColumnSpan(ResultsExpandPanel, isToolbarWide ? 1 : 2);
        ResultsExpandPanel.Margin = isToolbarWide ? new Thickness(0) : new Thickness(8, 0, 0, 0);
    }

    private void ApplyAppearance(AppearancePreference appearance)
    {
        WindowRoot.RequestedTheme = appearance switch
        {
            AppearancePreference.Light => ElementTheme.Light,
            AppearancePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ConfigureCaptionButtons();
    }

    private void OnAppearanceSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (AppearanceComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, ignoreCase: false, out AppearancePreference appearance))
        {
            settingsViewModel.SetAppearance(appearance);
        }
    }

    private void ConfigureCaptionButtons()
    {
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        Microsoft.UI.Windowing.AppWindowTitleBar titleBar = AppWindow.TitleBar;
        if (new AccessibilitySettings().HighContrast)
        {
            // Let Windows supply the system high-contrast caption colors rather than overriding them.
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            return;
        }

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

    private static (string DisplayName, Version Version) GetPackagePresentation()
    {
        try
        {
            Package package = Package.Current;
            Windows.ApplicationModel.PackageVersion version = package.Id.Version;
            return (package.DisplayName, new Version(version.Major, version.Minor, version.Build, version.Revision));
        }
        catch (InvalidOperationException)
        {
            return (Text("AppDisplayName"), Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0));
        }
    }

    private void LoadPersistedScanSetup()
    {
        isApplyingSetup = true;
        try
        {
            ScanSetupSettings persisted = appSettings.LoadScanSetup();
            savedProfiles.Clear();
            savedProfiles.AddRange(persisted.SavedProfiles);
            activeProfileId = persisted.ActiveProfileId;
            RefreshAvailableDrives();
            ReplaceCollection(customExtensions, persisted.CustomExtensions);
            ReplaceCollection(excludedFolders, persisted.ExcludedFolders);
            ReplaceCollection(excludedExtensions, persisted.ExcludedExtensions);
            IncludeSubfoldersToggle.IsOn = persisted.IncludeSubfolders;
            ApplyCriteriaControls(persisted.FileTypes, persisted.MinimumSizeBytes, persisted.MaximumSizeBytes);
            SetSelectedSources(persisted.Sources);
            RefreshProfileChoices(activeProfileId);
        }
        finally
        {
            isApplyingSetup = false;
        }
    }

    private void RefreshAvailableDrives()
    {
        string? previous = (AvailableDrivesComboBox.SelectedItem as DriveChoice)?.RootPath;
        availableDrives.Clear();
        foreach (LocalDriveSource drive in WindowsLocalDriveCatalog.GetAvailableDrives())
        {
            string capacity = string.Format(
                CultureInfo.CurrentCulture,
                DriveCapacityFormat,
                ResultDisplayFormatter.FormatBytes(drive.AvailableFreeSpace),
                ResultDisplayFormatter.FormatBytes(drive.TotalSize));
            availableDrives.Add(new DriveChoice(
                drive.RootPath,
                drive.DisplayName,
                $"{drive.DisplayName} · {capacity}",
                capacity));
        }

        AvailableDrivesComboBox.SelectedItem = availableDrives.FirstOrDefault(drive =>
            drive.RootPath.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? availableDrives.FirstOrDefault();
        AddDriveButton.IsEnabled = availableDrives.Count > 0;
    }

    private void SetSelectedSources(IEnumerable<string> paths)
    {
        RootNormalizationResult normalization = rootNormalizer.Normalize(paths);
        selectedRoots.Clear();
        foreach (ScanRoot root in normalization.Roots)
        {
            DriveChoice? drive = availableDrives.FirstOrDefault(candidate =>
                candidate.RootPath.Equals(root.NormalizedPath, StringComparison.OrdinalIgnoreCase));
            string name = Path.GetFileName(root.NormalizedPath.TrimEnd(Path.DirectorySeparatorChar));
            selectedRoots.Add(drive is null
                ? new SelectedScanRoot(
                    string.IsNullOrEmpty(name) ? root.NormalizedPath : name,
                    root.NormalizedPath,
                    Text("SourceFolderType"),
                    "\uE8B7")
                : new SelectedScanRoot(drive.DisplayName, root.NormalizedPath, drive.Capacity, "\uEDA2"));
        }
    }

    private void AddSelectedSource(string path)
    {
        SetSelectedSources(selectedRoots.Select(root => root.NormalizedPath).Append(path));
        PersistScanSetup();
        UpdateSetupState();
    }

    private void RefreshProfileChoices(string selectedId)
    {
        profileChoices.Clear();
        foreach (PremiumScanProfile profile in PremiumScanProfiles.BuiltIn)
        {
            profileChoices.Add(new ProfileChoice(
                profile.Id,
                Text(ProfileNameKey(profile.Id)),
                Text(ProfileDescriptionKey(profile.Id)),
                false));
        }

        foreach (SavedScanProfile profile in savedProfiles)
        {
            profileChoices.Add(new ProfileChoice(profile.Id, profile.Name, Text("SavedProfileDescription"), true));
        }

        profileChoices.Add(new ProfileChoice(
            PremiumScanProfiles.CustomId,
            Text("ProfileCustomName"),
            Text("ProfileCustomDescription"),
            false));
        ProfileComboBox.SelectedItem = profileChoices.FirstOrDefault(profile =>
            profile.Id.Equals(selectedId, StringComparison.Ordinal))
            ?? profileChoices.First(profile => profile.Id.Equals(PremiumScanProfiles.CustomId, StringComparison.Ordinal));
        DeleteProfileButton.Visibility = (ProfileComboBox.SelectedItem as ProfileChoice)?.IsSaved == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string ProfileNameKey(string id) => id switch
    {
        PremiumScanProfiles.AllFilesId => "ProfileAllFilesName",
        PremiumScanProfiles.LargeFilesId => "ProfileLargeFilesName",
        PremiumScanProfiles.PhotosAndVideosId => "ProfilePhotosVideosName",
        PremiumScanProfiles.DocumentsId => "ProfileDocumentsName",
        PremiumScanProfiles.MusicId => "ProfileMusicName",
        _ => "ProfileCustomName",
    };

    private static string ProfileDescriptionKey(string id) => id switch
    {
        PremiumScanProfiles.AllFilesId => "ProfileAllFilesDescription",
        PremiumScanProfiles.LargeFilesId => "ProfileLargeFilesDescription",
        PremiumScanProfiles.PhotosAndVideosId => "ProfilePhotosVideosDescription",
        PremiumScanProfiles.DocumentsId => "ProfileDocumentsDescription",
        PremiumScanProfiles.MusicId => "ProfileMusicDescription",
        _ => "ProfileCustomDescription",
    };

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isApplyingSetup || ProfileComboBox.SelectedItem is not ProfileChoice choice)
        {
            return;
        }

        isApplyingSetup = true;
        try
        {
            activeProfileId = choice.Id;
            PremiumScanProfile? builtIn = PremiumScanProfiles.Find(choice.Id);
            if (builtIn is not null)
            {
                ReplaceCollection(customExtensions, builtIn.CustomExtensions ?? []);
                IncludeSubfoldersToggle.IsOn = true;
                ApplyCriteriaControls(builtIn.FileTypes, builtIn.MinimumSizeBytes, builtIn.MaximumSizeBytes);
            }
            else if (savedProfiles.FirstOrDefault(profile => profile.Id.Equals(choice.Id, StringComparison.Ordinal)) is SavedScanProfile saved)
            {
                ReplaceCollection(customExtensions, saved.CustomExtensions);
                ReplaceCollection(excludedFolders, saved.ExcludedFolders);
                ReplaceCollection(excludedExtensions, saved.ExcludedExtensions);
                IncludeSubfoldersToggle.IsOn = saved.IncludeSubfolders;
                ApplyCriteriaControls(saved.FileTypes, saved.MinimumSizeBytes, saved.MaximumSizeBytes);
            }

            DeleteProfileButton.Visibility = choice.IsSaved ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            isApplyingSetup = false;
        }

        PersistScanSetup();
        UpdateSetupState();
    }

    private async void OnSaveProfileClick(object sender, RoutedEventArgs args)
    {
        TextBox nameBox = new()
        {
            Header = Text("SaveProfileNameLabel"),
            PlaceholderText = Text("SaveProfileNamePlaceholder"),
            MaxLength = 64,
        };
        ContentDialog dialog = new()
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = Text("SaveProfileDialogTitle"),
            Content = nameBox,
            PrimaryButtonText = Text("SaveProfileDialogSave"),
            CloseButtonText = Text("DialogCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        ScanSetupSettings current = CaptureScanSetup();
        string id = "saved:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        savedProfiles.Add(new SavedScanProfile(
            id,
            nameBox.Text.Trim(),
            current.IncludeSubfolders,
            current.FileTypes,
            current.CustomExtensions,
            current.MinimumSizeBytes,
            current.MaximumSizeBytes,
            current.ExcludedFolders,
            current.ExcludedExtensions));
        activeProfileId = id;
        isApplyingSetup = true;
        try
        {
            RefreshProfileChoices(id);
        }
        finally
        {
            isApplyingSetup = false;
        }

        PersistScanSetup();
        ShowSetupNotice(Text("ProfileSavedNotice"), InfoBarSeverity.Success);
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs args)
    {
        if (ProfileComboBox.SelectedItem is not ProfileChoice { IsSaved: true } selected)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = Text("DeleteProfileDialogTitle"),
            Content = Text("DeleteProfileDialogDescription"),
            PrimaryButtonText = Text("DeleteProfileDialogDelete"),
            CloseButtonText = Text("DialogCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        savedProfiles.RemoveAll(profile => profile.Id.Equals(selected.Id, StringComparison.Ordinal));
        activeProfileId = PremiumScanProfiles.CustomId;
        isApplyingSetup = true;
        try
        {
            RefreshProfileChoices(activeProfileId);
        }
        finally
        {
            isApplyingSetup = false;
        }

        PersistScanSetup();
        ShowSetupNotice(Text("ProfileDeletedNotice"), InfoBarSeverity.Informational);
    }

    private void ApplyCriteriaControls(ScanFileType types, long minimumBytes, long? maximumBytes)
    {
        DocumentsCheckBox.IsChecked = types.HasFlag(ScanFileType.Documents);
        ImagesCheckBox.IsChecked = types.HasFlag(ScanFileType.Images);
        AudioCheckBox.IsChecked = types.HasFlag(ScanFileType.Audio);
        VideoCheckBox.IsChecked = types.HasFlag(ScanFileType.Video);
        ArchivesCheckBox.IsChecked = types.HasFlag(ScanFileType.Archives);
        OtherCheckBox.IsChecked = types.HasFlag(ScanFileType.Other);
        MinimumSizeNumberBox.Value = minimumBytes / 1048576d;
        MaximumSizeNumberBox.Value = maximumBytes is long maximum ? maximum / 1048576d : double.NaN;
    }

    private void OnFileTypeChanged(object sender, RoutedEventArgs args) => MarkSetupCustomAndPersist();

    private void OnSizeCriteriaChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => MarkSetupCustomAndPersist();

    private void OnIncludeSubfoldersToggled(object sender, RoutedEventArgs args) => MarkSetupCustomAndPersist();

    private void MarkSetupCustomAndPersist()
    {
        if (isApplyingSetup)
        {
            return;
        }

        activeProfileId = PremiumScanProfiles.CustomId;
        isApplyingSetup = true;
        try
        {
            ProfileComboBox.SelectedItem = profileChoices.FirstOrDefault(profile =>
                profile.Id.Equals(activeProfileId, StringComparison.Ordinal));
            DeleteProfileButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            isApplyingSetup = false;
        }

        PersistScanSetup();
        UpdateSetupState();
    }

    private void OnAddCustomExtensionClick(object sender, RoutedEventArgs args) => AddExtension(CustomExtensionTextBox, customExtensions);

    private void OnCustomExtensionKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            AddExtension(CustomExtensionTextBox, customExtensions);
            args.Handled = true;
        }
    }

    private void OnRemoveCustomExtensionClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string extension })
        {
            customExtensions.Remove(extension);
            MarkSetupCustomAndPersist();
        }
    }

    private void OnAddExcludedExtensionClick(object sender, RoutedEventArgs args) => AddExtension(ExcludedExtensionTextBox, excludedExtensions);

    private void OnExcludedExtensionKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            AddExtension(ExcludedExtensionTextBox, excludedExtensions);
            args.Handled = true;
        }
    }

    private void OnRemoveExcludedExtensionClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string extension })
        {
            excludedExtensions.Remove(extension);
            MarkSetupCustomAndPersist();
        }
    }

    private void AddExtension(TextBox input, ObservableCollection<string> target)
    {
        string? extension = ScanCriteria.NormalizeExtension(input.Text);
        if (extension is null)
        {
            ShowSetupNotice(Text("InvalidExtensionNotice"), InfoBarSeverity.Warning);
            return;
        }

        if (!target.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(extension);
        }

        input.Text = string.Empty;
        MarkSetupCustomAndPersist();
    }

    private async void OnAddExcludedFolderClick(object sender, RoutedEventArgs args)
    {
        FolderPicker picker = new();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        RootNormalizationResult normalization = rootNormalizer.Normalize([folder.Path]);
        if (normalization.Roots.Count == 1 && !excludedFolders.Contains(normalization.Roots[0].NormalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            excludedFolders.Add(normalization.Roots[0].NormalizedPath);
            MarkSetupCustomAndPersist();
        }
    }

    private void OnRemoveExcludedFolderClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            excludedFolders.Remove(path);
            MarkSetupCustomAndPersist();
        }
    }

    private void OnAddDriveClick(object sender, RoutedEventArgs args)
    {
        if (AvailableDrivesComboBox.SelectedItem is DriveChoice drive)
        {
            AddSelectedSource(drive.RootPath);
        }
    }

    private ScanFileType GetSelectedFileTypes()
    {
        ScanFileType types = ScanFileType.None;
        if (DocumentsCheckBox.IsChecked == true) types |= ScanFileType.Documents;
        if (ImagesCheckBox.IsChecked == true) types |= ScanFileType.Images;
        if (AudioCheckBox.IsChecked == true) types |= ScanFileType.Audio;
        if (VideoCheckBox.IsChecked == true) types |= ScanFileType.Video;
        if (ArchivesCheckBox.IsChecked == true) types |= ScanFileType.Archives;
        if (OtherCheckBox.IsChecked == true) types |= ScanFileType.Other;
        return types;
    }

    private ScanSetupSettings CaptureScanSetup()
    {
        long minimum = ToBytes(MinimumSizeNumberBox.Value);
        long? maximum = double.IsNaN(MaximumSizeNumberBox.Value) ? null : ToBytes(MaximumSizeNumberBox.Value);
        return AppSettingsService.Normalize(new ScanSetupSettings(
            activeProfileId,
            IncludeSubfoldersToggle.IsOn,
            GetSelectedFileTypes(),
            customExtensions.ToArray(),
            minimum,
            maximum,
            selectedRoots.Select(root => root.NormalizedPath).ToArray(),
            excludedFolders.ToArray(),
            excludedExtensions.ToArray(),
            savedProfiles.ToArray()));
    }

    private void PersistScanSetup()
    {
        if (!isApplyingSetup)
        {
            appSettings.SaveScanSetup(CaptureScanSetup());
        }
    }

    private static long ToBytes(double mebibytes) => double.IsNaN(mebibytes) || mebibytes <= 0
        ? 0
        : checked((long)Math.Round(mebibytes * 1048576d, MidpointRounding.AwayFromZero));

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (T value in values)
        {
            target.Add(value);
        }
    }

    private void ShowSetupNotice(string message, InfoBarSeverity severity)
    {
        ScanSetupNotice.Message = message;
        ScanSetupNotice.Severity = severity;
        ScanSetupNotice.IsOpen = true;
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

        AddSelectedSource(folder.Path);
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: SelectedScanRoot root })
        {
            selectedRoots.Remove(root);
            PersistScanSetup();
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
        scanAnnouncementGate.Reset();
        scanStopwatch.Restart();
        elapsedTimer.Start();
        var progress = new CoalescingUiProgress<ScanSessionProgress>(DispatcherQueue, update =>
        {
            if (!isDisposed && generation == activeScanGeneration)
            {
                UpdateScanProgress(update);
            }
        });
        ScanSetupSettings setup = CaptureScanSetup();
        ScanSessionResult result = await scanWorkflow.StartAsync(
            selectedRoots.Select(root => new ScanRoot(root.NormalizedPath)),
            setup.CreateDiscoveryPolicy(),
            progress);
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
        string stage = progress.State == ScanSessionState.Discovering ? Text("ScanDiscovering") : Text("ScanAnalyzing");
        // Announce only meaningful stage changes; high-frequency file progress remains visual.
        if (scanAnnouncementGate.ShouldAnnounce(stage))
        {
            ScanStageTitle.Text = stage;
        }
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
            ResultsViewModel.SelectionRejected -= OnResultsSelectionRejected;
        }

        ResultsViewModel = new ResultsReviewViewModel(result);
        ResultsViewModel.PropertyChanged += OnResultsViewModelPropertyChanged;
        ResultsViewModel.SelectionRejected += OnResultsSelectionRejected;
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
        ResultsSelectionNotice.IsOpen = false;
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
            ResultsViewModel.SelectionRejected -= OnResultsSelectionRejected;
        }

        ResultsViewModel = null;
        ResultsPage.DataContext = null;
        ResultsNavigationItem.IsEnabled = false;
        cleanupWorkflow.ResetForNewScan();
        ResultsStaleNotice.IsOpen = false;
        ResultsSelectionNotice.IsOpen = false;
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

    private void OnResultsSelectionRejected(object? sender, EventArgs args)
    {
        // This is deliberately an explicit, localized live status rather than a silent checkbox reset.
        ResultsSelectionNotice.IsOpen = true;
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
        bool hasFileCriteria = GetSelectedFileTypes() != ScanFileType.None || customExtensions.Count > 0;
        bool sizeRangeValid = double.IsNaN(MaximumSizeNumberBox.Value)
            || MaximumSizeNumberBox.Value >= MinimumSizeNumberBox.Value;
        ScanLocationsEmptyPanel.Visibility = hasRoots ? Visibility.Collapsed : Visibility.Visible;
        ScanLocationsEmptyText.Visibility = hasRoots ? Visibility.Collapsed : Visibility.Visible;
        LocationsList.Visibility = hasRoots ? Visibility.Visible : Visibility.Collapsed;
        CustomExtensionsList.Visibility = customExtensions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ExcludedFoldersList.Visibility = excludedFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ExcludedFoldersEmptyText.Visibility = excludedFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExcludedExtensionsList.Visibility = excludedExtensions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ExcludedExtensionsEmptyText.Visibility = excludedExtensions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartScanButton.IsEnabled = hasRoots && hasFileCriteria && sizeRangeValid && !scanWorkflow.IsRunning;
        ReviewSummaryText.Text = setupNotice ?? (selectedRoots.Count switch
        {
            0 => Text("ReviewEmpty"),
            1 => Text("ReviewWithOneRoot"),
            _ => string.Format(CultureInfo.CurrentCulture, ReviewWithManyRootsFormat, selectedRoots.Count),
        });

        int selectedTypeCount = Enum.GetValues<ScanFileType>()
            .Count(type => type is not (ScanFileType.None or ScanFileType.All) && GetSelectedFileTypes().HasFlag(type));
        string typeSummary = GetSelectedFileTypes() == ScanFileType.All
            ? Text("CriteriaAllTypes")
            : string.Format(CultureInfo.CurrentCulture, CriteriaSelectedTypesFormat, selectedTypeCount);
        double minimumSize = MinimumSizeNumberBox.Value;
        double maximumSize = MaximumSizeNumberBox.Value;
        string sizeSummary = minimumSize == 0 && double.IsNaN(maximumSize)
            ? Text("CriteriaAnySize")
            : double.IsNaN(maximumSize)
                ? string.Format(CultureInfo.CurrentCulture, CriteriaMinimumSizeFormat, minimumSize)
                : minimumSize == 0
                    ? string.Format(CultureInfo.CurrentCulture, CriteriaMaximumSizeFormat, maximumSize)
                    : string.Format(CultureInfo.CurrentCulture, CriteriaSizeRangeFormat, minimumSize, maximumSize);
        CriteriaSummaryText.Text = string.Format(
            CultureInfo.CurrentCulture,
            CriteriaSummaryFormat,
            typeSummary,
            customExtensions.Count,
            excludedFolders.Count + excludedExtensions.Count,
            sizeSummary);

        if (!hasFileCriteria)
        {
            ShowSetupNotice(Text("NoFileTypesNotice"), InfoBarSeverity.Warning);
        }
        else if (!sizeRangeValid)
        {
            ShowSetupNotice(Text("InvalidSizeRangeNotice"), InfoBarSeverity.Warning);
        }
    }

    private static string Text(string key) => ResourceLoader.GetString(key);

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        PageHost.SizeChanged -= OnPageHostSizeChanged;
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

    private sealed record SelectedScanRoot(string DisplayName, string NormalizedPath, string TypeAndCapacity, string Glyph);

    private sealed record DriveChoice(string RootPath, string DisplayName, string DisplayLabel, string Capacity);

    private sealed record ProfileChoice(string Id, string DisplayName, string Description, bool IsSaved);

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
