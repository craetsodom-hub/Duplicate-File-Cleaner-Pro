using System.Globalization;
using System.Text;

namespace DuplicateFileCleanerPro.Core.FolderIntelligence;

public static class FolderIntelligenceExporter
{
    public static string CreateDuplicateFolderCsv(DuplicateFolderScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        AppendCsvRow(builder, "Group", "Folder root", "Logical file count", "Independent physical files", "Total logical bytes", "Potential reclaimable bytes");
        for (int groupIndex = 0; groupIndex < result.Groups.Count; groupIndex++)
        {
            VerifiedDuplicateFolderGroup group = result.Groups[groupIndex];
            foreach (FolderTreeSnapshot folder in group.MemberFolders)
            {
                AppendCsvRow(builder,
                    (groupIndex + 1).ToString(CultureInfo.InvariantCulture),
                    folder.RootPath,
                    folder.LogicalFileCount.ToString(CultureInfo.InvariantCulture),
                    folder.IndependentPhysicalFileCount.ToString(CultureInfo.InvariantCulture),
                    folder.TotalLogicalBytes.ToString(CultureInfo.InvariantCulture),
                    group.PotentialReclaimableBytes.ToString(CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    public static string CreateDuplicateFolderText(DuplicateFolderScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        for (int groupIndex = 0; groupIndex < result.Groups.Count; groupIndex++)
        {
            VerifiedDuplicateFolderGroup group = result.Groups[groupIndex];
            builder.Append("Group ").AppendLine((groupIndex + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append("Logical files: ").AppendLine(group.LogicalFileCount.ToString(CultureInfo.InvariantCulture));
            builder.Append("Independent physical files: ").AppendLine(group.IndependentPhysicalFileCount.ToString(CultureInfo.InvariantCulture));
            builder.Append("Potential reclaimable bytes: ").AppendLine(group.PotentialReclaimableBytes.ToString(CultureInfo.InvariantCulture));
            foreach (FolderTreeSnapshot folder in group.MemberFolders)
            {
                builder.Append("  ").AppendLine(folder.RootPath);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string CreateComparisonCsv(FolderComparisonTargetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        AppendCsvRow(builder, "Target", "Status", "Relative path", "Master path", "Compared path", "Master size", "Compared size", "Master modified UTC", "Compared modified UTC");
        foreach (FolderComparisonRow row in result.Rows)
        {
            AppendCsvRow(builder,
                result.TargetRoot,
                row.Status.ToString(),
                row.RelativePath,
                row.MasterFile?.File.NormalizedPath ?? string.Empty,
                string.Join(" | ", row.ComparedFiles.Select(file => file.File.NormalizedPath)),
                row.MasterSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.ComparedSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.MasterModifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.ComparedModifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return builder.ToString();
    }

    public static string CreateComparisonText(FolderComparisonTargetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.Append("Master: ").AppendLine(result.MasterRoot);
        builder.Append("Compared: ").AppendLine(result.TargetRoot);
        builder.AppendLine();
        foreach (FolderComparisonRow row in result.Rows)
        {
            builder.Append(row.Status).Append(" | ").Append(row.RelativePath).AppendLine();
            builder.Append("  Master: ").AppendLine(row.MasterFile?.File.NormalizedPath ?? "(none)");
            builder.Append("  Compared: ").AppendLine(row.ComparedFiles.Count == 0 ? "(none)" : string.Join(" | ", row.ComparedFiles.Select(file => file.File.NormalizedPath)));
        }

        return builder.ToString();
    }

    private static void AppendCsvRow(StringBuilder builder, params string[] values)
    {
        builder.AppendLine(string.Join(',', values.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
