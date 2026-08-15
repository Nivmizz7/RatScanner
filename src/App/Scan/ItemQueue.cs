using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace RatScanner.Scan;

[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This type is the scanner's queue abstraction."
)]
public class ItemQueue : IEnumerable<ItemScan>
{
    private readonly object enqueueSync = new();
    private readonly ConcurrentQueue<ItemScan> queue = new();
    private readonly Queue<IReadOnlyList<ItemScan>> pendingNotifications = new();
    private bool isDrainingNotifications;
    public event EventHandler? Changed;
    internal event Action<IReadOnlyList<ItemScan>>? ItemsEnqueued;

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
        bool shouldDrain;
        lock (enqueueSync)
        {
            queue.Enqueue(item);
            pendingNotifications.Enqueue([item]);
            shouldDrain = !isDrainingNotifications;
            isDrainingNotifications = true;
        }

        if (shouldDrain)
            DrainNotifications();
    }

    public void EnqueueRange<T>(List<T> items)
        where T : ItemScan
    {
        bool shouldDrain;
        lock (enqueueSync)
        {
            items.ForEach(queue.Enqueue);
            pendingNotifications.Enqueue(items);
            shouldDrain = !isDrainingNotifications;
            isDrainingNotifications = true;
        }

        if (shouldDrain)
            DrainNotifications();
    }

    private void DrainNotifications()
    {
        Exception? firstError = null;
        while (true)
        {
            IReadOnlyList<ItemScan> scans;
            lock (enqueueSync)
            {
                if (pendingNotifications.Count == 0)
                {
                    isDrainingNotifications = false;
                    if (firstError is not null)
                        throw firstError;
                    return;
                }

                scans = pendingNotifications.Dequeue();
            }

            try
            {
                ItemsEnqueued?.Invoke(scans);
                OnChanged();
            }
            catch (Exception exception)
            {
                firstError ??= exception;
            }
        }
    }

    public int Count => queue.Count;

    IEnumerator IEnumerable.GetEnumerator() => queue.GetEnumerator();

    public IEnumerator<ItemScan> GetEnumerator() => queue.GetEnumerator();
}
