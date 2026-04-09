using System.Text.Json.Serialization;

namespace StartScreen.Models
{
    /// <summary>
    /// Represents metadata about a news feed (RSS/Atom).
    /// </summary>
    public class FeedInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }
}
