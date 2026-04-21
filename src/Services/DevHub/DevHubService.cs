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

                // Phase 2: Fetch issues first (default visible tab)
                var issueTasks = authenticated.Select(p => p.GetUserIssuesAsync(cancellationToken)).ToList();
                var issueResults = await Task.WhenAll(issueTasks);
                foreach (var issues in issueResults)
                    dashboard.Issues.AddRange(issues);
                dashboard.Issues = dashboard.Issues.OrderByDescending(i => i.CreatedAt).ToList();
                _currentDashboard = dashboard;
                progress?.Report(dashboard);

                // Phase 3: Fetch PRs (second tab)
                var prTasks = authenticated.Select(p => p.GetUserPullRequestsAsync(cancellationToken)).ToList();
                var prResults = await Task.WhenAll(prTasks);
                foreach (var prs in prResults)
                    dashboard.PullRequests.AddRange(prs);
                dashboard.PullRequests = dashboard.PullRequests.OrderByDescending(pr => pr.UpdatedAt).ToList();
                _currentDashboard = dashboard;
                progress?.Report(dashboard);

                // Phase 4: Fetch CI runs last (slowest - multiple API calls per repo)
                var ciTasks = authenticated.Select(p => p.GetUserCiRunsAsync(cancellationToken)).ToList();
                var ciResults = await Task.WhenAll(ciTasks);
                foreach (var ci in ciResults)
                    dashboard.CiRuns.AddRange(ci);
                dashboard.CiRuns = dashboard.CiRuns.OrderByDescending(r => r.Timestamp).ToList();

                // Cache the final result
                _cache.WriteDashboard(dashboard);
                _currentDashboard = dashboard;

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
