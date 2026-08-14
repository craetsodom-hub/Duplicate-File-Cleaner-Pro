using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DuplicateFileCleanerPro.Core.Discovery;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;

internal sealed class WindowsFileInspector
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorMoreData = 234;
    private const int StreamInfoBufferSize = 64 * 1024;

    public static bool TryInspect(string path, out FileSnapshot? snapshot)
        => TryInspect(path, FileFlagOpenReparsePoint, out snapshot);

    public static bool TryInspectDirectory(string path, out FileSnapshot? snapshot)
        => TryInspect(path, FileFlagOpenReparsePoint | FileFlagBackupSemantics, out snapshot);

    private static bool TryInspect(string path, uint flags, out FileSnapshot? snapshot)
    {
        snapshot = null;
        using SafeFileHandle handle = CreateFile(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);

        return TryInspect(handle, out snapshot);
    }

    public static bool TryInspect(SafeFileHandle handle, out FileSnapshot? snapshot)
    {
        snapshot = null;
        if (handle.IsInvalid
            || !GetFileInformationByHandle(handle, out ByHandleFileInformation information)
            || !GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileBasicInfo, out FileBasicInformation basicInformation, Marshal.SizeOf<FileBasicInformation>())
            || !GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, out FileIdInformation identityInformation, Marshal.SizeOf<FileIdInformation>()))
        {
            return false;
        }

        if (basicInformation.ChangeTime <= 0)
        {
            return false;
        }

        PhysicalFileIdentity identity = new(identityInformation.VolumeSerialNumber, identityInformation.FileId.Low, identityInformation.FileId.High);
        if ((information.FileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            snapshot = new FileSnapshot(identity, ComposeLength(information.FileSizeHigh, information.FileSizeLow), DateTimeOffset.FromFileTime(basicInformation.LastWriteTime), DateTimeOffset.FromFileTime(basicInformation.ChangeTime), information.FileAttributes, information.NumberOfLinks, false);
            return true;
        }

        if (HasAdditionalNamedStream(handle))
        {
            snapshot = new FileSnapshot(
                identity,
                ComposeLength(information.FileSizeHigh, information.FileSizeLow),
                DateTimeOffset.FromFileTime(basicInformation.LastWriteTime),
                DateTimeOffset.FromFileTime(basicInformation.ChangeTime),
                information.FileAttributes,
                information.NumberOfLinks,
                true);
            return true;
        }

        snapshot = new FileSnapshot(
            identity,
            ComposeLength(information.FileSizeHigh, information.FileSizeLow),
            DateTimeOffset.FromFileTime(basicInformation.LastWriteTime),
            DateTimeOffset.FromFileTime(basicInformation.ChangeTime),
            information.FileAttributes,
            information.NumberOfLinks,
            false);
        return true;
    }

    private static bool HasAdditionalNamedStream(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(StreamInfoBufferSize);
        try
        {
            if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStreamInfo, buffer, StreamInfoBufferSize))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorMoreData)
                {
                    return true;
                }

                // Stream inspection cannot be trusted, so the caller treats this as an unsafe item.
                throw new Win32Exception(error);
            }

            int offset = 0;
            while (true)
            {
                if (offset < 0 || offset > StreamInfoBufferSize - 24)
                {
                    throw new InvalidOperationException("Invalid file stream metadata offset.");
                }

                uint nextOffset = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                uint nameLength = unchecked((uint)Marshal.ReadInt32(buffer, offset + sizeof(uint)));
                if ((nameLength & 1) != 0 || nameLength > StreamInfoBufferSize - offset - 24)
                {
                    throw new InvalidOperationException("Invalid file stream metadata length.");
                }

                string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 24), checked((int)nameLength / sizeof(char)));
                if (!string.Equals(name, "::$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (nextOffset == 0)
                {
                    return false;
                }

                offset += checked((int)nextOffset);
                if (offset >= StreamInfoBufferSize)
                {
                    throw new InvalidOperationException("Invalid file stream metadata.");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long ComposeLength(uint high, uint low) => checked((long)(((ulong)high << 32) | low));

    internal sealed record FileSnapshot(PhysicalFileIdentity Identity, long Length, DateTimeOffset LastWriteTimeUtc, DateTimeOffset ChangeTimeUtc, FileAttributes Attributes, uint NumberOfLinks, bool HasAdditionalNamedStream);

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileStreamInfo = 7,
        FileIdInfo = 18,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public FileAttributes FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInformation fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileBasicInformation fileInformation,
        int bufferSize);
}
