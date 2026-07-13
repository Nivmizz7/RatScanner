using System;
using System.Collections.Generic;
using System.Linq;
using RatScanner.FetchModels.TarkovTracker;
using RatScanner.TarkovDev;

namespace RatScanner;

public static class ItemExtensions
{
    private static UserProgress GetUserProgress()
    {
        UserProgress? progress = null;
        if (RatConfig.Tracking.TarkovTracker.Enable && RatScannerMain.Instance.TarkovTrackerDB.Progress.Count >= 1)
        {
            List<UserProgress> teamProgress = RatScannerMain.Instance.TarkovTrackerDB.Progress;
            progress = teamProgress.FirstOrDefault(x => x.UserId == RatScannerMain.Instance.TarkovTrackerDB.Self);
        }
        return progress ?? new UserProgress();
    }

    public static (int count, int kappaCount) GetTaskRemaining(this Item item, UserProgress? progress = null)
    {
        // Compensation for Damage Tasks
        // These tasks are not tracked by TarkovTracker
        string[] excludedTasks =
        [
            "61e6e5e0f5b9633f6719ed95",
            "61e6e60223374d168a4576a6",
            "61e6e621bfeab00251576265",
            "61e6e615eea2935bc018a2c5",
            "61e6e60c5ca3b3783662be27",
        ];

        progress ??= GetUserProgress();

        int needed = 0;
        int count = 0;
        int kappaCount = 0;

        bool showNonFir = RatConfig.Tracking.ShowNonFIRNeeds;

        TarkovDev.Task[] tasks = TarkovDevAPI.GetTasks();

        foreach (TarkovDev.Task task in tasks)
        {
            if (progress.Tasks.Any(p => p.Id == task.Id && p.Complete))
                continue;

            if (excludedTasks.Contains(task.Id))
                continue;

            if (task.Objectives == null)
                continue;

            foreach (TaskObjective objective in task.Objectives)
            {
                if (objective.Type is "giveItem" or "findItem" or "plantItem")
                {
                    if (objective.ItemIds == null || !objective.ItemIds.Contains(item.Id))
                        continue;
                    // FIR gate: hide non-FIR needs unless user opted in.
                    // Retained for future non-FIR "sell it" recommendations when quest requires FIR.
                    if (!showNonFir && !objective.FoundInRaid && objective.Type is "giveItem" or "findItem")
                        continue;
                    if (!showNonFir && objective.Type == "plantItem")
                        continue;

                    needed = objective.Count;
                    List<Progress> objectiveProgress = progress.TaskObjectives.Where(p => p.Id == objective.Id).ToList();
                    foreach (Progress p in objectiveProgress)
                        needed -= p.Complete ? objective.Count : p.Count;
                    needed = Math.Max(0, needed);
                    count += needed;
                    if (task.KappaRequired == true)
                        kappaCount += needed;
                }
                else if (objective.Type == "mark")
                {
                    if (objective.MarkerItemId != item.Id)
                        continue;
                    if (!showNonFir)
                        continue;
                    needed = Math.Max(1, objective.Count);
                    List<Progress> objectiveProgress = progress.TaskObjectives.Where(p => p.Id == objective.Id).ToList();
                    foreach (Progress p in objectiveProgress)
                        needed -= 1;
                    needed = Math.Max(0, needed);
                    count += needed;
                    if (task.KappaRequired == true)
                        kappaCount += needed;
                }
                else if (objective.Type == "buildWeapon")
                {
                    if (objective.BuildItemId != item.Id)
                        continue;
                    if (!showNonFir)
                        continue;
                    needed = Math.Max(1, objective.Count);
                    List<Progress> objectiveProgress = progress.TaskObjectives.Where(p => p.Id == objective.Id).ToList();
                    foreach (Progress p in objectiveProgress)
                        needed -= 1;
                    needed = Math.Max(0, needed);
                    count += needed;
                    if (task.KappaRequired == true)
                        kappaCount += needed;
                }
            }
        }
        return (count, kappaCount);
    }

    public static int GetHideoutRemaining(this Item item, UserProgress? progress = null)
    {
        progress ??= GetUserProgress();

        int count = 0;
        HideoutStation[] stations = TarkovDevAPI.GetHideoutStations();

        foreach (HideoutStation station in stations)
        {
            if (station.Levels == null)
                continue;
            foreach (HideoutStationLevel? level in station.Levels)
            {
                if (level == null)
                    continue;

                if (progress.HideoutModules.Any(p => p.Id == level.Id && p.Complete))
                    continue;

                if (level.ItemRequirements == null)
                    continue;
                foreach (RequirementItem requiredItem in level.ItemRequirements)
                {
                    if (requiredItem.ItemId != item.Id)
                        continue;

                    int remaining = requiredItem.Count;
                    List<Progress> objectiveProgress = progress.HideoutParts.Where(p => p.Id == requiredItem.Id).ToList();
                    foreach (Progress p in objectiveProgress)
                        remaining -= p.Complete ? requiredItem.Count : p.Count;
                    count += Math.Max(0, remaining);
                }
            }
        }
        return count;
    }

    public static int GetAvg24hMarketPricePerSlot(this Item item)
    {
        int price = item.Avg24HPrice ?? 0;
        int size = Math.Max(1, item.Width * item.Height);
        return price / size;
    }

    public static ItemSellPrice? GetBestTraderOffer(this Item item) =>
        item.SellFor?.MaxBy(i => i.PriceRub);

    public static TraderOffer? GetBestTraderOfferVendor(this Item item) => GetBestTraderOffer(item)?.Vendor;

    public static string GetWikiLink(this Item item)
    {
        if (item.WikiLink?.Length > 3)
            return item.WikiLink;

        string pageName = (item.Name ?? string.Empty).Replace(" ", "_", StringComparison.Ordinal);
        return $"https://escapefromtarkov.gamepedia.com/{Uri.EscapeDataString(pageName)}";
    }

    public static IEnumerable<Item> GetAmmoOfSameCaliber(this Item item)
    {
        if (item.Properties is not { IsAmmo: true } ammo || string.IsNullOrEmpty(ammo.Caliber))
            return Enumerable.Empty<Item>();
        return TarkovDevAPI
            .GetItems()
            .Where(i =>
                i.Properties is { IsAmmo: true } a
                && string.Equals(ammo.Caliber, a.Caliber, StringComparison.Ordinal)
            );
    }
}
