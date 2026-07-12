using System;
using System.Linq;
using RatEye;
using RatStash;
using Icon = RatEye.Processing.Icon;

namespace RatScanner.Scan;

public class ItemIconScan : ItemScan
{
    public bool Rotated;
    public ItemExtraInfo ItemExtraInfo;
    public Vector2 ItemSize;

    private Vector2 _toolTipPosition;

    public ItemIconScan(Icon icon, Vector2 toolTipPosition, int duration)
    {
        RatStash.Item iconItem = icon.Item;
        Item =
            TarkovDevAPI.GetItems().FirstOrDefault(item => item.Id == iconItem.Id)
            ?? throw new Exception($"Unknown item: {icon.Item.Id}");
        ItemExtraInfo = icon.ItemExtraInfo;
        Confidence = icon.DetectionConfidence;
        Rotated = icon.Rotated;
        ItemSize = icon.ItemSize;
        IconPath = icon.IconPath;

        _toolTipPosition = toolTipPosition;
        DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + duration;
    }

    public override Vector2 GetToolTipPosition()
    {
        return _toolTipPosition;
    }
}
