using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// Orchestrates Dev Hub data fetching across all authenticated providers.
    /// Manages cache-first loading and background refresh.
    /// </summary>
    internal sealed class DevHubService
    {
        private readonly DevHubCacheService _cache;
        private DevHubDashboard _currentDashboard;
        private bool _isRefreshing;

        public DevHubService() : this(new DevHubCacheService())
        {
        }

        public DevHubService(DevHubCacheService cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <summary>
        /// Whether a refresh is currently in progress.
        /// </summary>
        public bool IsRefreshing => _isRefreshing;

        /// <summary>
        /// The current dashboard data (may be from cache).
        /// </summary>
        public DevHubDashboard CurrentDashboard => _currentDashboard;

        /// <summary>
        /// Loads the dashboard from cache for immediate display.
        /// Returns null if no cache exists.
        /// </summary>
        public async Task<DevHubDashboard> LoadFromCacheAsync()
        {
            _currentDashboard = await _cache.ReadDashboardAsync();
            return _currentDashboard;
        }

        /// <summary>
        /// Returns true if the cache is stale and a refresh should be triggered.
        /// </summary>
        public bool IsCacheStale() => _cache.IsCacheStale();

        /// <summary>
        /// Fetches fresh data from all authenticated providers and updates the dashboard.
        /// Reports progress incrementally so the UI can render data as it arrives.
        /// Issues are fetched first (visible tab), then PRs, then CI runs.
        /// </summary>
        public async Task<DevHubDashboard> RefreshAsync(CancellationToken cancellationToken, IProgress<DevHubDashboard> progress = null)
        {
            if (_isRefreshing)
                return _currentDashboard;

            _isRefreshing = true;

            try
            {
                var previousDashboard = _currentDashboard;
                var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };
                var allProviders = DevHubProviderRegistry.GetAllProviders();

                // Phase 1: Authenticate all providers in parallel. A single provider may
                // surface multiple authenticated users when it spans multiple hosts (e.g.
                // Azure DevOps cloud + on-premises Server).
                var authTasks = allProviders.Select(async provider =>
                {
                    var users = await provider.GetAuthenticatedUsersAsync(cancellationToken);
                    return (provider, users);
                }).ToList();

                var authResults = await Task.WhenAll(authTasks);
                var authenticated = new List<IDevHubProvider>();

                foreach (var (provider, users) in authResults)
                {
                    if (users != null && users.Count > 0)
                    {
                        dashboard.Users.AddRange(users);
                        authenticated.Add(provider);
                    }
                }

                if (authenticated.Count == 0)
                {
                    _cache.WriteDashboard(dashboard);
                    _currentDashboard = dashboard;
                    progress?.Report(dashboard);
                    return dashboard;
                }

                if (previousDashboard != null)
                {
                    dashboard.Issues = previousDashboard.Issues?.ToList() ?? new List<DevHubIssue>();
                    dashboard.PullRequests = previousDashboard.PullRequests?.ToList() ?? new List<DevHubPullRequest>();
                    dashboard.CiRuns = previousDashboard.CiRuns?.ToList() ?? new List<DevHubCiRun>();
                }

                // Show the dashboard shell immediately once we know auth state
                _currentDashboard = dashboard;
                progress?.Report(dashboard);

                // Fetch issues, PRs, and CI runs in parallel; report each as it arrives
                DevHubSortOrder sortOrder = Options.Instance.DevHubSortOrder;

                // Providers without a user-level activity API (Azure DevOps) are populated by
                // fetching each MRU repo individually. Fetch once and split the result into the
                // issue / PR / CI categories below so each repo is only requested a single time.
                var perRepoActivityTask = FetchPerRepoActivityAsync(authenticated, cancellationToken);

                var issueTask = Task.Run(async () =>
                {
                    var tasks = authenticated.Select(p => p.GetUserIssuesAsync(cancellationToken)).ToList();
                    var results = await Task.WhenAll(tasks);
                    var perRepo = await perRepoActivityTask;
                    return DevHubItemSorter.Sort(
                        results.SelectMany(r => r).Concat(perRepo.SelectMany(d => d.Issues)),
                        sortOrder,
                        i => i.RepoIdentifier,
                        i => i.UpdatedAt);
                });

                var prTask = Task.Run(async () =>
                {
                    var tasks = authenticated.Select(p => p.GetUserPullRequestsAsync(cancellationToken)).ToList();
                    var results = await Task.WhenAll(tasks);
                    var perRepo = await perRepoActivityTask;
                    return DevHubItemSorter.Sort(
                        results.SelectMany(r => r).Concat(perRepo.SelectMany(d => d.PullRequests)),
                        sortOrder,
                        pr => pr.RepoIdentifier,
                        pr => pr.UpdatedAt);
                });

                var ciTask = Task.Run(async () =>
                {
                    var tasks = authenticated.Select(p => p.GetUserCiRunsAsync(cancellationToken)).ToList();
                    var results = await Task.WhenAll(tasks);
                    var perRepo = await perRepoActivityTask;
                    return results.SelectMany(r => r)
                        .Concat(perRepo.SelectMany(d => d.CiRuns))
                        .OrderByDescending(r => r.Timestamp)
                        .ToList();
                });

                // Report each category as it completes
                var remaining = new List<Task> { issueTask, prTask, ciTask };
                while (remaining.Count > 0)
                {
                    var completed = await Task.WhenAny(remaining);
                    remaining.Remove(completed);

                    if (completed == issueTask)
                        dashboard.Issues = await issueTask;
                    else if (completed == prTask)
                        dashboard.PullRequests = await prTask;
                    else if (completed == ciTask)
                        dashboard.CiRuns = await ciTask;

                    _currentDashboard = dashboard;
                    progress?.Report(dashboard);
                }

                // Cache the final result
                _cache.WriteDashboard(dashboard);

                return dashboard;
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        /// <summary>
        /// Number of items fetched per repository during a full dashboard refresh. Kept small
        /// because many repos may be queried.
        /// </summary>
        private const int DashboardRepoItemCount = 10;

        /// <summary>
        /// Number of items fetched per category when the user scopes the dashboard to a single
        /// repository. Larger than the refresh count since only one repo is queried.
        /// </summary>
        public const int ScopedRepoItemCount = 25;

        /// <summary>
        /// Fetches detailed data for a specific repository from the appropriate provider.
        /// Returns cached/filtered data from the current dashboard when available.
        /// </summary>
        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null)
                return null;

            // First check if we can filter from the current dashboard data
            if (_currentDashboard != null)
            {
                var filtered = _currentDashboard.FilterByRepo(repo);
                if (filtered != null && filtered.HasData)
                    return filtered;
            }

            return await FetchRepoDetailFromProviderAsync(repo, DashboardRepoItemCount, cancellationToken);
        }

        /// <summary>
        /// Fetches fresh detail for a single repository (bypassing the dashboard filter shortcut)
        /// and merges it into the current dashboard, replacing any previously loaded items for
        /// that repo. Used by the "Scope to repo" command so the scoped view shows the full set
        /// of PRs / issues / builds rather than whatever happened to already be loaded.
        /// Returns the updated dashboard.
        /// </summary>
        public async Task<DevHubDashboard> ScopeToRepoAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null)
                return _currentDashboard;

            var detail = await FetchRepoDetailFromProviderAsync(repo, ScopedRepoItemCount, cancellationToken);
            if (detail == null)
                return _currentDashboard;

            var dashboard = _currentDashboard ?? new DevHubDashboard { FetchedAt = DateTime.UtcNow };
            MergeRepoDetail(dashboard, repo, detail);

            _currentDashboard = dashboard;
            _cache.WriteDashboard(dashboard);
            return dashboard;
        }

        /// <summary>
        /// Builds a representative URL the registry's host-based matching can recognize (including
        /// the "/_git/" segment for ADO so on-prem servers route to the Azure DevOps provider) and
        /// fetches repo detail from the matching provider.
        /// </summary>
        private static async Task<DevHubRepoDetail> FetchRepoDetailFromProviderAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken)
        {
            var lookupUrl = !string.IsNullOrEmpty(repo.Project) && !string.IsNullOrEmpty(repo.BaseUrl)
                ? $"{repo.BaseUrl}/{repo.Project}/_git/{repo.Repo}"
                : $"https://{repo.Host}/{repo.Owner}/{repo.Repo}";

            var provider = DevHubProviderRegistry.GetProvider(lookupUrl);
            if (provider == null)
                return null;

            return await provider.GetRepoDetailAsync(repo, maxItems, cancellationToken);
        }

        /// <summary>
        /// Replaces the dashboard's items for <paramref name="repo"/> with the freshly fetched set.
        /// </summary>
        internal static void MergeRepoDetail(DevHubDashboard dashboard, RemoteRepoIdentifier repo, DevHubRepoDetail detail)
        {
            dashboard.PullRequests = dashboard.PullRequests
                .Where(pr => !repo.Equals(pr.RepoIdentifier))
                .Concat(detail.PullRequests)
                .ToList();
            dashboard.Issues = dashboard.Issues
                .Where(i => !repo.Equals(i.RepoIdentifier))
                .Concat(detail.Issues)
                .ToList();
            dashboard.CiRuns = dashboard.CiRuns
                .Where(c => !repo.Equals(c.RepoIdentifier))
                .Concat(detail.CiRuns)
                .ToList();
        }

        /// <summary>
        /// Fetches per-repository activity for providers that have no user-level aggregation
        /// API (currently Azure DevOps). The repos are taken from the user's MRU list, so the
        /// number of requests stays proportional to what the user is actually working on.
        /// </summary>
        private static async Task<IReadOnlyList<DevHubRepoDetail>> FetchPerRepoActivityAsync(
            IReadOnlyList<IDevHubProvider> providers, CancellationToken cancellationToken)
        {
            try
            {
                var candidateUrls = await GetCandidateRemoteUrlsAsync();
                if (candidateUrls.Count == 0)
                    return Array.Empty<DevHubRepoDetail>();

                var detailTasks = new List<Task<DevHubRepoDetail>>();
                foreach (var provider in providers)
                {
                    var repos = await provider.GetActivityReposAsync(candidateUrls, cancellationToken);
                    if (repos == null)
                        continue;

                    foreach (var repo in repos)
                    {
                        detailTasks.Add(provider.GetRepoDetailAsync(repo, DashboardRepoItemCount, cancellationToken));
                    }
                }

                if (detailTasks.Count == 0)
                    return Array.Empty<DevHubRepoDetail>();

                var details = await Task.WhenAll(detailTasks);
                return details.Where(d => d != null).ToList();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubRepoDetail>();
            }
        }

        /// <summary>
        /// Reads distinct git remote URLs from the MRU list to use as the candidate set of
        /// repositories for per-repo activity fetching.
        /// </summary>
        private static async Task<IReadOnlyList<string>> GetCandidateRemoteUrlsAsync()
        {
            try
            {
                var items = await MruService.GetMruItemsAsync();
                return items
                    .Select(i => i.RemoteUrl)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Clears cached data.
        /// </summary>
        public void ClearCache()
        {
            _cache.ClearCache();
            _currentDashboard = null;
        }
    }
}
