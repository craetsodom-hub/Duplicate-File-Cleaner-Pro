using DuplicateFileCleanerPro.Phase0.Core;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DuplicateFileCleanerPro.Phase0.Harness;

internal static partial class Program
{
    private const string OwnershipFileName = ".dfcp-phase0-owner";

    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro", "phase0", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, OwnershipFileName), root);

        try
        {
            IdentityProof(root);
            ExactPipelineProof(root);
            SqliteProof(root);
            RecycleBinProof(root);
            JournalProof();
            Console.WriteLine("PHASE 0 HARNESS: PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            DeleteOwnedRoot(root);
        }
    }

    private static void IdentityProof(string root)
    {
        var original = Path.Combine(root, "identity-original.bin");
        var hardLink = Path.Combine(root, "identity-hardlink.bin");
        var renamed = Path.Combine(root, "identity-renamed.bin");
        File.WriteAllBytes(original, [1, 2, 3, 4]);
        Assert(CreateHardLink(hardLink, original, IntPtr.Zero), "Could not create a hard link.");
        var originalIdentity = GetPhysicalIdentity(original);
        Assert(originalIdentity == GetPhysicalIdentity(hardLink), "Hard-linked paths did not share an identity.");
        File.Move(original, renamed);
        Assert(originalIdentity == GetPhysicalIdentity(renamed), "Rename did not retain physical identity.");
        File.Delete(renamed);
        File.WriteAllBytes(renamed, [4, 3, 2, 1]);
        Assert(originalIdentity != GetPhysicalIdentity(renamed), "Replacement did not change physical identity.");
        Console.WriteLine("0A identity: PASS");
    }

    private static void ExactPipelineProof(string root)
    {
        var sameFirst = Path.Combine(root, "same-first.bin");
        var sameSecond = Path.Combine(root, "same-second.other");
        var differentMiddle = Path.Combine(root, "different-middle.bin");
        var sameSampleDifferent = Path.Combine(root, "same-sample-different.bin");
        var uniqueSize = Path.Combine(root, "unique-size.bin");
        var bytes = Enumerable.Range(0, 24 * 1024).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(sameFirst, bytes);
        File.WriteAllBytes(sameSecond, bytes);
        bytes[12 * 1024] ^= 0xFF;
        File.WriteAllBytes(differentMiddle, bytes);
        bytes = File.ReadAllBytes(sameFirst);
        bytes[6 * 1024] ^= 0xFF;
        File.WriteAllBytes(sameSampleDifferent, bytes);
        File.WriteAllBytes(uniqueSize, [9]);

        var files = new[]
        {
            Candidate(sameFirst), Candidate(sameSecond), Candidate(differentMiddle), Candidate(sameSampleDifferent), Candidate(uniqueSize)
        };
        var realGroups = new ExactDuplicateProbe().FindExactGroups(files);
        Assert(realGroups.Count == 1 && realGroups[0].Members.Count == 2, "The exact pipeline did not isolate the exact pair.");

        var collisionGroups = new ExactDuplicateProbe(_ => new byte[32]).FindExactGroups(files);
        Assert(collisionGroups.Count == 1 && collisionGroups[0].Members.Count == 2, "A forced full-hash collision created a false duplicate group.");
        Console.WriteLine("0B exact pipeline and collision guard: PASS");
    }

    private static void SqliteProof(string root)
    {
        var databasePath = Path.Combine(root, "session-index.db");
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE files (id INTEGER PRIMARY KEY, normalized_path TEXT NOT NULL, size_bytes INTEGER NOT NULL); CREATE INDEX ix_files_size ON files(size_bytes);";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO files(normalized_path, size_bytes) VALUES ($path, $size);";
        var path = command.CreateParameter();
        path.ParameterName = "$path";
        command.Parameters.Add(path);
        var size = command.CreateParameter();
        size.ParameterName = "$size";
        command.Parameters.Add(size);
        for (var index = 0; index < 10_000; index++)
        {
            path.Value = $"C:\\synthetic\\{index:D5}.bin";
            size.Value = index % 17 == 0 ? 1024L : index;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        command.Transaction = null;
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM files WHERE size_bytes = 1024;";
        var candidates = Convert.ToInt32(command.ExecuteScalar());
        Assert(candidates > 1, "SQLite candidate query returned an unexpected result.");
        Console.WriteLine("0D SQLite 10,000-record session index: PASS");
    }

    private static void RecycleBinProof(string root)
    {
        var target = Path.Combine(root, "recycle-proof.bin");
        File.WriteAllBytes(target, [7, 7, 7]);
        EnsureOwnedFile(root, target);
        FileSystem.DeleteFile(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        Assert(!File.Exists(target), "Recycle Bin request did not remove the generated test file.");
        Console.WriteLine("0C Recycle Bin request without permanent fallback: PASS");
    }

    private static void JournalProof()
    {
        var journal = new Dictionary<string, string>(StringComparer.Ordinal) { ["pending-item"] = "pending", ["succeeded-item"] = "succeeded" };
        ReconcileWithoutResumingDeletion(journal);
        Assert(journal["pending-item"] == "needs-review" && journal["succeeded-item"] == "succeeded", "Journal reconciliation changed an unsafe state.");
        var once = string.Join(';', journal.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        ReconcileWithoutResumingDeletion(journal);
        var twice = string.Join(';', journal.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        Assert(once == twice, "Journal reconciliation was not idempotent.");
        Console.WriteLine("0G journal reconciliation: PASS");
    }

    private static void ReconcileWithoutResumingDeletion(IDictionary<string, string> journal)
    {
        foreach (var item in journal.Where(pair => pair.Value == "pending").Select(pair => pair.Key).ToArray())
        {
            journal[item] = "needs-review";
        }
    }

    private static ProbeFile Candidate(string path) => new(path, new FileInfo(path).Length, GetPhysicalIdentity(path));

    private static string GetPhysicalIdentity(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        var info = new FileIdInfo();
        Assert(GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, ref info, (uint)Marshal.SizeOf<FileIdInfo>()), $"File identity lookup failed for {path}: {Marshal.GetLastWin32Error()}");
        unsafe
        {
            byte* fileId = info.FileId;
            return $"{info.VolumeSerialNumber:X16}:{Convert.ToHexString(new ReadOnlySpan<byte>(fileId, 16))}";
        }
    }

    private static void EnsureOwnedFile(string root, string path)
    {
        Assert(Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Recycle target is outside the generated root.");
        Assert(File.Exists(Path.Combine(root, OwnershipFileName)), "Generated root ownership marker is missing.");
        Assert((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Recycle target is a reparse point.");
    }

    private static void DeleteOwnedRoot(string root)
    {
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, OwnershipFileName)))
        {
            return;
        }

        var expectedPrefix = Path.Combine(Path.GetTempPath(), "DuplicateFileCleanerPro", "phase0") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Temporary Phase 0 probe cleanup will be retried later: {exception.GetType().Name}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, FileInfoByHandleClass fileInformationClass, ref FileIdInfo fileInformation, uint bufferSize);

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }
}
