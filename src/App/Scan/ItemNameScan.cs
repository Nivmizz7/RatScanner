using System;
using System.Linq;
using RatEye;
using RatEye.Processing;

namespace RatScanner.Scan;

public class ItemNameScan : ItemScan
{
    private Vector2 _toolTipPosition;

    public ItemNameScan(Inspection inspection, Vector2 toolTipPosition, int duration)
    {
        RatStash.Item inspectionItem = inspection.Item ?? throw new InvalidOperationException("No item was detected.");
        Item =
            TarkovDevAPI.GetItems().FirstOrDefault(item => item.Id == inspectionItem.Id)
            ?? throw new InvalidOperationException($"Unknown item: {inspection.Item.Id}");
        Confidence = inspection.ItemConfidence;
        IconPath = inspection.IconPath ?? string.Empty;
        _toolTipPosition = toolTipPosition;
        DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + duration;
    }

    public override Vector2 GetToolTipPosition()
    {
        return _toolTipPosition;
    }
}
