using System.ComponentModel;
using System.Runtime.CompilerServices;
using DuplicateFileCleanerPro.Core.Similarity;

namespace DuplicateFileCleanerPro.App.Settings;

public enum AppearancePreference { System, Light, Dark }

public interface IAppSettingsStore
{
    string? Read(string key);
    void Write(string key, string value);
}

public sealed partial class AppSettingsService(IAppSettingsStore store)
{
    public const string AppearanceKey = "AppearancePreference";
    public const string SimilarPhotosSensitivityKey = "SimilarPhotosSensitivityV1";
    private readonly IAppSettingsStore store = store ?? throw new ArgumentNullException(nameof(store));

    public AppearancePreference LoadAppearance()
    {
        string? value = store.Read(AppearanceKey);
        return Enum.TryParse(value, ignoreCase: true, out AppearancePreference appearance)
            && Enum.IsDefined(appearance)
            ? appearance
            : AppearancePreference.System;
    }

    public void SaveAppearance(AppearancePreference appearance)
    {
        if (!Enum.IsDefined(appearance)) appearance = AppearancePreference.System;
        store.Write(AppearanceKey, appearance.ToString());
    }

    public SimilarPhotoSensitivity LoadSimilarPhotosSensitivity() =>
        Enum.TryParse(store.Read(SimilarPhotosSensitivityKey), ignoreCase: false, out SimilarPhotoSensitivity sensitivity)
        && Enum.IsDefined(sensitivity) ? sensitivity : SimilarPhotoSensitivity.Balanced;

    public void SaveSimilarPhotosSensitivity(SimilarPhotoSensitivity sensitivity)
    {
        if (!Enum.IsDefined(sensitivity)) sensitivity = SimilarPhotoSensitivity.Balanced;
        store.Write(SimilarPhotosSensitivityKey, sensitivity.ToString());
    }
}

/// <summary>Session presentation state for the single persisted v1 preference.</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppSettingsService settings;
    private readonly Action<AppearancePreference> applyAppearance;
    private AppearancePreference appearance;

    public SettingsViewModel(AppSettingsService settings, Action<AppearancePreference> applyAppearance)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.applyAppearance = applyAppearance ?? throw new ArgumentNullException(nameof(applyAppearance));
        appearance = settings.LoadAppearance();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public AppearancePreference Appearance => appearance;

    public bool SetAppearance(AppearancePreference value)
    {
        if (!Enum.IsDefined(value) || appearance == value) return false;
        appearance = value;
        settings.SaveAppearance(value);
        applyAppearance(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Appearance)));
        return true;
    }
}
