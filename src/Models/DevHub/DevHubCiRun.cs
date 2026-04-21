using System;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Represents a CI/build run from any supported hosting provider.
    /// </summary>
    public sealed class DevHubCiRun
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
        /// The workflow or pipeline name (e.g., "build.yaml", "CI Pipeline").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The branch this run was triggered on.
        /// </summary>
        public string Branch { get; set; }

        /// <summary>
        /// The result status (e.g., "success", "failure", "pending", "cancelled").
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// When this run completed (or started, if still running).
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Direct URL to view this run in a browser.
        /// </summary>
        public string WebUrl { get; set; }

        /// <summary>
        /// Returns a status display icon.
        /// </summary>
        public string StatusIcon
        {
            get
            {
                return Status switch
                {
                    "success" => "\u2713",
                    "failure" => "\u2717",
                    "pending" => "\u25CF",
                    "cancelled" => "\u25CB",
                    "skipped" => "\u2192",
                    _ => "\u25CF",
                };
            }
        }

        /// <summary>
        /// Returns a human-readable relative time since the run.
        /// </summary>
        public string TimestampAgoText
        {
            get
            {
                TimeSpan span = DateTime.UtcNow - Timestamp;

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

                return Timestamp.ToString("MMM d");
            }
        }
    }
}
