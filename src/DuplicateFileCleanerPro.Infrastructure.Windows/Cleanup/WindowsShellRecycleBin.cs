using System.Runtime.InteropServices;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.Cleanup;

internal sealed record WindowsRecycleBinResult(bool Succeeded, int? NativeErrorCode = null);

internal interface IWindowsRecycleBin
{
    Task<WindowsRecycleBinResult> RecycleAsync(string absolutePath, CancellationToken cancellationToken);
}

/// <summary>
/// The sole production destructive boundary. IFileOperation is always configured with
/// FOFX_RECYCLEONDELETE; failure never falls back to another deletion API.
/// </summary>
internal sealed class WindowsShellRecycleBin : IWindowsRecycleBin
{
    internal static uint RecycleOperationFlags =>
        FileOperationSilent |
        FileOperationNoConfirmation |
        FileOperationNoErrorUi |
        FileOperationNoRecursion |
        FileOperationNoConnectedElements |
        FileOperationEarlyFailure |
        FileOperationRecycleOnDelete |
        FileOperationAddUndoRecord;

    internal static uint RequiredRecycleFlag => FileOperationRecycleOnDelete;
    internal static uint RequiredUndoFlag => FileOperationAddUndoRecord;

    private const uint FileOperationSilent = 0x0004;
    private const uint FileOperationNoConfirmation = 0x0010;
    private const uint FileOperationNoErrorUi = 0x0400;
    private const uint FileOperationNoRecursion = 0x1000;
    private const uint FileOperationNoConnectedElements = 0x2000;
    private const uint FileOperationRecycleOnDelete = 0x00080000;
    private const uint FileOperationEarlyFailure = 0x00100000;
    private const uint FileOperationAddUndoRecord = 0x20000000;

    public Task<WindowsRecycleBinResult> RecycleAsync(string absolutePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<WindowsRecycleBinResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                completion.TrySetResult(RecycleOnStaThread(absolutePath));
            }
            catch (Exception exception)
            {
                completion.TrySetResult(new WindowsRecycleBinResult(false, exception.HResult));
            }
        })
        {
            IsBackground = true,
            Name = "DuplicateFileCleanerPro.RecycleBin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static WindowsRecycleBinResult RecycleOnStaThread(string absolutePath)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = (IFileOperation)(object)new FileOperationComObject();
            int result = operation.SetOperationFlags(RecycleOperationFlags);
            if (result < 0)
            {
                return new WindowsRecycleBinResult(false, result);
            }

            Guid shellItemId = typeof(IShellItem).GUID;
            result = SHCreateItemFromParsingName(absolutePath, IntPtr.Zero, ref shellItemId, out item);
            if (result < 0 || item is null)
            {
                return new WindowsRecycleBinResult(false, result);
            }

            result = operation.DeleteItem(item, IntPtr.Zero);
            if (result < 0)
            {
                return new WindowsRecycleBinResult(false, result);
            }

            result = operation.PerformOperations();
            if (result < 0)
            {
                return new WindowsRecycleBinResult(false, result);
            }

            result = operation.GetAnyOperationsAborted(out bool aborted);
            if (result < 0 || aborted || File.Exists(absolutePath))
            {
                return new WindowsRecycleBinResult(false, result < 0 ? result : null);
            }

            return new WindowsRecycleBinResult(true);
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private sealed class FileOperationComObject
    {
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr propertyChangeArray);
        [PreserveSig] int SetOwnerWindow(uint ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr progressSink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr progressSink);
        [PreserveSig] int MoveItems(IntPtr items, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string copyName, IntPtr progressSink);
        [PreserveSig] int CopyItems(IntPtr items, [MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder);
        [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, IntPtr progressSink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem([MarshalAs(UnmanagedType.Interface)] IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IntPtr progressSink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
