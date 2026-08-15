using Windows.Storage;

namespace DuplicateFileCleanerPro.App.Settings;

/// <summary>Packaged-app local preference storage for appearance and reusable scan setup; it stores no scan history or results.</summary>
public sealed class WindowsAppSettingsStore : IAppSettingsStore
{
    private readonly ApplicationDataContainer values = ApplicationData.Current.LocalSettings;

    public string? Read(string key) => values.Values.TryGetValue(key, out object? value) ? value as string : null;

    public void Write(string key, string value) => values.Values[key] = value;
}
