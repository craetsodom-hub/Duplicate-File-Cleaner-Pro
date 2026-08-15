using System.Text.Json;
using System.Collections.ObjectModel;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.App.Settings;

public sealed record SavedScanProfile(
    string Id,
    string Name,
    bool IncludeSubfolders,
    ScanFileType FileTypes,
    IReadOnlyList<string> CustomExtensions,
    long MinimumSizeBytes,
    long? MaximumSizeBytes,
    IReadOnlyList<string> ExcludedFolders,
    IReadOnlyList<string> ExcludedExtensions)
{
    public ScanCriteria CreateCriteria() => new(FileTypes, CustomExtensions, MinimumSizeBytes, MaximumSizeBytes);
}

public sealed record ScanSetupSettings(
    string ActiveProfileId,
    bool IncludeSubfolders,
    ScanFileType FileTypes,
    IReadOnlyList<string> CustomExtensions,
    long MinimumSizeBytes,
    long? MaximumSizeBytes,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> ExcludedFolders,
    IReadOnlyList<string> ExcludedExtensions,
    IReadOnlyList<SavedScanProfile> SavedProfiles)
{
    public static ScanSetupSettings Default { get; } = new(
        PremiumScanProfiles.AllFilesId,
        true,
        ScanFileType.All,
        [],
        0,
        null,
        [],
        [],
        [],
        []);

    public ScanCriteria CreateCriteria() => new(FileTypes, CustomExtensions, MinimumSizeBytes, MaximumSizeBytes);

    public DiscoveryPolicy CreateDiscoveryPolicy() => new(
        IncludeSubfolders: IncludeSubfolders,
        Criteria: CreateCriteria(),
        ExcludedFolders: ExcludedFolders,
        ExcludedExtensions: ExcludedExtensions);
}

public sealed partial class AppSettingsService
{
    public const string ScanSetupKey = "PremiumScanSetupV1";
    private const int MaximumSources = 64;
    private const int MaximumExtensions = 128;
    private const int MaximumExcludedFolders = 128;
    private const int MaximumSavedProfiles = 24;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
    };

    public ScanSetupSettings LoadScanSetup()
    {
        string? value = store.Read(ScanSetupKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            return ScanSetupSettings.Default;
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<ScanSetupSettings>(value));
        }
        catch (JsonException)
        {
            return ScanSetupSettings.Default;
        }
        catch (NotSupportedException)
        {
            return ScanSetupSettings.Default;
        }
    }

    public void SaveScanSetup(ScanSetupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        store.Write(ScanSetupKey, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
    }

    public static ScanSetupSettings Normalize(ScanSetupSettings? settings)
    {
        if (settings is null)
        {
            return ScanSetupSettings.Default;
        }

        ScanFileType fileTypes = (settings.FileTypes & ~ScanFileType.All) == 0
            ? settings.FileTypes
            : ScanFileType.All;
        long minimum = Math.Max(0, settings.MinimumSizeBytes);
        long? maximum = settings.MaximumSizeBytes is long candidate && candidate >= minimum ? candidate : null;
        IReadOnlyList<string> customExtensions = NormalizeExtensions(settings.CustomExtensions, MaximumExtensions);
        IReadOnlyList<string> excludedExtensions = NormalizeExtensions(settings.ExcludedExtensions, MaximumExtensions);
        IReadOnlyList<string> sources = NormalizePaths(settings.Sources, MaximumSources);
        IReadOnlyList<string> excludedFolders = NormalizePaths(settings.ExcludedFolders, MaximumExcludedFolders);

        List<SavedScanProfile> profiles = [];
        foreach (SavedScanProfile profile in settings.SavedProfiles ?? [])
        {
            if (profiles.Count >= MaximumSavedProfiles)
            {
                break;
            }

            string id = (profile.Id ?? string.Empty).Trim();
            string name = (profile.Name ?? string.Empty).Trim();
            if (id.Length is < 1 or > 80 || name.Length is < 1 or > 64
                || PremiumScanProfiles.Find(id) is not null
                || profiles.Any(existing => existing.Id.Equals(id, StringComparison.Ordinal)))
            {
                continue;
            }

            ScanFileType profileTypes = (profile.FileTypes & ~ScanFileType.All) == 0 ? profile.FileTypes : ScanFileType.All;
            long profileMinimum = Math.Max(0, profile.MinimumSizeBytes);
            long? profileMaximum = profile.MaximumSizeBytes is long profileMax && profileMax >= profileMinimum ? profileMax : null;
            profiles.Add(new SavedScanProfile(
                id,
                name,
                profile.IncludeSubfolders,
                profileTypes,
                NormalizeExtensions(profile.CustomExtensions, MaximumExtensions),
                profileMinimum,
                profileMaximum,
                NormalizePaths(profile.ExcludedFolders, MaximumExcludedFolders),
                NormalizeExtensions(profile.ExcludedExtensions, MaximumExtensions)));
        }

        string activeProfileId = settings.ActiveProfileId ?? PremiumScanProfiles.CustomId;
        if (PremiumScanProfiles.Find(activeProfileId) is null
            && !activeProfileId.Equals(PremiumScanProfiles.CustomId, StringComparison.Ordinal)
            && !profiles.Any(profile => profile.Id.Equals(activeProfileId, StringComparison.Ordinal)))
        {
            activeProfileId = PremiumScanProfiles.CustomId;
        }

        return new ScanSetupSettings(
            activeProfileId,
            settings.IncludeSubfolders,
            fileTypes,
            customExtensions,
            minimum,
            maximum,
            sources,
            excludedFolders,
            excludedExtensions,
            Array.AsReadOnly(profiles.ToArray()));
    }

    private static ReadOnlyCollection<string> NormalizeExtensions(IEnumerable<string>? extensions, int maximumCount) =>
        Array.AsReadOnly((extensions ?? [])
            .Select(ScanCriteria.NormalizeExtension)
            .Where(extension => extension is not null)
            .Select(extension => extension!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximumCount)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static ReadOnlyCollection<string> NormalizePaths(IEnumerable<string>? paths, int maximumCount) =>
        Array.AsReadOnly((paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Where(path => path.Length <= 32767)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximumCount)
            .ToArray());
}
