using System;

namespace StartScreen.Models
{
    /// <summary>
    /// Lightweight DTO for JSON serialization of cached YouTube video items.
    /// Stores only the fields needed to reconstruct a <see cref="YouTubeVideo"/>.
    /// </summary>
    internal class YouTubeCacheEntry
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string ThumbnailUrl { get; set; }
        public DateTime PublishDate { get; set; }
    }
}
