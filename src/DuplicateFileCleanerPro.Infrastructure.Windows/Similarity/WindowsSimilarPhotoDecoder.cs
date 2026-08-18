using System.Runtime.InteropServices;
using DuplicateFileCleanerPro.Core.Discovery;
using DuplicateFileCleanerPro.Core.Similarity;
using DuplicateFileCleanerPro.Infrastructure.Windows.Discovery;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DuplicateFileCleanerPro.Infrastructure.Windows.Similarity;

/// <summary>Read-only Windows Imaging Component decoder with bounded output and snapshot validation.</summary>
public sealed class WindowsSimilarPhotoDecoder : ISimilarPhotoDecoder
{
    private const int CodecNotFound = unchecked((int)0x88982F50);
    private static readonly SemaphoreSlim DecoderGate = new(1, 1);

    public async Task<PhotoDecodeOutcome> DecodeAsync(DiscoveredFile file, int maximumDimension, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (maximumDimension is < 8 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        BitmapDecoder? decoder = null;
        await DecoderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesDiscoverySnapshot(file)) return PhotoDecodeOutcome.Failure(SimilarPhotoSkipReason.ChangedDuringAnalysis);

            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(file.NormalizedPath).AsTask(cancellationToken).ConfigureAwait(false);
            using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false);
            decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            BitmapTransform transform = await CreateTransformAsync(decoder, maximumDimension, cancellationToken).ConfigureAwait(false);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            int byteCount = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
            var buffer = new global::Windows.Storage.Streams.Buffer((uint)byteCount);
            bitmap.CopyToBuffer(buffer);
            byte[] pixels = new byte[byteCount];
            using DataReader reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(pixels);
            if (!MatchesDiscoverySnapshot(file)) return PhotoDecodeOutcome.Failure(SimilarPhotoSkipReason.ChangedDuringAnalysis);
            return PhotoDecodeOutcome.Success(new PhotoAnalysisImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return PhotoDecodeOutcome.Failure(SimilarPhotoSkipReason.Inaccessible);
        }
        catch (UnauthorizedAccessException)
        {
            return PhotoDecodeOutcome.Failure(SimilarPhotoSkipReason.Inaccessible);
        }
        catch (COMException exception) when (exception.HResult == CodecNotFound)
        {
            return PhotoDecodeOutcome.Failure(SimilarPhotoSkipReason.CodecUnavailable);
        }
        catch (Exception exception) when (exception is IOException or COMException or ArgumentException or InvalidOperationException)
        {
            return PhotoDecodeOutcome.Failure(SimilarPhotoSkipReason.CorruptImage);
        }
        finally
        {
            // BitmapDecoder is a WinRT object and can retain a mapped section after its input stream closes.
            // Release it before callers replace a source file during a scan session.
            if (decoder is not null)
            {
                try { Marshal.FinalReleaseComObject(decoder); }
                catch (Exception exception) when (exception is InvalidComObjectException or ArgumentException) { }
            }

            DecoderGate.Release();
        }
    }

    private static async Task<BitmapTransform> CreateTransformAsync(BitmapDecoder decoder, int maximumDimension, CancellationToken token)
    {
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        double scale = Math.Min(1, maximumDimension / (double)Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(width * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        try
        {
            BitmapPropertySet properties = await decoder.BitmapProperties
                .GetPropertiesAsync(["/app1/ifd/{ushort=274}"])
                .AsTask(token).ConfigureAwait(false);
            if (properties.TryGetValue("/app1/ifd/{ushort=274}", out BitmapTypedValue? orientationValue))
            {
                ushort orientation = Convert.ToUInt16(orientationValue.Value, System.Globalization.CultureInfo.InvariantCulture);
                ApplyOrientation(transform, orientation);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or FormatException or OverflowException)
        {
            // Absence or invalid EXIF orientation means the stored pixel orientation is used.
        }
        return transform;
    }

    internal static void ApplyOrientation(BitmapTransform transform, ushort orientation)
    {
        switch (orientation)
        {
            case 2: transform.Flip = BitmapFlip.Horizontal; break;
            case 3: transform.Rotation = BitmapRotation.Clockwise180Degrees; break;
            case 4: transform.Flip = BitmapFlip.Vertical; break;
            case 5: transform.Rotation = BitmapRotation.Clockwise90Degrees; transform.Flip = BitmapFlip.Horizontal; break;
            case 6: transform.Rotation = BitmapRotation.Clockwise90Degrees; break;
            case 7: transform.Rotation = BitmapRotation.Clockwise270Degrees; transform.Flip = BitmapFlip.Horizontal; break;
            case 8: transform.Rotation = BitmapRotation.Clockwise270Degrees; break;
        }
    }

    private static bool MatchesDiscoverySnapshot(DiscoveredFile file)
    {
        try
        {
            using var stream = new FileStream(file.NormalizedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            return WindowsFileInspector.TryInspect(stream.SafeFileHandle, out WindowsFileInspector.FileSnapshot? snapshot)
                && snapshot is { HasAdditionalNamedStream: false }
                && snapshot.Identity == file.PhysicalIdentity
                && snapshot.Length == file.Length
                && snapshot.LastWriteTimeUtc == file.LastWriteTimeUtc
                && snapshot.ChangeTimeUtc == file.ChangeTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
