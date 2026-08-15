using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using RatScanner.Presentation;
using RatScanner.Runtime;
using RatScanner.Scan;
using RatScanner.TarkovDev;

namespace RatScanner.ViewModel;

internal class MenuVM : INotifyPropertyChanged
{
    private readonly IScanOrchestrator _scanOrchestrator;
    private readonly ITrackerService _trackerService;
    private readonly DefaultItemScan _placeholderScan;

    // Derived-value cache: quest/hideout classification iterates the whole catalog
    // (tasks + hideout stations) and used to re-run on every property read — several
    // times per Blazor render across main page, overlay, and minimal menu bindings.
    // Compute once per (scan, tracker snapshot) pair. ITrackerService.State returns a
    // fresh TrackerStateSnapshot record per read, so reference equality is a reliable
    // "tracker data changed" signal without an explicit version.
    private ItemScan? _derivedSourceScan;
    private TrackerStateSnapshot? _derivedSourceState;
    private DerivedScanState? _derivedState;

    internal sealed class DerivedScanState
    {
        internal (int Count, int KappaCount) TaskRemaining;
        internal int HideoutRemaining;
        internal List<KeyValuePair<string, KeyValuePair<int, int>>>? TeamNeeds;
    }

    public ItemQueue ItemScans => _scanOrchestrator.ItemScans;

    internal FetchModels.TarkovTracker.UserProgress CurrentUserProgress => _trackerService.State.CurrentUser;

    public ItemScan LastItemScan => ItemScans.LastOrDefault() ?? _placeholderScan;

    public Item LastItem => LastItemScan.Item;

    // WPF resolves these through the instance DataContext; making them static breaks bindings.
#pragma warning disable CA1822
    /// <summary>Numeric product version from csproj.</summary>
    public string Version => RatConfig.Version;

    /// <summary>Sidebar/about display, e.g. <c>v4.0.0-beta.1</c>.</summary>
    public string VersionDisplay => RatConfig.VersionDisplay;
#pragma warning restore CA1822

    public string Updated =>
        DateTime.TryParse(
            LastItem.Updated,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTime updated
        )
            ? updated.ToLocalTime().ToString(CultureInfo.CurrentCulture)
            : string.Empty;

    public TimeSpan? DataAge
    {
        get
        {
            if (
                !DateTime.TryParse(
                    LastItem.Updated ?? string.Empty,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var updated
                )
            )
                return null;
            return DateTime.UtcNow - updated.ToUniversalTime();
        }
    }

    public int FleaPrice => LastItem.Avg24HPrice ?? 0;

    public int TraderPrice => BestTraderOffer?.PriceRub ?? 0;

    public int FleaVsTraderDifference => FleaPrice - TraderPrice;

    public bool FleaBetterThanTrader => FleaPrice > TraderPrice && TraderPrice > 0;

    public bool HasTraderOffer => BestTraderOffer != null;

    public bool HasFleaOffer => FleaPrice > 0;

    public bool RecommendFlea => HasFleaOffer && (!HasTraderOffer || FleaPrice > TraderPrice);

    // These localized labels are instance-bound and refreshed through INotifyPropertyChanged.
#pragma warning disable CA1822
    public string FleaMarketLabel => PresentationText.T("FleaMarket", "Flea Market");

    public string PricePerSlotLabel => PresentationText.T("PricePerSlot", "Price per Slot");

    public string KappaNeededLabel => PresentationText.T("KappaNeeded", "Kappa Needed");

    public string NeededItemsLabel => PresentationText.T("NeededQuestHideout", "Needed Quest & Hideout");

    public string UpdatedTimestampLabel => PresentationText.T("UpdatedTimestamp", "Updated Timestamp");
#pragma warning restore CA1822

    public string WikiLink => LastItem.GetWikiLink();

    public int PricePerSlot => LastItem.GetAvg24hMarketPricePerSlot();

    public ItemSellPrice? BestTraderOffer => LastItem.GetBestTraderOffer();
    public TraderOffer? BestTraderOfferVendor => LastItem.GetBestTraderOfferVendor();

    public (int count, int kappaCount) TaskRemainingResult => Derived.TaskRemaining;

    public int TaskRemaining => Derived.TaskRemaining.Count;

    public int TaskRemainingKappa => Derived.TaskRemaining.KappaCount;

    public bool KappaNeeded => TaskRemainingKappa > 0;

    public int HideoutRemaining => Derived.HideoutRemaining;

    public bool ItemNeeded => TaskRemaining + HideoutRemaining > 0;

    public static bool ShowKappaNeeds => RatConfig.Tracking.ShowKappaNeeds;

    public List<KeyValuePair<string, KeyValuePair<int, int>>>? ItemTeamNeeds => Derived.TeamNeeds;

    public (int task, int hideout) ItemTeamNeedsSummed
    {
        get
        {
            List<KeyValuePair<string, KeyValuePair<int, int>>>? needs = Derived.TeamNeeds;
            return (needs?.Sum(i => i.Value.Key) ?? 0, needs?.Sum(i => i.Value.Value) ?? 0);
        }
    }

    public bool ItemTeamNeeded => Derived.TeamNeeds is { Count: > 0 };
    public event PropertyChangedEventHandler? PropertyChanged;

    internal MenuVM(IScanOrchestrator scanOrchestrator, ITrackerService trackerService)
    {
        _scanOrchestrator = scanOrchestrator ?? throw new ArgumentNullException(nameof(scanOrchestrator));
        _trackerService = trackerService ?? throw new ArgumentNullException(nameof(trackerService));
        _placeholderScan = CreatePlaceholderScan();
        _scanOrchestrator.PropertyChanged += ModelPropertyChanged;
    }

    private static DefaultItemScan CreatePlaceholderScan() =>
        new(
            new Item
            {
                Id = "loading",
                Name = "Loading...",
                ShortName = "Loading...",
                Width = 1,
                Height = 1,
            },
            isSeed: true
        );

    /// <summary>
    /// Quest/hideout/team classification for the current scan, computed once per
    /// (scan, tracker snapshot) pair and reused across property reads.
    /// </summary>
    internal DerivedScanState Derived
    {
        get
        {
            ItemScan scan = LastItemScan;
            TrackerStateSnapshot trackerState = _trackerService.State;
            if (!ReferenceEquals(_derivedSourceScan, scan) || !ReferenceEquals(_derivedSourceState, trackerState))
            {
                _derivedSourceScan = scan;
                _derivedSourceState = trackerState;
                _derivedState = ComputeDerivedState(scan, trackerState);
            }

            return _derivedState!;
        }
    }

    private static DerivedScanState ComputeDerivedState(ItemScan scan, TrackerStateSnapshot trackerState)
    {
        Item item = scan.Item;
        FetchModels.TarkovTracker.UserProgress currentUser = trackerState.CurrentUser;
        (int taskCount, int kappaCount) = item.GetTaskRemaining(currentUser);
        return new DerivedScanState
        {
            TaskRemaining = (taskCount, kappaCount),
            HideoutRemaining = item.GetHideoutRemaining(currentUser),
            TeamNeeds = ComputeTeamNeeds(item, trackerState),
        };
    }

    private static List<KeyValuePair<string, KeyValuePair<int, int>>>? ComputeTeamNeeds(
        Item item,
        TrackerStateSnapshot trackerState
    )
    {
        IReadOnlyList<FetchModels.TarkovTracker.UserProgress> progress = trackerState.Progress;
        if (progress.Count == 0)
            return null;
        IEnumerable<FetchModels.TarkovTracker.UserProgress> teamProgress = progress.Where(x =>
            x.UserId != trackerState.Self
        );

        List<KeyValuePair<string, KeyValuePair<int, int>>> needs = [];
        foreach (FetchModels.TarkovTracker.UserProgress memberProgress in teamProgress)
        {
            int task = item.GetTaskRemaining(memberProgress).count;
            int hideout = item.GetHideoutRemaining(memberProgress);

            if (task == 0 && hideout == 0)
                continue;

            KeyValuePair<int, int> need = new(task, hideout);

            string baseName = memberProgress.DisplayName ?? "Unknown";
            string name = baseName;
            for (int i = 2; i < 99; i++)
            {
                if (needs.All(n => n.Key != name))
                    break;
                name = $"{baseName} #{i}";
            }
            needs.Add(new KeyValuePair<string, KeyValuePair<int, int>>(name, need));
        }
        return needs;
    }

    protected virtual void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(FleaMarketLabel));
        OnPropertyChanged(nameof(PricePerSlotLabel));
        OnPropertyChanged(nameof(KappaNeededLabel));
        OnPropertyChanged(nameof(NeededItemsLabel));
        OnPropertyChanged(nameof(UpdatedTimestampLabel));
    }

    public void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged();
    }

    internal static string FormatLongPrice(int? value)
    {
        if (value is null or 0)
            return "0 ₽";
        string text = $"{value:n0}";
        string numberGroupSeparator = NumberFormatInfo.CurrentInfo.NumberGroupSeparator;
        return text.Replace(numberGroupSeparator, RatConfig.ToolTip.DigitGroupingSymbol) + " ₽";
    }
}
