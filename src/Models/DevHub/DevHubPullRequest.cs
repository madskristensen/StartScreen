using System;
using System.Text.Json.Serialization;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Represents a pull request from any supported hosting provider.
    /// </summary>
    public sealed class DevHubPullRequest
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
        /// PR title.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// PR number as displayed (e.g., "#142" for GitHub, "!731" for ADO).
        /// </summary>
        public string Number { get; set; }

        /// <summary>
        /// The numeric PR ID (without prefix).
        /// </summary>
        public int NumericId { get; set; }

        /// <summary>
        /// The PR author username.
        /// </summary>
        public string Author { get; set; }

        /// <summary>
        /// The target/base branch name.
        /// </summary>
        public string TargetBranch { get; set; }

        /// <summary>
        /// The source branch name.
        /// </summary>
        public string SourceBranch { get; set; }

        /// <summary>
        /// PR status (e.g., "open", "draft", "conflicts").
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// CI/check status for this PR (e.g., "success", "failure", "pending", null).
        /// </summary>
        public string CiStatus { get; set; }

        /// <summary>
        /// Number of approving reviews.
        /// </summary>
        public int ApprovalCount { get; set; }

        /// <summary>
        /// When the PR was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// When the PR was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Direct URL to open this PR in a browser.
        /// </summary>
        public string WebUrl { get; set; }

        /// <summary>
        /// Whether this item has been updated since the user last viewed the PRs tab.
        /// Runtime-only flag set by the UI; not persisted to the cache.
        /// </summary>
        [JsonIgnore]
        public bool IsNew { get; set; }

        /// <summary>
        /// Whether this PR was authored by the current user (vs. assigned/reviewer).
        /// </summary>
        public bool IsAuthoredByCurrentUser { get; set; }

        public bool IsReviewRequestedForCurrentUser { get; set; }

        public bool HasChangesRequested { get; set; }

        public bool IsApproved { get; set; }

        [JsonIgnore]
        public bool IsOpen => string.Equals(Status, "open", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "conflicts", StringComparison.OrdinalIgnoreCase) || IsDraft;

        [JsonIgnore]
        public string AttentionReason => !IsOpen || IsDraft ? null
            : !IsAuthoredByCurrentUser ? (IsReviewRequestedForCurrentUser ? "Review requested" : null)
            : string.Equals(Status, "conflicts", StringComparison.OrdinalIgnoreCase) ? "Conflicts"
            : HasChangesRequested ? "Changes requested"
            : CiStatus == "failure" ? "Checks failing" : null;

        [JsonIgnore]
        public bool NeedsAttention => AttentionReason != null;

        /// <summary>
        /// Whether this PR is a draft.
        /// </summary>
        public bool IsDraft => string.Equals(Status, "draft", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public string StatusText => Status == "conflicts" ? "Conflicts"
            : HasChangesRequested && IsOpen && !IsDraft ? "Changes requested"
            : CiStatus == "failure" ? "Checks failing"
            : IsDraft ? "Draft"
            : IsReviewRequestedForCurrentUser ? "Review requested"
            : CiStatus == "pending" ? "Checks pending"
            : ApprovalCount == 1 ? "1 approval"
            : ApprovalCount > 1 ? $"{ApprovalCount} approvals"
            : IsApproved && IsOpen && !IsDraft ? "Approved" : "Waiting for review";

        [JsonIgnore]
        public string ActivityDateText => FormatActivityDate(UpdatedAt, DateTime.Now);

        [JsonIgnore]
        public string ActivityDateToolTip => UpdatedAt == default ? string.Empty : UpdatedAt.ToLocalTime().ToString("f");

        internal static string FormatActivityDate(DateTime updatedAt, DateTime now)
        {
            if (updatedAt == default)
                return string.Empty;

            var localDate = updatedAt.ToLocalTime();
            var localNow = now.ToLocalTime();
            var daysAgo = (localNow.Date - localDate.Date).Days;
            if (daysAgo == 0)
                return "Updated today";
            if (daysAgo == 1)
                return "Updated yesterday";

            return $"Updated {localDate.ToString(localDate.Year == localNow.Year ? "MMM d" : "MMM d, yyyy")}";
        }

        /// <summary>
        /// Returns the source branch display text (e.g., "feature/my-branch -> main"), or null if unavailable.
        /// </summary>
        public string BranchDisplayText
        {
            get
            {
                if (string.IsNullOrEmpty(SourceBranch))
                    return null;

                if (!string.IsNullOrEmpty(TargetBranch))
                    return $"{SourceBranch} \u2192 {TargetBranch}";

                return SourceBranch;
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
        /// Returns a short CI status display character.
        /// </summary>
        public string CiStatusIcon
        {
            get
            {
                return CiStatus switch
                {
                    "success" => "\u2713",
                    "failure" => "\u2717",
                    "pending" => "\u25CF",
                    _ => string.Empty,
                };
            }
        }
    }
}
