using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace RatScanner.Scan;

public class ItemQueue : IEnumerable<ItemScan>
{
    private readonly ConcurrentQueue<ItemScan> queue = new();
    public event EventHandler? Changed;

    protected virtual void OnChanged()
    {
        PruneExpired(DateTimeOffset.Now.ToUnixTimeMilliseconds());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal bool PruneExpired(long now)
    {
        bool removed = false;
        while (queue.Count > 1 && queue.TryPeek(out ItemScan? first) && first.DissapearAt <= now)
        {
            if (!queue.TryDequeue(out _))
                break;
            removed = true;
        }
        return removed;
    }

    internal long? GetNextExpiration(long now) =>
        queue.Where(scan => scan.DissapearAt > now).Select(scan => (long?)scan.DissapearAt).Min();

    public virtual void Enqueue(ItemScan item)
    {
        queue.Enqueue(item);
        OnChanged();
    }

    public void EnqueueRange<T>(List<T> items)
        where T : ItemScan
    {
        items.ForEach(queue.Enqueue);
        OnChanged();
    }

    public int Count => queue.Count;

    IEnumerator IEnumerable.GetEnumerator() => queue.GetEnumerator();

    public IEnumerator<ItemScan> GetEnumerator() => queue.GetEnumerator();
}
