using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;

namespace StartScreen.Models
{
    /// <summary>
    /// Represents a news post from an RSS/Atom feed.
    /// </summary>
    public class NewsPost : INotifyPropertyChanged
    {
        private static readonly Regex HtmlRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        private const string TrackingParameter = "?cid=vs_start_screen";
        private const int MaxSummaryLength = 1000;
        private const string NoDescriptionText = "(No description)";

        private string _title;
        private string _summary;
        private string _url;
        private DateTime _publishDate;
        private string _source;
        private string _toolTip;
        private bool _hasDescription;
        private bool _isPinned;

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

        public string Summary
        {
            get => _summary;
            set
            {
                if (_summary != value)
                {
                    _summary = value;
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

        public string Source
        {
            get => _source;
            set
            {
                if (_source != value)
                {
                    _source = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ToolTip
        {
            get => _toolTip;
            set
            {
                if (_toolTip != value)
                {
                    _toolTip = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasDescription
        {
            get => _hasDescription;
            set
            {
                if (_hasDescription != value)
                {
                    _hasDescription = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned != value)
                {
                    _isPinned = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether the post is new (published within the last 3 days).
        /// </summary>
        public bool IsNew => (DateTime.Now - PublishDate).TotalDays < 3;

        /// <summary>
        /// Creates a NewsPost from a SyndicationItem.
        /// </summary>
        public static NewsPost FromSyndicationItem(SyndicationItem item)
        {
            if (item == null)
                return null;

            var post = new NewsPost();

            // Title
            post.Title = WebUtility.HtmlDecode(item.Title?.Text ?? string.Empty).Trim();

            // URL with tracking parameter
            post.Url = item.Links?.FirstOrDefault()?.Uri?.OriginalString ?? string.Empty;
            if (!string.IsNullOrEmpty(post.Url) && !post.Url.Contains('?'))
            {
                post.Url += TrackingParameter;
            }

            // Summary (strip HTML and truncate)
            var summary = item.Summary?.Text ?? NoDescriptionText;
            post.Summary = WebUtility.HtmlDecode(TruncateHtml(summary.Trim()));
            post.HasDescription = summary != NoDescriptionText;

            // Publish date and source
            post.PublishDate = item.PublishDate.DateTime;
            post.Source = $"{item.PublishDate:MMM d} in {item.SourceFeed?.Title?.Text}";

            // Tooltip
            post.ToolTip = $"{post.Title}\r\n{item.PublishDate:MMMM d, yyyy}";

            return post;
        }

        private static string TruncateHtml(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            // Remove HTML tags
            var clearText = HtmlRegex.Replace(input, string.Empty)
                .Replace("\r", " ")
                .Replace("\n", string.Empty);

            // Truncate to max length
            var maxLength = Math.Min(MaxSummaryLength, clearText.Length);
            return clearText.Substring(0, maxLength);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
