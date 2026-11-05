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
        private readonly IReadOnlyList<IDevHubProvider> _providers;
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _getCandidateRemoteUrls;
        private readonly Func<DevHubSortOrder> _getSortOrder;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private DevHubDashboard _currentDashboard;
        private volatile bool _isRefreshing;

        public DevHubService() : this(new DevHubCacheService())
        {
        }

        public DevHubService(DevHubCacheService cache, IReadOnlyList<IDevHubProvider> providers = null,
            Func<CancellationToken, Task<IReadOnlyList<string>>> getCandidateRemoteUrls = null,
            Func<DevHubSortOrder> getSortOrder = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _providers = providers ?? DevHubProviderRegistry.GetAllProviders();
            _getCandidateRemoteUrls = getCandidateRemoteUrls ?? GetCandidateRemoteUrlsAsync;
            _getSortOrder = getSortOrder ?? (() => Options.Instance.DevHubSortOrder);
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
        public async Task<DevHubDashboard> LoadFromCacheAsync(CancellationToken cancellationToken = default)
        {
            _currentDashboard = await _cache.ReadDashboardAsync(cancellationToken);
            return _currentDashboard;
        }

        /// <summary>
        /// Returns true if the cache is stale and a refresh should be triggered.
        /// </summary>
        public bool IsCacheStale() => _cache.IsCacheStale();

        /// <summary>
        /// Fetches fresh data from all authenticated providers and updates the dashboard.
        /// Reports progress incrementally so the UI can render data as it arrives.
        /// Categories and per-repository activity are published independently as they complete.
        /// A forced refresh waits for an active refresh and then fetches fresh authentication and data.
        /// </summary>
        public async Task<DevHubDashboard> RefreshAsync(CancellationToken cancellationToken, IProgress<DevHubDashboard> progress = null, bool forceRefresh = false)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await _refreshGate.WaitAsync(forceRefresh ? Timeout.Infinite : 0, cancellationToken))
                return _currentDashboard;

            _isRefreshing = true;

            try
            {
                var previousDashboard = _currentDashboard;
                var dashboard = new DevHubDashboard { FetchedAt = DateTime.UtcNow };

                // Phase 1: Authenticate all providers in parallel. A single provider may
                // surface multiple authenticated users when it spans multiple hosts (e.g.
                // Azure DevOps cloud + on-premises Server).
                var authTasks = _providers.Select(async provider =>
                {
                    var users = await provider.GetAuthenticatedUsersAsync(cancellationToken);
                    return (provider, users);
                }).ToList();

                var authResults = await Task.WhenAll(authTasks);
                cancellationToken.ThrowIfCancellationRequested();
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
                DevHubSortOrder sortOrder = _getSortOrder();

                // Providers without a user-level activity API (Azure DevOps) are populated by
                // fetching each MRU repo individually. Fetch once and split the result into the
                // issue / PR / CI categories below so each repo is only requested a single time.
                var perRepoActivityTask = FetchPerRepoActivityAsync(authenticated, cancellationToken);
                var issueTask = Task.WhenAll(authenticated.Select(
                    p => p.GetUserIssuesAsync(cancellationToken)));
                var prTask = Task.WhenAll(authenticated.Select(
                    p => p.GetUserPullRequestsAsync(cancellationToken)));
                var ciTask = Task.WhenAll(authenticated.Select(
                    p => p.GetUserCiRunsAsync(cancellationToken)));

                IReadOnlyList<DevHubRepoDetail> perRepo = Array.Empty<DevHubRepoDetail>();
                IEnumerable<DevHubIssue> issues = dashboard.Issues;
                IEnumerable<DevHubPullRequest> pullRequests = dashboard.PullRequests;
                IEnumerable<DevHubCiRun> ciRuns = dashboard.CiRuns;

                var remaining = new List<Task> { issueTask, prTask, ciTask, perRepoActivityTask };
                while (remaining.Count > 0)
                {
                    var completed = await Task.WhenAny(remaining);
                    cancellationToken.ThrowIfCancellationRequested();
                    remaining.Remove(completed);

                    if (completed == issueTask)
                    {
                        issues = (await issueTask).SelectMany(r => r).ToList();
                    }
                    else if (completed == prTask)
                    {
                        pullRequests = (await prTask).SelectMany(r => r).ToList();
                    }
                    else if (completed == ciTask)
                    {
                        ciRuns = (await ciTask).SelectMany(r => r).ToList();
                    }
                    else
                    {
                        perRepo = await perRepoActivityTask;
                    }

                    var replacedRepos = new HashSet<RemoteRepoIdentifier>(perRepo.Select(detail => detail.RepoIdentifier));
                    dashboard.Issues = DevHubItemSorter.Sort(
                        issues.Where(item => !replacedRepos.Contains(item.RepoIdentifier)).Concat(perRepo.SelectMany(detail => detail.Issues)),
                        sortOrder, item => item.RepoIdentifier, item => item.UpdatedAt);
                    dashboard.PullRequests = DevHubItemSorter.Sort(
                        pullRequests.Where(item => !replacedRepos.Contains(item.RepoIdentifier)).Concat(perRepo.SelectMany(detail => detail.PullRequests)),
                        sortOrder, item => item.RepoIdentifier, item => item.UpdatedAt);
                    dashboard.CiRuns = ciRuns.Where(item => !replacedRepos.Contains(item.RepoIdentifier))
                        .Concat(perRepo.SelectMany(detail => detail.CiRuns)).OrderByDescending(item => item.Timestamp).ToList();

                    _currentDashboard = dashboard;
                    progress?.Report(dashboard);
                }

                // Cache the final result
                cancellationToken.ThrowIfCancellationRequested();
                _cache.WriteDashboard(dashboard);

                return dashboard;
            }
            finally
            {
                _isRefreshing = false;
                _refreshGate.Release();
            }
        }

        /// <summary>
        /// Number of items fetched per repository during a full dashboard refresh. Kept small
        /// because many repos may be queried.
        /// </summary>
        private const int _dashboardRepoItemCount = 10;

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

            return await FetchRepoDetailFromProviderAsync(repo, _dashboardRepoItemCount, cancellationToken);
        }

        /// <summary>
        /// Fetches a separate repository view without replacing the aggregate dashboard or
        /// its cache. This prevents overlapping MRU selections and global refreshes from
        /// overwriting each other's results.
        /// </summary>
        public async Task<DevHubDashboard> ScopeToRepoAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null)
                return _currentDashboard;

            var detail = await FetchRepoDetailFromProviderAsync(repo, ScopedRepoItemCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (detail == null)
                return null;

            var dashboard = new DevHubDashboard
            {
                Users = _currentDashboard?.Users.ToList() ?? new List<DevHubUser>(),
                FetchedAt = detail.FetchedAt,
            };
            MergeRepoDetail(dashboard, repo, detail);
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
        private async Task<IReadOnlyList<DevHubRepoDetail>> FetchPerRepoActivityAsync(
            IReadOnlyList<IDevHubProvider> providers, CancellationToken cancellationToken)
        {
            try
            {
                var candidateUrls = await _getCandidateRemoteUrls(cancellationToken);
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
                        detailTasks.Add(provider.GetRepoDetailAsync(repo, _dashboardRepoItemCount, cancellationToken));
                    }
                }

                if (detailTasks.Count == 0)
                    return Array.Empty<DevHubRepoDetail>();

                var details = await Task.WhenAll(detailTasks);
                return details.Where(d => d != null).ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
        private static async Task<IReadOnlyList<string>> GetCandidateRemoteUrlsAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await MruService.GetRepositoryRemoteUrlsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
