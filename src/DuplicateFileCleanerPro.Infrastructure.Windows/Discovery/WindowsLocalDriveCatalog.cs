namespace DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

public sealed record LocalDriveSource(
    string RootPath,
    string DisplayName,
    DriveType DriveType,
    string DriveFormat,
    long TotalSize,
    long AvailableFreeSpace);

/// <summary>Enumerates ready local storage without probing network or unavailable volumes.</summary>
public static class WindowsLocalDriveCatalog
{
    public static IReadOnlyList<LocalDriveSource> GetAvailableDrives()
    {
        List<LocalDriveSource> result = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                {
                    continue;
                }

                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd(Path.DirectorySeparatorChar)})";
                result.Add(new LocalDriveSource(
                    drive.RootDirectory.FullName,
                    label,
                    drive.DriveType,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return Array.AsReadOnly(result.ToArray());
    }
}
