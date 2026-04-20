using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.ServiceModel.Syndication;

namespace StartScreen.Models
{
    /// <summary>
    /// Represents a YouTube video from the channel feed.
    /// </summary>
    public class YouTubeVideo : INotifyPropertyChanged
    {
        private string _title;
        private string _url;
        private string _thumbnailUrl;
        private DateTime _publishDate;

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

        public string ThumbnailUrl
        {
            get => _thumbnailUrl;
            set
            {
                if (_thumbnailUrl != value)
                {
                    _thumbnailUrl = value;
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
            video.ThumbnailUrl = BuildThumbnailUrl(video.Url);

            return video;
        }

        /// <summary>
        /// Builds a YouTube thumbnail URL from the video watch URL.
        /// </summary>
        internal static string BuildThumbnailUrl(string videoUrl)
        {
            if (string.IsNullOrEmpty(videoUrl))
                return string.Empty;

            try
            {
                var uri = new Uri(videoUrl);
                var query = uri.Query;

                if (query.StartsWith("?"))
                    query = query.Substring(1);

                foreach (var param in query.Split('&'))
                {
                    var parts = param.Split(new[] { '=' }, 2);
                    if (parts.Length == 2 && parts[0] == "v")
                    {
                        return $"https://i.ytimg.com/vi/{parts[1]}/mqdefault.jpg";
                    }
                }
            }
            catch
            {
                // Malformed URL; fall through to return empty
            }

            return string.Empty;
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

            return new YouTubeVideo
            {
                Title = entry.Title ?? string.Empty,
                Url = entry.Url ?? string.Empty,
                ThumbnailUrl = entry.ThumbnailUrl ?? string.Empty,
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
