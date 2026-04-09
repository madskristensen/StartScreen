using System;

namespace StartScreen.Models
{
    /// <summary>
    /// Represents metadata about a news feed (RSS/Atom).
    /// </summary>
    public class FeedInfo
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public bool IsSelected { get; set; }

        /// <summary>
        /// True if this feed is part of the default set, false if added by user.
        /// </summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>
        /// Gets the display name without any prefix characters (!, ?).
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(Name))
                    return string.Empty;

                return Name.TrimStart('!', '?');
            }
        }

        /// <summary>
        /// Returns a string representation for serialization.
        /// Format: "Name:IsSelected"
        /// </summary>
        public override string ToString()
        {
            return $"{Name}:{IsSelected}";
        }

        /// <summary>
        /// Creates a FeedInfo from a serialized string.
        /// </summary>
        public static FeedInfo Parse(string serialized)
        {
            if (string.IsNullOrEmpty(serialized))
                return null;

            var parts = serialized.Split(new[] { ':' }, 2);
            if (parts.Length < 2)
                return null;

            return new FeedInfo
            {
                Name = parts[0],
                IsSelected = bool.TryParse(parts[1], out bool selected) && selected,
                IsBuiltIn = false
            };
        }
    }
}
