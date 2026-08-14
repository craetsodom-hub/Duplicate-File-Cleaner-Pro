using System.Text.Json;
using DuplicateFileCleanerPro.Core.Models;
using DuplicateFileCleanerPro.Core.Services;

namespace DuplicateFileCleanerPro.Infrastructure.Settings;

public sealed class JsonSettingsService(string settingsDirectory) : ISettingsService
{
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(settingsDirectory, SettingsFileName);
        if (!File.Exists(path)) return AppSettings.Default;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken) ?? AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
        catch (IOException)
        {
            return AppSettings.Default;
        }
    }

    public async ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(settingsDirectory);
        var finalPath = Path.Combine(settingsDirectory, SettingsFileName);
        var temporaryPath = Path.Combine(settingsDirectory, $"{SettingsFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
