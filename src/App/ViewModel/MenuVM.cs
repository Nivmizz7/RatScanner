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

    public ItemQueue ItemScans => _scanOrchestrator.ItemScans;

    internal FetchModels.TarkovTracker.UserProgress CurrentUserProgress => _trackerService.State.CurrentUser;

    public ItemScan LastItemScan =>
        ItemScans.LastOrDefault()
        ?? new DefaultItemScan(
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

    public Item LastItem => LastItemScan.Item;

    /// <summary>Numeric product version from csproj.</summary>
    public string Version => RatConfig.Version;

    /// <summary>Sidebar/about display, e.g. <c>v4.0.0-beta.1</c>.</summary>
    public string VersionDisplay => RatConfig.VersionDisplay;

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

    public string FleaMarketLabel => PresentationText.T("FleaMarket", "Flea Market");

    public string PricePerSlotLabel => PresentationText.T("PricePerSlot", "Price per Slot");

    public string KappaNeededLabel => PresentationText.T("KappaNeeded", "Kappa Needed");

    public string NeededItemsLabel => PresentationText.T("NeededQuestHideout", "Needed Quest & Hideout");

    public string UpdatedTimestampLabel => PresentationText.T("UpdatedTimestamp", "Updated Timestamp");

    public string WikiLink => LastItem.GetWikiLink();

    public int PricePerSlot => LastItem.GetAvg24hMarketPricePerSlot();

    public ItemSellPrice? BestTraderOffer => LastItem.GetBestTraderOffer();
    public TraderOffer? BestTraderOfferVendor => LastItem.GetBestTraderOfferVendor();

    public (int count, int kappaCount) TaskRemainingResult => LastItem.GetTaskRemaining(CurrentUserProgress);

    public int TaskRemaining => TaskRemainingResult.count;

    public int TaskRemainingKappa => TaskRemainingResult.kappaCount;

    public bool KappaNeeded => TaskRemainingKappa > 0;

    public int HideoutRemaining => LastItem.GetHideoutRemaining(CurrentUserProgress);

    public bool ItemNeeded => TaskRemaining + HideoutRemaining > 0;

    public static bool ShowKappaNeeds => RatConfig.Tracking.ShowKappaNeeds;

    public List<KeyValuePair<string, KeyValuePair<int, int>>>? ItemTeamNeeds
    {
        get
        {
            TrackerStateSnapshot trackerState = _trackerService.State;
            IReadOnlyList<FetchModels.TarkovTracker.UserProgress> progress = trackerState.Progress;
            if (progress.Count == 0)
                return null;
            IEnumerable<FetchModels.TarkovTracker.UserProgress> teamProgress = progress.Where(x =>
                x.UserId != trackerState.Self
            );

            List<KeyValuePair<string, KeyValuePair<int, int>>> needs = [];
            foreach (FetchModels.TarkovTracker.UserProgress? memberProgress in teamProgress)
            {
                int task = LastItem.GetTaskRemaining(memberProgress).count;
                int hideout = LastItem.GetHideoutRemaining(memberProgress);

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
    }
    public (int task, int hideout) ItemTeamNeedsSummed
    {
        get
        {
            List<KeyValuePair<string, KeyValuePair<int, int>>>? needs = ItemTeamNeeds;
            return (needs?.Sum(i => i.Value.Key) ?? 0, needs?.Sum(i => i.Value.Value) ?? 0);
        }
    }
    public bool ItemTeamNeeded
    {
        get
        {
            List<KeyValuePair<string, KeyValuePair<int, int>>>? needs = ItemTeamNeeds;
            return needs != null && needs.Count != 0;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal MenuVM(IScanOrchestrator scanOrchestrator, ITrackerService trackerService)
    {
        _scanOrchestrator = scanOrchestrator ?? throw new ArgumentNullException(nameof(scanOrchestrator));
        _trackerService = trackerService ?? throw new ArgumentNullException(nameof(trackerService));
        _scanOrchestrator.PropertyChanged += ModelPropertyChanged;
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
