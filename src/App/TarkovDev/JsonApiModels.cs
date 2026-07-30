using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RatScanner.TarkovDev;

/// <summary>Raw JSON envelopes from json.tarkov.dev (pre-projection).</summary>
internal static class JsonApiModels
{
    internal sealed class Envelope<T>
    {
        [JsonProperty("data")]
        public T? Data { get; set; }
    }

    internal sealed class ItemsPayload
    {
        [JsonProperty("items")]
        public Dictionary<string, RawItem>? Items { get; set; }
    }

    internal sealed class TasksPayload
    {
        [JsonProperty("tasks")]
        public Dictionary<string, RawTask>? Tasks { get; set; }
    }

    internal sealed class MapsPayload
    {
        [JsonProperty("maps")]
        public Dictionary<string, RawMap>? Maps { get; set; }
    }

    // Hideout and traders are id→object maps at data root (no nested key).

    internal abstract class Identifiable
    {
        [JsonProperty("id")]
        public string? Id { get; set; }
    }

    internal abstract class Entity : Identifiable
    {
        [JsonProperty("name")]
        public string? Name { get; set; }
    }

    internal abstract class NamedEntity : Entity
    {
        [JsonProperty("normalizedName")]
        public string? NormalizedName { get; set; }
    }

    internal sealed class RawItem : Entity
    {
        [JsonProperty("shortName")]
        public string? ShortName { get; set; }

        [JsonProperty("updated")]
        public string? Updated { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonProperty("wikiLink")]
        public string? WikiLink { get; set; }

        [JsonProperty("link")]
        public string? Link { get; set; }

        [JsonProperty("iconLink")]
        public string? IconLink { get; set; }

        [JsonProperty("baseImageLink")]
        public string? BaseImageLink { get; set; }

        [JsonProperty("avg24hPrice")]
        public int? Avg24HPrice { get; set; }

        [JsonProperty("backgroundColor")]
        public string? BackgroundColor { get; set; }

        [JsonProperty("types")]
        public List<string>? Types { get; set; }

        [JsonProperty("properties")]
        public RawItemProperties? Properties { get; set; }

        [JsonProperty("sellToTrader")]
        public List<RawTraderPrice>? SellToTrader { get; set; }
    }

    internal sealed class RawItemProperties
    {
        [JsonProperty("propertiesType")]
        public string? PropertiesType { get; set; }

        [JsonProperty("caliber")]
        public string? Caliber { get; set; }

        [JsonProperty("damage")]
        public int? Damage { get; set; }

        [JsonProperty("penetrationPower")]
        public int? PenetrationPower { get; set; }

        [JsonProperty("fragmentationChance")]
        public float? FragmentationChance { get; set; }
    }

    internal sealed class RawTraderPrice
    {
        [JsonProperty("trader")]
        public string? Trader { get; set; }

        [JsonProperty("priceRUB")]
        public int? PriceRub { get; set; }

        [JsonProperty("price")]
        public int? Price { get; set; }
    }

    internal sealed class RawTrader : NamedEntity
    {
        [JsonProperty("imageLink")]
        public string? ImageLink { get; set; }
    }

    internal sealed class RawTask : Entity
    {
        [JsonProperty("wikiLink")]
        public string? WikiLink { get; set; }

        [JsonProperty("taskImageLink")]
        public string? TaskImageLink { get; set; }

        [JsonProperty("kappaRequired")]
        public bool? KappaRequired { get; set; }

        [JsonProperty("trader")]
        public string? TraderId { get; set; }

        [JsonProperty("objectives")]
        public List<JObject>? Objectives { get; set; }
    }

    internal sealed class RawHideoutStation : Entity
    {
        [JsonProperty("levels")]
        public List<RawHideoutLevel>? Levels { get; set; }
    }

    internal sealed class RawHideoutLevel : Identifiable
    {
        [JsonProperty("itemRequirements")]
        public List<RawHideoutItemReq>? ItemRequirements { get; set; }
    }

    internal sealed class RawHideoutItemReq : Identifiable
    {
        [JsonProperty("item")]
        public string? Item { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("attributes")]
        public RawHideoutItemAttributes? Attributes { get; set; }
    }

    internal sealed class RawHideoutItemAttributes
    {
        [JsonProperty("foundInRaid")]
        public bool FoundInRaid { get; set; }
    }

    internal sealed class RawMap : NamedEntity { }
}
