using System;
using System.Collections.Generic;

namespace RatScanner.Presentation;

internal sealed class SessionHistoryService
{
    private readonly List<ScanResultViewModel> _items = new();
    internal IReadOnlyList<ScanResultViewModel> Items => _items;
    internal ScanResultViewModel? Selected { get; set; }

    internal void Record(ScanResultViewModel result)
    {
        _items.RemoveAll(existing => existing.Item.Id == result.Item.Id);
        _items.Insert(0, result with { ScannedAt = DateTimeOffset.Now, IsHistoricalResult = true });
        if (_items.Count > 50) _items.RemoveRange(50, _items.Count - 50);
    }
}
