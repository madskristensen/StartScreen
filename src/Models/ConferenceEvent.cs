using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace StartScreen.Models
{
    public sealed class ConferenceEvent : INotifyPropertyChanged
    {
        private BitmapImage _thumbnail;

        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public string ThumbnailUrl { get; set; }
        public string Location { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string SubscriberDiscountUrl { get; set; }
        public string[] Sources { get; set; }

        [JsonIgnore]
        public bool HasSubscriberDiscount => !string.IsNullOrEmpty(SubscriberDiscountUrl);

        [JsonIgnore]
        public string DateRange
        {
            get
            {
                if (!StartDate.HasValue)
                    return string.Empty;
                var start = StartDate.Value;
                var end = EndDate ?? start;
                return start == end ? $"{start:MMM d, yyyy}"
                    : start.Year == end.Year ? $"{start:MMM d} - {end:MMM d, yyyy}"
                    : $"{start:MMM d, yyyy} - {end:MMM d, yyyy}";
            }
        }

        [JsonIgnore]
        public BitmapImage Thumbnail
        {
            get => _thumbnail;
            internal set
            {
                if (ReferenceEquals(_thumbnail, value))
                    return;
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class EventCatalog
    {
        public int Version { get; set; }
        public List<ConferenceEvent> Events { get; set; }
    }

    internal sealed class EventFeedResult
    {
        internal IReadOnlyList<ConferenceEvent> Events { get; set; } = Array.Empty<ConferenceEvent>();
        internal bool IsCached { get; set; }
        internal bool IsStale { get; set; }
        internal bool IsUnavailable { get; set; }
        internal string Message { get; set; }
        internal DateTimeOffset? LastSuccessfulRefreshUtc { get; set; }
    }
}
