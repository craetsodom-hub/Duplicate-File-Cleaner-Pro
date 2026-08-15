using Windows.Storage;

namespace DuplicateFileCleanerPro.App.Settings;

/// <summary>Minimal packaged-app local preference storage; it stores no product/session data.</summary>
public sealed class WindowsAppSettingsStore : IAppSettingsStore
{
    private readonly ApplicationDataContainer values = ApplicationData.Current.LocalSettings;

    public string? Read(string key) => values.Values.TryGetValue(key, out object? value) ? value as string : null;

    public void Write(string key, string value) => values.Values[key] = value;
}
