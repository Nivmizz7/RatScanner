using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using RatScanner.Scan;
using RatScanner.TarkovDev;

namespace RatScanner.ViewModel;

internal class MenuVM : INotifyPropertyChanged
{
    private RatScannerMain _dataSource = null!;

    public RatScannerMain DataSource
    {
        get => _dataSource;
        set
        {
            _dataSource = value;
            OnPropertyChanged();
        }
    }

    public ItemQueue ItemScans => DataSource.ItemScans;

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

    /// <summary>Numeric product version (TarkovTracker Edition line from csproj).</summary>
    public string Version => RatConfig.Version;

    /// <summary>Sidebar/about display, e.g. <c>v4.0.0 · TT</c>.</summary>
    public string VersionDisplay => $"{RatConfig.VersionDisplay} · {Constants.Branding.EditionToken}";

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

    public string WikiLink => LastItem.GetWikiLink();

    public int PricePerSlot => LastItem.GetAvg24hMarketPricePerSlot();

    public ItemSellPrice? BestTraderOffer => LastItem.GetBestTraderOffer();
    public TraderOffer? BestTraderOfferVendor => LastItem.GetBestTraderOfferVendor();

    public (int count, int kappaCount) TaskRemainingResult => LastItem.GetTaskRemaining();

    public int TaskRemaining => TaskRemainingResult.count;

    public int TaskRemainingKappa => TaskRemainingResult.kappaCount;

    public bool KappaNeeded => TaskRemainingKappa > 0;

    public int HideoutRemaining => LastItem.GetHideoutRemaining();

    public bool ItemNeeded => TaskRemaining + HideoutRemaining > 0;

    public static bool ShowKappaNeeds => RatConfig.Tracking.ShowKappaNeeds;

    public List<KeyValuePair<string, KeyValuePair<int, int>>>? ItemTeamNeeds
    {
        get
        {
            List<FetchModels.TarkovTracker.UserProgress> progress = RatScannerMain.Instance.TarkovTrackerDB.Progress;
            if (progress.Count == 0)
                return null;
            IEnumerable<FetchModels.TarkovTracker.UserProgress> teamProgress = progress.Where(x =>
                x.UserId != RatScannerMain.Instance.TarkovTrackerDB.Self
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

    public MenuVM(RatScannerMain ratScanner)
    {
        DataSource = ratScanner;
        DataSource.PropertyChanged += ModelPropertyChanged;
    }

    protected virtual void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
