using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.ServiceModel.Syndication;
using System.Windows.Media.Imaging;

namespace StartScreen.Models
{
    /// <summary>
    /// Represents a YouTube video from the channel feed.
    /// </summary>
    public class YouTubeVideo : INotifyPropertyChanged
    {
        private string _title;
        private string _url;
        private DateTime _publishDate;
        private BitmapSource _thumbnail;

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Url
        {
            get => _url;
            set
            {
                if (_url != value)
                {
                    _url = value;
                    OnPropertyChanged();
                }
            }
        }

        internal string ThumbnailUrl { get; set; }

        public BitmapSource Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail != value)
                {
                    _thumbnail = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime PublishDate
        {
            get => _publishDate;
            set
            {
                if (_publishDate != value)
                {
                    _publishDate = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether the video is new (published within the last 3 days).
        /// </summary>
        public bool IsNew => (DateTime.Now - PublishDate).TotalDays < 3;

        /// <summary>
        /// Creates a YouTubeVideo from a SyndicationItem parsed from the YouTube Atom feed.
        /// </summary>
        public static YouTubeVideo FromSyndicationItem(SyndicationItem item)
        {
            if (item == null)
                return null;

            var video = new YouTubeVideo();
            video.Title = WebUtility.HtmlDecode(item.Title?.Text ?? string.Empty).Trim();
            video.Url = item.Links?.FirstOrDefault()?.Uri?.OriginalString ?? string.Empty;
            video.PublishDate = item.PublishDate.DateTime;

            // item.Id in YouTube feeds is "yt:video:{videoId}" - most reliable source
            var videoId = ExtractVideoId(item.Id) ?? ExtractVideoIdFromUrl(video.Url);
            video.ThumbnailUrl = string.IsNullOrEmpty(videoId)
                ? string.Empty
                : $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

            return video;
        }

        /// <summary>
        /// Extracts the video ID from a YouTube Atom feed item ID ("yt:video:{videoId}").
        /// </summary>
        private static string ExtractVideoId(string itemId)
        {
            const string prefix = "yt:video:";
            if (!string.IsNullOrEmpty(itemId) && itemId.StartsWith(prefix))
                return itemId.Substring(prefix.Length);
            return null;
        }

        /// <summary>
        /// Extracts the video ID from a YouTube watch or Shorts URL as a fallback.
        /// Handles https://www.youtube.com/watch?v={id} and https://www.youtube.com/shorts/{id}.
        /// </summary>
        internal static string ExtractVideoIdFromUrl(string videoUrl)
        {
            if (string.IsNullOrEmpty(videoUrl))
                return null;

            try
            {
                var uri = new Uri(videoUrl);

                // Shorts: /shorts/{videoId}
                var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var shortsIndex = Array.IndexOf(segments, "shorts");
                if (shortsIndex >= 0 && shortsIndex + 1 < segments.Length)
                    return segments[shortsIndex + 1];

                // Standard watch URL: ?v={videoId}
                var query = uri.Query.TrimStart('?');
                foreach (var param in query.Split('&'))
                {
                    var parts = param.Split(new[] { '=' }, 2);
                    if (parts.Length == 2 && parts[0] == "v")
                        return parts[1];
                }
            }
            catch
            {
                // Malformed URL
            }

            return null;
        }

        /// <summary>
        /// Converts this video to a lightweight cache entry for JSON serialization.
        /// </summary>
        internal YouTubeCacheEntry ToCacheEntry()
        {
            return new YouTubeCacheEntry
            {
                Title = Title,
                Url = Url,
                ThumbnailUrl = ThumbnailUrl,
                PublishDate = PublishDate,
            };
        }

        /// <summary>
        /// Creates a YouTubeVideo from a cached JSON entry.
        /// </summary>
        internal static YouTubeVideo FromCacheEntry(YouTubeCacheEntry entry)
        {
            if (entry == null)
                return null;

            var url = entry.Url ?? string.Empty;
            var videoId = ExtractVideoIdFromUrl(entry.ThumbnailUrl) ?? ExtractVideoIdFromUrl(url);

            return new YouTubeVideo
            {
                Title = entry.Title ?? string.Empty,
                Url = url,
                ThumbnailUrl = string.IsNullOrEmpty(videoId) ? string.Empty : $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg",
                PublishDate = entry.PublishDate,
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
