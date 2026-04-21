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
                var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };
                var allProviders = DevHubProviderRegistry.GetAllProviders();

                // Phase 1: Authenticate all providers in parallel
                var authTasks = allProviders.Select(async provider =>
                {
                    var user = await provider.GetAuthenticatedUserAsync(cancellationToken);
                    return (provider, user);
                }).ToList();

                var authResults = await Task.WhenAll(authTasks);
                var authenticated = new List<IDevHubProvider>();

                foreach (var (provider, user) in authResults)
                {
                    if (user != null)
                    {
                        dashboard.Users.Add(user);
                        authenticated.Add(provider);
                    }
                }

                // Show the dashboard shell immediately once we know auth state
                progress?.Report(dashboard);

                if (authenticated.Count == 0)
                {
                    _cache.WriteDashboard(dashboard);
                    _currentDashboard = dashboard;
                    return dashboard;
                }

                // Fetch issues, PRs, and CI runs in parallel; report each as it arrives
                var issueTask = Task.Run(async () =>
                {
                    var tasks = authenticated.Select(p => p.GetUserIssuesAsync(cancellationToken)).ToList();
                    var results = await Task.WhenAll(tasks);
                    return results.SelectMany(r => r).OrderByDescending(i => i.CreatedAt).ToList();
                });

                var prTask = Task.Run(async () =>
                {
                    var tasks = authenticated.Select(p => p.GetUserPullRequestsAsync(cancellationToken)).ToList();
                    var results = await Task.WhenAll(tasks);
                    return results.SelectMany(r => r).OrderByDescending(pr => pr.UpdatedAt).ToList();
                });

                var ciTask = Task.Run(async () =>
                {
                    var tasks = authenticated.Select(p => p.GetUserCiRunsAsync(cancellationToken)).ToList();
                    var results = await Task.WhenAll(tasks);
                    return results.SelectMany(r => r).OrderByDescending(r => r.Timestamp).ToList();
                });

                // Report each category as it completes
                var remaining = new List<Task> { issueTask, prTask, ciTask };
                while (remaining.Count > 0)
                {
                    var completed = await Task.WhenAny(remaining);
                    remaining.Remove(completed);

                    if (completed == issueTask)
                        dashboard.Issues = issueTask.Result;
                    else if (completed == prTask)
                        dashboard.PullRequests = prTask.Result;
                    else if (completed == ciTask)
                        dashboard.CiRuns = ciTask.Result;

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
        /// Fetches detailed data for a specific repository from the appropriate provider.
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

            // If no cached data, try fetching from the appropriate provider
            var provider = DevHubProviderRegistry.GetProvider($"https://{repo.Host}/{repo.Owner}/{repo.Repo}");
            if (provider == null)
                return null;

            return await provider.GetRepoDetailAsync(repo, cancellationToken);
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
