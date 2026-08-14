using System.Text.Json;
using DuplicateFileCleanerPro.Core.Services;

namespace DuplicateFileCleanerPro.Infrastructure.TemporaryData;

public sealed class OwnedTempDirectoryService(string rootDirectory) : IOwnedTempDirectoryService
{
    private const string MarkerFileName = ".dfcp-owner.json";
    private const int CurrentMarkerSchema = 1;

    public ValueTask<string> CreateSessionDirectoryAsync(string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(purpose) || purpose.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A valid session purpose is required.", nameof(purpose));

        var sessionId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(GetValidatedRoot(), purpose, sessionId);
        Directory.CreateDirectory(directory);
        var marker = new OwnershipMarker(CurrentMarkerSchema, sessionId, purpose, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(directory, MarkerFileName), JsonSerializer.Serialize(marker));
        return ValueTask.FromResult(directory);
    }

    public ValueTask CleanupStaleSessionsAsync(CancellationToken cancellationToken = default)
    {
        var root = GetValidatedRoot();
        if (!Directory.Exists(root)) return ValueTask.CompletedTask;
        foreach (var purposeDirectory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(purposeDirectory)) continue;
            foreach (var sessionDirectory in Directory.EnumerateDirectories(purposeDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDeleteOwnedSession(root, sessionDirectory);
            }
        }
        return ValueTask.CompletedTask;
    }

    private void TryDeleteOwnedSession(string root, string sessionDirectory)
    {
        try
        {
            if (IsReparsePoint(sessionDirectory)) return;
            var markerPath = Path.Combine(sessionDirectory, MarkerFileName);
            if (!File.Exists(markerPath)) return;
            var marker = JsonSerializer.Deserialize<OwnershipMarker>(File.ReadAllText(markerPath));
            if (marker is null || marker.SchemaVersion != CurrentMarkerSchema || !Guid.TryParseExact(marker.SessionId, "N", out _)) return;
            var expectedParent = Path.Combine(root, marker.Purpose);
            if (!Path.GetFullPath(sessionDirectory).StartsWith(Path.GetFullPath(expectedParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.EnumerateFileSystemEntries(sessionDirectory, "*", SearchOption.AllDirectories).Any(IsReparsePoint)) return;
            Directory.Delete(sessionDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private string GetValidatedRoot()
    {
        var fullRoot = Path.GetFullPath(rootDirectory);
        var appDataRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuplicateFileCleanerPro", "Temp"));
        if (!string.Equals(fullRoot, appDataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Temporary session root must be the app-owned local-data root.");
        return fullRoot;
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private sealed record OwnershipMarker(int SchemaVersion, string SessionId, string Purpose, DateTimeOffset CreatedUtc);
}
