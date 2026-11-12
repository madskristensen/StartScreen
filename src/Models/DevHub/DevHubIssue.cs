using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Represents an issue or work item from any supported hosting provider.
    /// </summary>
    public sealed class DevHubIssue
    {
        /// <summary>
        /// The hosting provider name (e.g., "GitHub", "Azure DevOps").
        /// </summary>
        public string ProviderName { get; set; }

        /// <summary>
        /// Repository display name (e.g., "madskristensen/StartScreen").
        /// </summary>
        public string RepoDisplayName { get; set; }

        /// <summary>
        /// Alias for RepoDisplayName for XAML binding convenience.
        /// </summary>
        public string RepoFullName => RepoDisplayName ?? string.Empty;

        /// <summary>
        /// The parsed remote repository identifier.
        /// </summary>
        public RemoteRepoIdentifier RepoIdentifier { get; set; }

        /// <summary>
        /// Issue title.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Issue number as displayed (e.g., "#201").
        /// </summary>
        public string Number { get; set; }

        /// <summary>
        /// The numeric issue ID (without prefix).
        /// </summary>
        public int NumericId { get; set; }

        /// <summary>
        /// The issue author username.
        /// </summary>
        public string Author { get; set; }

        /// <summary>
        /// Labels applied to this issue.
        /// </summary>
        public List<DevHubLabel> Labels { get; set; } = new List<DevHubLabel>();

        /// <summary>
        /// Issue state (e.g., "open", "active", "new").
        /// </summary>
        public string State { get; set; }

        public bool IsAuthoredByCurrentUser { get; set; }

        public bool IsAssignedToCurrentUser { get; set; }

        public bool IsClosed { get; set; }

        public string WorkItemType { get; set; }
        public string AssignedTo { get; set; }
        public string Iteration { get; set; }
        public bool IsBlocked { get; set; }
        public bool IsMentioned { get; set; }

        [JsonIgnore]
        public string AttentionReason => !IsClosed && !string.IsNullOrWhiteSpace(State)
            && !string.Equals(State, "closed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(State, "done", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(State, "removed", StringComparison.OrdinalIgnoreCase)
            && DevHubAttentionPolicy.HasRecentActivity(UpdatedAt, CreatedAt, DateTime.UtcNow)
            && IsAssignedToCurrentUser ? (IsBlocked ? "Blocked" : "Assigned to you") : null;

        [JsonIgnore]
        public bool NeedsAttention => AttentionReason != null;

        [JsonIgnore]
        public string StatusText => IsBlocked && !IsClosed ? "Blocked" : State;

        [JsonIgnore]
        public string MetadataText => string.Join(" / ", new[] { WorkItemType, Priority }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        [JsonIgnore]
        public string StatusDetails => string.Join(Environment.NewLine, new[]
        {
            string.IsNullOrEmpty(Author) ? null : $"Created by: {Author}",
            string.IsNullOrEmpty(AssignedTo) ? null : $"Assigned to: {AssignedTo}",
            State,
            MetadataText,
            string.IsNullOrEmpty(Iteration) ? null : $"Iteration: {Iteration}",
            IsBlocked ? "Blocked" : null,
            IsMentioned ? "You were mentioned (not an unread indicator)" : null,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        /// <summary>
        /// Priority level if available (e.g., "P1", "Critical"), or null.
        /// </summary>
        public string Priority { get; set; }

        /// <summary>
        /// When the issue was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// When the issue was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Direct URL to open this issue in a browser.
        /// </summary>
        public string WebUrl { get; set; }

        /// <summary>
        /// Whether this item has been updated since the user last viewed the Issues tab.
        /// Runtime-only flag set by the UI; not persisted to the cache.
        /// </summary>
        [JsonIgnore]
        public bool IsNew { get; set; }

        /// <summary>
        /// Returns a human-readable relative time since creation.
        /// </summary>
        public string CreatedAgoText
        {
            get
            {
                TimeSpan span = DateTime.UtcNow - CreatedAt;

                if (span.TotalMinutes < 1)
                    return "just now";
                if (span.TotalMinutes < 60)
                    return $"{(int)span.TotalMinutes}m ago";
                if (span.TotalHours < 24)
                    return $"{(int)span.TotalHours}h ago";
                if (span.TotalDays < 7)
                    return $"{(int)span.TotalDays}d ago";
                if (span.TotalDays < 30)
                    return $"{(int)(span.TotalDays / 7)}w ago";

                return CreatedAt.ToString("MMM d");
            }
        }

        /// <summary>
        /// Returns a human-readable relative time since last update.
        /// </summary>
        public string UpdatedAgoText
        {
            get
            {
                TimeSpan span = DateTime.UtcNow - UpdatedAt;

                if (span.TotalMinutes < 1)
                    return "just now";
                if (span.TotalMinutes < 60)
                    return $"{(int)span.TotalMinutes}m ago";
                if (span.TotalHours < 24)
                    return $"{(int)span.TotalHours}h ago";
                if (span.TotalDays < 7)
                    return $"{(int)span.TotalDays}d ago";
                if (span.TotalDays < 30)
                    return $"{(int)(span.TotalDays / 7)}w ago";

                return UpdatedAt.ToString("MMM d");
            }
        }

        /// <summary>
        /// Returns a comma-separated label text for display.
        /// </summary>
        public string LabelText => Labels.Count > 0 ? string.Join(", ", Labels.Select(l => l.Name)) : string.Empty;

        internal DevHubIssue Clone()
        {
            var copy = (DevHubIssue)MemberwiseClone();
            copy.Labels = Labels?.ToList();
            return copy;
        }
    }
}
