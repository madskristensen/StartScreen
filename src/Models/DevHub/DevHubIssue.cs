using System;
using System.Collections.Generic;
using System.Linq;

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
        /// Returns a comma-separated label text for display.
        /// </summary>
        public string LabelText => Labels.Count > 0 ? string.Join(", ", Labels.Select(l => l.Name)) : string.Empty;
    }
}
