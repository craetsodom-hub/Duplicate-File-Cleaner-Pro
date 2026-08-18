using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace DuplicateFileCleanerPro.App.SimilarPhotos;

/// <summary>Demand-driven local thumbnails. Images are decoded at a bounded size and retained only for the active session.</summary>
public sealed class WindowsThumbnailService : IDisposable
{
    private const int DecodeDimension = 160;
    private const int MaximumEntries = 128;
    private const long MaximumBytes = 16L * 1024 * 1024;
    private readonly BoundedThumbnailCache<BitmapImage> cache = new(MaximumEntries, MaximumBytes);

    public int CacheEntries => cache.Count;
    public long ApproximateCacheBytes => cache.ApproximateBytes;
    public long Generation => cache.Generation;

    public Task<BitmapImage?> GetAsync(string path, CancellationToken cancellationToken = default) =>
        cache.GetOrCreateAsync(path, DecodeDimension * DecodeDimension * 4L, async token =>
        {
            if (!File.Exists(path)) return null;
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                using Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenReadAsync().AsTask(token);
                var bitmap = new BitmapImage { DecodePixelWidth = DecodeDimension, DecodePixelHeight = DecodeDimension };
                await bitmap.SetSourceAsync(stream);
                token.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch (Exception) when (token.IsCancellationRequested || !File.Exists(path)) { return null; }
            catch (Exception) { return null; }
        }, cancellationToken);

    public void ResetSession() => cache.Clear();
    public void Dispose() => cache.Dispose();
}
