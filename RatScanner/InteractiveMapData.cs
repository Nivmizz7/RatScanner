using System.Collections.Generic;
using Newtonsoft.Json;

namespace RatScanner
{
    public class InteractiveMapData
    {
        [JsonProperty("normalizedName")]
        public string NormalizedName { get; set; } = null!;

        [JsonProperty("primaryPath")]
        public string PrimaryPath { get; set; } = null!;

        [JsonProperty("maps")]
        public List<Map> Maps { get; set; } = null!;

        [JsonProperty("name")]
        public string Name { get; set; } = null!;

        [JsonProperty("description")]
        public string Description { get; set; } = null!;

        public class Extent
        {
            [JsonProperty("height")]
            public List<double?> Height { get; set; } = null!;

            [JsonProperty("bounds")]
            public List<List<object>> Bounds { get; set; } = null!;
        }

        public class Label
        {
            [JsonProperty("position")]
            public List<double?> Position { get; set; } = null!;

            [JsonProperty("text")]
            public string Text { get; set; } = null!;

            [JsonProperty("rotation")]
            public object Rotation { get; set; } = null!;

            [JsonProperty("size")]
            public int? Size { get; set; }

            [JsonProperty("top")]
            public int? Top { get; set; }

            [JsonProperty("bottom")]
            public double? Bottom { get; set; }
        }

        public class Layer
        {
            [JsonProperty("name")]
            public string Name { get; set; } = null!;

            [JsonProperty("svgLayer")]
            public string SvgLayer { get; set; } = null!;

            [JsonProperty("show")]
            public bool? Show { get; set; }

            [JsonProperty("extents")]
            public List<Extent> Extents { get; set; } = null!;

            [JsonProperty("tilePath")]
            public string TilePath { get; set; } = null!;
        }

        public class Map
        {
            [JsonProperty("key")]
            public string Key { get; set; } = null!;

            [JsonProperty("projection")]
            public string Projection { get; set; } = null!;

            [JsonProperty("minZoom")]
            public int? MinZoom { get; set; }

            [JsonProperty("maxZoom")]
            public int? MaxZoom { get; set; }

            [JsonProperty("transform")]
            public List<double?> Transform { get; set; } = null!;

            [JsonProperty("coordinateRotation")]
            public int? CoordinateRotation { get; set; }

            [JsonProperty("bounds")]
            public List<List<double?>> Bounds { get; set; } = null!;

            [JsonProperty("heightRange")]
            public List<double?> HeightRange { get; set; } = null!;

            [JsonProperty("author")]
            public string Author { get; set; } = null!;

            [JsonProperty("authorLink")]
            public string AuthorLink { get; set; } = null!;

            [JsonProperty("svgPath")]
            public string SvgPath { get; set; } = null!;

            [JsonProperty("svgLayer")]
            public string SvgLayer { get; set; } = null!;

            [JsonProperty("layers")]
            public List<Layer> Layers { get; set; } = null!;

            [JsonProperty("labels")]
            public List<Label> Labels { get; set; } = null!;

            [JsonProperty("specific")]
            public string Specific { get; set; } = null!;

            [JsonProperty("altMaps")]
            public List<string> AltMaps { get; set; } = null!;

            [JsonProperty("tileSize")]
            public int? TileSize { get; set; }

            [JsonProperty("tilePath")]
            public string TilePath { get; set; } = null!;

            [JsonProperty("_heightRange")]
            public List<int?> AlternateHeightRange { get; set; } = null!;

            [JsonProperty("orientation")]
            public string Orientation { get; set; } = null!;

            [JsonProperty("svgBounds")]
            public List<List<int?>> SvgBounds { get; set; } = null!;
        }
    }
}
