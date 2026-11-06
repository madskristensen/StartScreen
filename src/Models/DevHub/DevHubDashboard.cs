using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace StartScreen.Models.DevHub
{
    /// <summary>
    /// Aggregates all Dev Hub data across all authenticated providers.
    /// Provides filtering capabilities for repo-specific views.
    /// </summary>
    public sealed class DevHubDashboard
    {
        /// <summary>
        /// All authenticated users (one per provider).
        /// </summary>
        public List<DevHubUser> Users { get; set; } = new List<DevHubUser>();

        /// <summary>
        /// Loaded pull requests, optionally including recent personal completions.
        /// </summary>
        public List<DevHubPullRequest> PullRequests { get; set; } = new List<DevHubPullRequest>();

        /// <summary>
        /// Loaded personal and incoming issues across all providers.
        /// </summary>
        public List<DevHubIssue> Issues { get; set; } = new List<DevHubIssue>();

        /// <summary>
        /// Recent CI runs across all providers.
        /// </summary>
        public List<DevHubCiRun> CiRuns { get; set; } = new List<DevHubCiRun>();

        /// <summary>
        /// When this dashboard data was last fetched.
        /// </summary>
        public DateTime FetchedAt { get; set; }

        public int ActivityLimit { get; set; } = 30;
        public bool IncludesCompleted { get; set; }

        [JsonIgnore]
        public bool IsRefreshing { get; set; }

        public string CacheContext { get; set; }
        public string IdentityContext { get; set; }
        internal int CredentialGeneration { get; set; }
        internal string QueryContext { get; set; }
        public Dictionary<string, DevHubActivitySnapshot> Sources { get; set; } = new Dictionary<string, DevHubActivitySnapshot>();

        public DevHubActivityStatus Status => Sources.Values.All(source => !source.IsStale) ? DevHubActivityStatus.Success
            : HasData || Sources.Values.Any(source => !source.IsStale || source.Status == DevHubActivityStatus.Partial) ? DevHubActivityStatus.Partial
            : Sources.Values.Any(source => source.Status == DevHubActivityStatus.Unauthorized) ? DevHubActivityStatus.Unauthorized
            : DevHubActivityStatus.Unavailable;

        public Dictionary<string, string> ActivityWarnings { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Whether any providers are authenticated.
        /// </summary>
        public bool HasAuthentication => Users.Count > 0;

        /// <summary>
        /// Whether a user authenticated against the given host is present.
        /// </summary>
        public bool HasProvider(string host)
        {
            for (int i = 0; i < Users.Count; i++)
            {
                if (string.Equals(Users[i].Host, host, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the dashboard has any data to display.
        /// </summary>
        public bool HasData => PullRequests.Count > 0 || Issues.Count > 0 || CiRuns.Count > 0;

        /// <summary>
        /// Total number of open pull requests.
        /// </summary>
        public int TotalPullRequests => PullRequests.Count;

        /// <summary>
        /// Total number of assigned issues.
        /// </summary>
        public int TotalIssues => Issues.Count;

        /// <summary>
        /// Number of failed CI runs.
        /// </summary>
        public int FailedCiRuns => CiRuns.Count(r => r.Status == "failure");

        /// <summary>
        /// Filters the dashboard data to show only items for a specific repository.
        /// Returns a new DevHubRepoDetail with the filtered data.
        /// </summary>
        public DevHubRepoDetail FilterByRepo(RemoteRepoIdentifier repo)
        {
            if (repo == null)
                return null;

            var warnings = ActivityWarnings.Where(pair => pair.Key == repo.ToString().ToLowerInvariant()
                || Sources.TryGetValue(pair.Key, out var source) && source.RepoIdentifier == null).Select(pair => pair.Value).Distinct().ToArray();

            return new DevHubRepoDetail
            {
                RepoIdentifier = repo,
                PullRequests = PullRequests
                    .Where(pr => repo.Equals(pr.RepoIdentifier))
                    .OrderByDescending(pr => pr.UpdatedAt)
                    .ToList(),
                Issues = Issues
                    .Where(i => repo.Equals(i.RepoIdentifier))
                    .OrderByDescending(i => i.UpdatedAt)
                    .ToList(),
                CiRuns = CiRuns
                    .Where(r => repo.Equals(r.RepoIdentifier))
                    .OrderByDescending(r => r.Timestamp)
                    .ToList(),
                FetchedAt = FetchedAt,
                Status = Status,
                ActivityWarning = warnings.Length == 0 ? null : string.Join("\n", warnings),
            };
        }
    }
}
