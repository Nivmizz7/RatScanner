using System;
using System.Collections.Generic;
using System.Linq;
using RatScanner.Scan;

namespace RatScanner.Presentation;

internal sealed class SessionHistoryService : IDisposable
{
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

    internal ScanResultViewModel? Selected { get; set; }
    internal event EventHandler? Changed;

    internal SessionHistoryService(ItemQueue itemScans, Func<ItemScan, ScanResultViewModel> mapResult)
    {
        _itemScans = itemScans;
        _mapResult = mapResult;
        _itemScans.Changed += OnItemScansChanged;
    }

    private void OnItemScansChanged(object? sender, EventArgs e)
    {
        ItemScan? scan = _itemScans.LastOrDefault();
        if (scan is null || scan.IsSeed)
            return;

        Record(_mapResult(scan));
    }

    internal void Record(ScanResultViewModel result)
    {
        lock (_sync)
        {
            _items.RemoveAll(existing => existing.Item.Id == result.Item.Id);
            _items.Insert(0, result with { ScannedAt = DateTimeOffset.Now, IsHistoricalResult = true });
            if (_items.Count > 50)
                _items.RemoveRange(50, _items.Count - 50);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _itemScans.Changed -= OnItemScansChanged;
}
