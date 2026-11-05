using System;

namespace StartScreen.Models.DevHub
{
    public enum DevHubRepoSummaryStatus
    {
        Ready,
        Unsupported,
        Unauthorized,
        Unavailable,
    }

    /// <summary>
    /// Repository-wide totals, never counts from a capped activity list. Null means unknown,
    /// not zero. Ready guarantees OpenPullRequests; RequestedReviews can still be unknown.
    /// </summary>
    public sealed class DevHubRepoSummary
    {
        public RemoteRepoIdentifier RepoIdentifier { get; }
        public int? OpenPullRequests { get; }

        /// <summary>
        /// Direct personal requests only, excluding team/group requests. GitHub counts pending
        /// requested_reviewers; Azure DevOps counts directly assigned reviewers with vote=0.
        /// </summary>
        public int? RequestedReviews { get; }
        public DevHubRepoSummaryStatus Status { get; }
        public string StatusMessage { get; }
        public DateTime FetchedAt { get; }

        internal DevHubRepoSummary(RemoteRepoIdentifier repo, DevHubRepoSummaryStatus status,
            int? openPullRequests = null, int? requestedReviews = null, string statusMessage = null)
        {
            RepoIdentifier = repo ?? throw new ArgumentNullException(nameof(repo));
            Status = status;
            OpenPullRequests = openPullRequests;
            RequestedReviews = requestedReviews;
            StatusMessage = statusMessage;
            FetchedAt = DateTime.UtcNow;
        }
    }
}
