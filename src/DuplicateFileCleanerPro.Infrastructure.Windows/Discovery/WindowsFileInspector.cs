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
    private const int ErrorMoreData = 234;
    private const int StreamInfoBufferSize = 64 * 1024;

    public static bool TryInspect(string path, out FileSnapshot? snapshot)
    {
        snapshot = null;
        using SafeFileHandle handle = CreateFile(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        return TryInspect(handle, out snapshot);
    }

    public static bool TryInspect(SafeFileHandle handle, out FileSnapshot? snapshot)
    {
        snapshot = null;
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            return false;
        }

        if (HasAdditionalNamedStream(handle))
        {
            snapshot = new FileSnapshot(
                new PhysicalFileIdentity(information.VolumeSerialNumber, ComposeFileId(information.FileIndexHigh, information.FileIndexLow)),
                ComposeLength(information.FileSizeHigh, information.FileSizeLow),
                DateTimeOffset.FromFileTime(ComposeFileTime(information.LastWriteTimeHigh, information.LastWriteTimeLow)),
                true);
            return true;
        }

        snapshot = new FileSnapshot(
            new PhysicalFileIdentity(information.VolumeSerialNumber, ComposeFileId(information.FileIndexHigh, information.FileIndexLow)),
            ComposeLength(information.FileSizeHigh, information.FileSizeLow),
            DateTimeOffset.FromFileTime(ComposeFileTime(information.LastWriteTimeHigh, information.LastWriteTimeLow)),
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
                uint nextOffset = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                uint nameLength = unchecked((uint)Marshal.ReadInt32(buffer, offset + sizeof(uint)));
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

    private static ulong ComposeFileId(uint high, uint low) => ((ulong)high << 32) | low;

    private static long ComposeLength(uint high, uint low) => checked((long)(((ulong)high << 32) | low));

    private static long ComposeFileTime(uint high, uint low) => unchecked((long)(((ulong)high << 32) | low));

    internal sealed record FileSnapshot(PhysicalFileIdentity Identity, long Length, DateTimeOffset LastWriteTimeUtc, bool HasAdditionalNamedStream);

    private enum FileInfoByHandleClass
    {
        FileStreamInfo = 7,
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
}
