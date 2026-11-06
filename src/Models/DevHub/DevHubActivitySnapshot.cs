using System;
using System.Collections.Generic;

namespace StartScreen.Models.DevHub
{
    public enum DevHubActivityStatus
    {
        Success,
        Partial,
        Unavailable,
        Unauthorized,
    }

    /// <summary>A persisted source, including its last successful data and latest refresh outcome.</summary>
    public sealed class DevHubActivitySnapshot
    {
        public DevHubActivityStatus Status { get; set; }
        public DateTime FetchedAt { get; set; }
        public List<DevHubPullRequest> PullRequests { get; set; } = new List<DevHubPullRequest>();
        public List<DevHubIssue> Issues { get; set; } = new List<DevHubIssue>();
        public List<DevHubCiRun> CiRuns { get; set; } = new List<DevHubCiRun>();
        public List<RemoteRepoIdentifier> ActivityRepositories { get; set; } = new List<RemoteRepoIdentifier>();
        public RemoteRepoIdentifier RepoIdentifier { get; set; }
        public bool HasCategoryOutcomes { get; set; }
        public DevHubActivityStatus PullRequestsStatus { get; set; }
        public DevHubActivityStatus IssuesStatus { get; set; }
        public DevHubActivityStatus CiRunsStatus { get; set; }
        public string Message { get; set; }
        public bool IsStale => Status != DevHubActivityStatus.Success;
    }
}