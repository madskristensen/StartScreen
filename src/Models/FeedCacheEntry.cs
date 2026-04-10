using System;

namespace StartScreen.Models
{
    /// <summary>
    /// Lightweight DTO for JSON serialization of cached feed items.
    /// Stores only the fields needed to reconstruct a <see cref="NewsPost"/>.
    /// </summary>
    internal class FeedCacheEntry
    {
        public string Title { get; set; }
        public string Summary { get; set; }
        public string Url { get; set; }
        public DateTime PublishDate { get; set; }
        public string Source { get; set; }
        public bool HasDescription { get; set; }
    }
}
