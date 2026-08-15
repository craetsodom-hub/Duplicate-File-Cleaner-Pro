namespace DuplicateFileCleanerPro.App.Settings;

public static class AppVersionFormatter
{
    public static string Format(ushort major, ushort minor, ushort build, ushort revision) =>
        $"{major}.{minor}.{build}.{revision}";
}
