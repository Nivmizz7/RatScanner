using RatEye;
using RatEye.Processing;
using System;
using TarkovItem = RatScanner.TarkovDev.GraphQL.Item;

namespace RatScanner.Scan;

public class ItemNameScan : ItemScan {
	private Vector2 _toolTipPosition;

	public ItemNameScan(Inspection inspection, Vector2 toolTipPosition, int duration) {
		RatStash.Item inspectionItem = inspection.Item;
		if (!TarkovDevAPI.TryGetItemById(inspectionItem.Id, out TarkovItem? tarkovItem) || tarkovItem is null) {
			throw new Exception($"Unknown item: {inspectionItem.Id}");
		}

		Item = tarkovItem;
		Confidence = inspection.MarkerConfidence;
		IconPath = inspection.IconPath;
		_toolTipPosition = toolTipPosition;
		DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + duration;
	}

	public override Vector2 GetToolTipPosition() {
		return _toolTipPosition;
	}
}
