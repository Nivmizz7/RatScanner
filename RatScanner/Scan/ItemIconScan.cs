using RatEye;
using RatStash;
using System;
using Icon = RatEye.Processing.Icon;
using TarkovItem = RatScanner.TarkovDev.GraphQL.Item;

namespace RatScanner.Scan;

public class ItemIconScan : ItemScan {
	public bool Rotated;
	public ItemExtraInfo ItemExtraInfo;
	public Icon Icon;

	private Vector2 _toolTipPosition;

	public ItemIconScan(Icon icon, Vector2 toolTipPosition, int duration) {
		Icon = icon;
		RatStash.Item iconItem = icon.Item;
		if (!TarkovDevAPI.TryGetItemById(iconItem.Id, out TarkovItem? tarkovItem) || tarkovItem is null) {
			throw new Exception($"Unknown item: {iconItem.Id}");
		}

		Item = tarkovItem;
		ItemExtraInfo = icon.ItemExtraInfo;
		Confidence = icon.DetectionConfidence;
		Rotated = icon.Rotated;
		IconPath = icon.IconPath;

		_toolTipPosition = toolTipPosition;
		DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + duration;
	}

	public override Vector2 GetToolTipPosition() {
		return _toolTipPosition;
	}
}
