using DuplicateFileCleanerPro.App.SimilarPhotos;

namespace DuplicateFileCleanerPro.Core.Tests;

[TestClass]
public sealed class BoundedThumbnailCacheTests
{
    [TestMethod]
    public async Task EvictsLeastRecentlyUsedEntriesAtDeterministicBudget()
    {
        using var cache = new BoundedThumbnailCache<object>(2, 20);
        object first = new();
        object second = new();
        object third = new();
        await cache.GetOrCreateAsync("first", 10, _ => Task.FromResult<object?>(first));
        await cache.GetOrCreateAsync("second", 10, _ => Task.FromResult<object?>(second));
        await cache.GetOrCreateAsync("first", 10, _ => Task.FromResult<object?>(new object()));
        await cache.GetOrCreateAsync("third", 10, _ => Task.FromResult<object?>(third));

        Assert.AreEqual(2, cache.Count);
        Assert.AreEqual(20, cache.ApproximateBytes);
        Assert.AreSame(first, await cache.GetOrCreateAsync("first", 10, _ => Task.FromResult<object?>(new object())));
        Assert.AreNotSame(second, await cache.GetOrCreateAsync("second", 10, _ => Task.FromResult<object?>(new object())));
    }

    [TestMethod]
    public async Task ResetRejectsStaleInFlightThumbnail()
    {
        using var cache = new BoundedThumbnailCache<object>(4, 40);
        var ready = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object?> request = cache.GetOrCreateAsync("photo", 10, _ => ready.Task);
        cache.Clear();
        ready.SetResult(new object());
        Assert.IsNull(await request);
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public async Task FiveThousandDemandRequestsRemainWithinThumbnailBudget()
    {
        using var cache = new BoundedThumbnailCache<object>(128, 16L * 1024 * 1024);
        const long bytesPerThumbnail = 160L * 160 * 4;
        for (int index = 0; index < 5000; index++)
        {
            int item = index;
            _ = await cache.GetOrCreateAsync($"photo-{item}", bytesPerThumbnail, _ => Task.FromResult<object?>(new object()));
        }

        Assert.IsLessThanOrEqualTo(128, cache.Count);
        Assert.IsLessThanOrEqualTo(16L * 1024 * 1024, cache.ApproximateBytes);
    }
}
