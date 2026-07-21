using System.Collections.Generic;
using Newtonsoft.Json;

namespace RatScanner.FetchModels.TarkovTracker;

public class TeamProgressResponse
{
    [JsonProperty("data")]
    public List<UserProgress>? TeamProgress { get; set; }

    [JsonProperty("meta")]
    public Metadata? Meta { get; set; }

    public class Metadata
    {
        [JsonProperty("self")]
        public string? Self { get; set; }

        [JsonProperty("hiddenTeammates")]
        public List<string>? HiddenTeammates { get; set; }
    }
}
