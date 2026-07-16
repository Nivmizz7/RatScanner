using Newtonsoft.Json;

namespace RatScanner.FetchModels.TarkovTracker;

public class ProgressResponse
{
    [JsonProperty("data")]
    public UserProgress? UserProgress { get; set; }

    [JsonProperty("meta")]
    public Metadata? Meta { get; set; }

    public class Metadata
    {
        [JsonProperty("self")]
        public string? Self { get; set; }

        [JsonProperty("gameMode")]
        public string? GameMode { get; set; }
    }
}
