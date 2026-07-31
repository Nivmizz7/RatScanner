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
        TarkovTrackerDB db = RatScannerMain.Instance.TarkovTrackerDB;
        List<UserProgress> progress = db.Progress;
        if (progress.Count >= 1)
        {
            string self = db.Self;
            return progress.FirstOrDefault(x => x.UserId == self) ?? new UserProgress();
        }
        return new UserProgress();
    }

    public static (int count, int kappaCount) GetTaskRemaining(this Item item, UserProgress? progress = null)
    {
        QuestNeedReport report = item.GetQuestNeedReport(progress);
        return (report.CurrentTotal, report.KappaTotal);
    }

    /// <summary>
    /// Full applicability-aware quest need classification for the item.
    /// Does not inspect the scanned item's FIR state (no vision yet).
    /// </summary>
    internal static QuestNeedReport GetQuestNeedReport(this Item item, UserProgress? progress = null) =>
        QuestNeedClassifier.Classify(
            item,
            TarkovDevAPI.GetTasks(),
            progress ?? GetUserProgress(),
            RatConfig.Tracking.ShowNonFIRNeeds
        );

    /// <summary>
    /// Remaining active quest needs, split by whether the objective requires found-in-raid.
    /// </summary>
    public static RequirementBreakdown GetTaskRequirementBreakdown(this Item item, UserProgress? progress = null)
    {
        progress ??= GetUserProgress();
        bool showNonFir = RatConfig.Tracking.ShowNonFIRNeeds;

        TarkovDev.Task[] tasks = TarkovDevAPI.GetTasks();
        IReadOnlyDictionary<string, TarkovDev.Task> tasksById = ToTaskMap(tasks);

        int fir = 0;
        int nonFir = 0;
        foreach (TarkovDev.Task task in tasks)
        {
            if (task.Objectives == null)
                continue;
            // Keep behavior consistent with the report: never count finished/invalid/future tasks here.
            if (QuestNeedClassifier.ClassifyGate(task, tasksById, progress, out _) != QuestGate.ApplicableNow)
                continue;

            RequirementBreakdown taskNeed = GetTaskRequirementBreakdown(item, task.Objectives, progress, showNonFir);
            fir += taskNeed.FoundInRaid;
            nonFir += taskNeed.NonFoundInRaid;
        }

        return new RequirementBreakdown(fir + nonFir, fir, nonFir);
    }

    internal static IReadOnlyDictionary<string, TarkovDev.Task> ToTaskMap(IReadOnlyList<TarkovDev.Task> tasks) =>
        tasks.Where(t => !string.IsNullOrEmpty(t.Id)).ToDictionary(t => t.Id);

    /// <summary>Objective-level remaining counts for one task.</summary>
    internal readonly record struct ObjectiveNeedBreakdown(
        int Total,
        int FoundInRaid,
        int NonFoundInRaid,
        int WeaponHandIn
    );

    internal static ObjectiveNeedBreakdown GetObjectiveNeedBreakdown(
        Item item,
        IReadOnlyList<TaskObjective>? objectives,
        UserProgress progress,
        bool showNonFir
    )
    {
        RequirementBreakdown breakdown = GetTaskRequirementBreakdown(
            item,
            objectives,
            progress,
            showNonFir,
            out int weaponHandIn
        );
        return new ObjectiveNeedBreakdown(
            breakdown.Total,
            breakdown.FoundInRaid,
            breakdown.NonFoundInRaid,
            weaponHandIn
        );
    }

    internal static RequirementBreakdown GetTaskRequirementBreakdown(
        Item item,
        IReadOnlyList<TaskObjective>? objectives,
        UserProgress progress,
        bool showNonFir
    ) => GetTaskRequirementBreakdown(item, objectives, progress, showNonFir, out _);

    private static RequirementBreakdown GetTaskRequirementBreakdown(
        Item item,
        IReadOnlyList<TaskObjective>? objectives,
        UserProgress progress,
        bool showNonFir,
        out int weaponHandIn
    )
    {
        weaponHandIn = 0;
        if (objectives == null)
            return new RequirementBreakdown(0, 0, 0);

        int fir = 0;
        int nonFir = 0;
        bool isWeapon = item.Types?.Contains("gun", StringComparer.OrdinalIgnoreCase) == true;
        Dictionary<TaskObjective, TaskObjective> pairedFindByGive = [];
        HashSet<TaskObjective> pairedFindObjectives = [];

        foreach (
            TaskObjective giveObjective in objectives.Where(objective =>
                !objective.Optional && objective.Type == "giveItem" && objective.ItemIds?.Contains(item.Id) == true
            )
        )
        {
            // Current tarkov.dev find/give pairs share count and FIR flags;
            // loosen this match only if the upstream data starts emitting asymmetric pairs.
            TaskObjective? pairedFind = objectives.FirstOrDefault(candidate =>
                !candidate.Optional
                && candidate.Type == "findItem"
                && candidate.Count == giveObjective.Count
                && candidate.FoundInRaid == giveObjective.FoundInRaid
                && candidate.ItemIds?.Contains(item.Id) == true
                && !pairedFindObjectives.Contains(candidate)
            );
            if (pairedFind == null)
                continue;
            pairedFindByGive[giveObjective] = pairedFind;
            pairedFindObjectives.Add(pairedFind);
        }

        foreach (TaskObjective objective in objectives)
        {
            // Tarkov exposes "find" and "hand over" as separate objectives for the same
            // physical items. Count the pair once using whichever objective is further along.
            if (pairedFindObjectives.Contains(objective))
                continue;

            int needed = RemainingForObjective(item, objective, progress, showNonFir, out bool requiresFir);
            if (pairedFindByGive.TryGetValue(objective, out TaskObjective? pairedFind))
            {
                int findRemaining = RemainingForObjective(item, pairedFind, progress, showNonFir, out _);
                needed = Math.Min(needed, findRemaining);
            }
            if (needed <= 0)
                continue;

            if (requiresFir)
                fir += needed;
            else
                nonFir += needed;

            if (isWeapon && objective.Type is "giveItem" or "buildWeapon")
                weaponHandIn += needed;
        }

        return new RequirementBreakdown(fir + nonFir, fir, nonFir);
    }

    private static int RemainingForObjective(
        Item item,
        TaskObjective objective,
        UserProgress progress,
        bool showNonFir,
        out bool requiresFir
    )
    {
        requiresFir = false;
        int needed = 0;

        // Optional objectives are nice-to-have, never a requirement.
        if (objective.Optional)
            return 0;

        if (objective.Type is "giveItem" or "findItem" or "plantItem")
        {
            if (objective.ItemIds == null || !objective.ItemIds.Contains(item.Id))
                return 0;

            requiresFir = (objective.Type is "giveItem" or "findItem") && objective.FoundInRaid;
            // plantItem is treated as non-FIR requirement (same as legacy).
            if (!showNonFir && !requiresFir)
                return 0;
            if (!showNonFir && objective.Type == "plantItem")
                return 0;

            needed = objective.Count;
            foreach (Progress p in progress.TaskObjectives.Where(p => p.Id == objective.Id))
                needed -= p.Complete ? objective.Count : p.Count;
            return Math.Max(0, needed);
        }

        if (objective.Type is "mark" or "buildWeapon")
        {
            if (objective.MarkerItemId != item.Id && objective.BuildItemId != item.Id)
                return 0;
            if (!showNonFir)
                return 0;
            requiresFir = false;
            needed = Math.Max(1, objective.Count);
            foreach (Progress p in progress.TaskObjectives.Where(p => p.Id == objective.Id))
                needed -= 1;
            return Math.Max(0, needed);
        }

        return 0;
    }

    public static int GetHideoutRemaining(this Item item, UserProgress? progress = null) =>
        item.GetHideoutRequirementBreakdown(progress).Total;

    /// <summary>
    /// Remaining hideout upgrade needs, split by FIR attribute on the station item requirement.
    /// </summary>
    public static RequirementBreakdown GetHideoutRequirementBreakdown(this Item item, UserProgress? progress = null)
    {
        progress ??= GetUserProgress();

        int fir = 0;
        int nonFir = 0;
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
                    foreach (Progress p in progress.HideoutParts.Where(p => p.Id == requiredItem.Id))
                        remaining -= p.Complete ? requiredItem.Count : p.Count;
                    remaining = Math.Max(0, remaining);
                    if (remaining <= 0)
                        continue;

                    if (requiredItem.FoundInRaid)
                        fir += remaining;
                    else
                        nonFir += remaining;
                }
            }
        }

        return new RequirementBreakdown(fir + nonFir, fir, nonFir);
    }

    public static AcquisitionInfo GetAcquisitionInfo(this Item item)
    {
        int crafts = TarkovDevAPI.CraftRecipeCount(item.Id);
        int barters = TarkovDevAPI.BarterOfferCount(item.Id);
        return new AcquisitionInfo(crafts > 0, crafts, barters > 0, barters);
    }

    public static int GetAvg24hMarketPricePerSlot(this Item item)
    {
        int price = item.Avg24HPrice ?? 0;
        int size = Math.Max(1, item.Width * item.Height);
        return price / size;
    }

    public static ItemSellPrice? GetBestTraderOffer(this Item item) => item.SellFor?.MaxBy(i => i.PriceRub);

    public static TraderOffer? GetBestTraderOfferVendor(this Item item) => GetBestTraderOffer(item)?.Vendor;

    public static string GetWikiLink(this Item item)
    {
        if (item.WikiLink?.Length > 3)
            return item.WikiLink;

        string pageName = (item.Name ?? string.Empty).Replace(" ", "_", StringComparison.Ordinal);
        // gamepedia.com no longer redirects reliably; fandom hosts the live wiki.
        return $"https://escapefromtarkov.fandom.com/wiki/{Uri.EscapeDataString(pageName)}";
    }

    public static IEnumerable<Item> GetAmmoOfSameCaliber(this Item item)
    {
        if (item.Properties is not { IsAmmo: true } ammo || string.IsNullOrEmpty(ammo.Caliber))
            return Enumerable.Empty<Item>();
        return TarkovDevAPI
            .GetItems()
            .Where(i =>
                i.Properties is { IsAmmo: true } a && string.Equals(ammo.Caliber, a.Caliber, StringComparison.Ordinal)
            );
    }
}
