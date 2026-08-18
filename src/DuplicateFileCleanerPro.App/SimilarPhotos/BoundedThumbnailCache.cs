namespace DuplicateFileCleanerPro.App.SimilarPhotos;

/// <summary>Session-only LRU cache with a deterministic byte and entry budget.</summary>
public sealed class BoundedThumbnailCache<T> : IDisposable where T : class
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> leastRecentlyUsed = [];
    private readonly int maximumEntries;
    private readonly long maximumBytes;
    private readonly CancellationTokenSource sessionCancellation = new();
    private long bytes;
    private long generation;
    private bool disposed;

    public BoundedThumbnailCache(int maximumEntries, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1L);
        this.maximumEntries = maximumEntries;
        this.maximumBytes = maximumBytes;
    }

    public int Count { get { lock (gate) return entries.Count; } }
    public long ApproximateBytes { get { lock (gate) return bytes; } }
    public long Generation { get { lock (gate) return generation; } }

    public async Task<T?> GetOrCreateAsync(string key, long estimatedBytes, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfLessThan(estimatedBytes, 1L);
        long requestGeneration;
        lock (gate)
        {
            ThrowIfDisposed();
            if (entries.TryGetValue(key, out Entry? entry))
            {
                Touch(entry);
                return entry.Value;
            }
            requestGeneration = generation;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionCancellation.Token);
        T? value = await factory(linked.Token).ConfigureAwait(true);
        if (value is null || linked.IsCancellationRequested) return null;
        lock (gate)
        {
            if (disposed || requestGeneration != generation) return null;
            if (entries.TryGetValue(key, out Entry? existing)) { Touch(existing); return existing.Value; }
            LinkedListNode<string> node = leastRecentlyUsed.AddFirst(key);
            entries.Add(key, new Entry(value, estimatedBytes, node));
            bytes = checked(bytes + estimatedBytes);
            Evict();
            return entries.TryGetValue(key, out Entry? retained) ? retained.Value : null;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            generation++;
            entries.Clear();
            leastRecentlyUsed.Clear();
            bytes = 0;
        }
    }

    private void Evict()
    {
        while (entries.Count > maximumEntries || bytes > maximumBytes)
        {
            LinkedListNode<string>? node = leastRecentlyUsed.Last;
            if (node is null) break;
            Entry entry = entries[node.Value];
            entries.Remove(node.Value);
            leastRecentlyUsed.Remove(node);
            bytes -= entry.Bytes;
        }
    }

    private void Touch(Entry entry)
    {
        leastRecentlyUsed.Remove(entry.Node);
        leastRecentlyUsed.AddFirst(entry.Node);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sessionCancellation.Cancel();
        sessionCancellation.Dispose();
        entries.Clear();
        leastRecentlyUsed.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private sealed record Entry(T Value, long Bytes, LinkedListNode<string> Node);
}
