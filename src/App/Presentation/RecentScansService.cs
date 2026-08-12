using System;
using System.Collections.Generic;
using RatScanner.Scan;

namespace RatScanner.Presentation;

internal sealed class RecentScansService : IDisposable
{
    private const int Capacity = 5;

    private readonly object _sync = new();
    private readonly List<ScanResultViewModel> _items = new();
    private readonly ItemQueue _itemScans;
    private readonly Func<ItemScan, ScanResultViewModel> _mapResult;

    internal IReadOnlyList<ScanResultViewModel> Items
    {
        get
        {
            lock (_sync)
                return _items.ToArray();
        }
    }

    internal event EventHandler? Changed;

    internal RecentScansService(ItemQueue itemScans, Func<ItemScan, ScanResultViewModel> mapResult)
    {
        _itemScans = itemScans;
        _mapResult = mapResult;
        _itemScans.ItemsEnqueued += OnItemsEnqueued;
    }

    private void OnItemsEnqueued(IReadOnlyList<ItemScan> scans)
    {
        foreach (ItemScan scan in scans)
        {
            if (!scan.IsSeed)
                Record(_mapResult(scan));
        }
    }

    private void Record(ScanResultViewModel result)
    {
        lock (_sync)
        {
            _items.RemoveAll(existing => existing.Item.Id == result.Item.Id);
            _items.Insert(0, result with { ScannedAt = DateTimeOffset.Now, IsHistoricalResult = true });
            if (_items.Count > Capacity)
                _items.RemoveRange(Capacity, _items.Count - Capacity);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _itemScans.ItemsEnqueued -= OnItemsEnqueued;
}
