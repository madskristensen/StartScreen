using System;
using System.Collections.Generic;
using System.Linq;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Dev Hub data filtered to a single repository.
    /// Created by <see cref="DevHubDashboard.FilterByRepo"/>.
    /// </summary>
    public sealed class DevHubRepoDetail
    {
        /// <summary>
        /// The repository this detail is for.
        /// </summary>
        public RemoteRepoIdentifier RepoIdentifier { get; set; }

        /// <summary>
        /// Open pull requests for this repository.
        /// </summary>
        public List<DevHubPullRequest> PullRequests { get; set; } = new List<DevHubPullRequest>();

        /// <summary>
        /// Assigned issues for this repository.
        /// </summary>
        public List<DevHubIssue> Issues { get; set; } = new List<DevHubIssue>();

        /// <summary>
        /// CI runs for this repository.
        /// </summary>
        public List<DevHubCiRun> CiRuns { get; set; } = new List<DevHubCiRun>();

        /// <summary>
        /// When this data was last fetched.
        /// </summary>
        public DateTime FetchedAt { get; set; }

        /// <summary>
        /// Whether this repo has any data to display.
        /// </summary>
        public bool HasData => PullRequests.Count > 0 || Issues.Count > 0 || CiRuns.Count > 0;

        /// <summary>
        /// Number of failed CI runs for this repo.
        /// </summary>
        public int FailedCiRuns => CiRuns.Count(r => r.Status == "failure");
    }
}
