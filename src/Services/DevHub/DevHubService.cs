using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
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
        private readonly Func<IReadOnlyList<IDevHubProvider>> _getProviders;
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _getCandidateRemoteUrls;
        private readonly Func<DevHubSortOrder> _getSortOrder;
        private readonly Func<string> _getQueryContext;
        private readonly Func<int> _getCredentialGeneration;
        internal const int ScopedSourceCapacity = 32;
        private readonly Dictionary<string, ScopedSource> _scopedSources = new Dictionary<string, ScopedSource>();
        private readonly Func<DateTimeOffset> _utcNow;
        private string _scopedContext;
        private string _scopedIdentity;
        private string _scopedQuery;
        private int _scopedCredentialGeneration;
        private long _scopeRequestId;
        private long _scopedContextRequestId;
        private long _scopedEpoch;
        private readonly object _scopeGate = new object();
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private volatile DevHubDashboard _currentDashboard;
        private volatile bool _isRefreshing;
        private volatile bool _cacheNeedsRefresh;

        public DevHubService() : this(new DevHubCacheService())
        {
        }

        public DevHubService(DevHubCacheService cache, IReadOnlyList<IDevHubProvider> providers = null,
            Func<CancellationToken, Task<IReadOnlyList<string>>> getCandidateRemoteUrls = null,
            Func<DevHubSortOrder> getSortOrder = null, Func<string> getQueryContext = null, Func<int> getCredentialGeneration = null,
            Func<DateTimeOffset> utcNow = null, Func<IReadOnlyList<IDevHubProvider>> getProviders = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _getProviders = getProviders ?? (providers == null ? DevHubProviderRegistry.GetEnabledProviders : () => providers);
            _getCandidateRemoteUrls = getCandidateRemoteUrls ?? GetCandidateRemoteUrlsAsync;
            _getSortOrder = getSortOrder ?? (() => Options.Instance.DevHubSortOrder);
            _getQueryContext = getQueryContext ?? (() => Options.Instance.DevHubSearchQuery ?? string.Empty);
            _getCredentialGeneration = getCredentialGeneration ?? (() => DevHubCredentialHelper.AccountGeneration);
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Whether a refresh is currently in progress.
        /// </summary>
        public bool IsRefreshing => _isRefreshing;

        /// <summary>
        /// The current dashboard data (may be from cache).
        /// </summary>
        public DevHubDashboard CurrentDashboard
        {
            get
            {
                var dashboard = _currentDashboard;
                return dashboard != null && dashboard.CredentialGeneration == _getCredentialGeneration()
                    && dashboard.QueryContext == _getQueryContext() ? dashboard : null;
            }
        }

        /// <summary>
        /// Loads the dashboard from cache for immediate display.
        /// Returns null if no cache exists.
        /// </summary>
        public async Task<DevHubDashboard> LoadFromCacheAsync(CancellationToken cancellationToken = default, DevHubActivityOptions options = null,
            IProgress<DevHubDashboard> progress = null, IProgress<(int Completed, int Total)> workProgress = null)
        {
            var completedWork = 0;
            var totalWork = 2;
            workProgress?.Report((completedWork, totalWork));
            _cacheNeedsRefresh = false;
            var cached = await _cache.ReadDashboardAsync(cancellationToken);
            workProgress?.Report((++completedWork, totalWork));
            if (cached == null || string.IsNullOrEmpty(cached.CacheContext))
                return _currentDashboard = null;
            var generation = _getCredentialGeneration();
            var queryContext = _getQueryContext();
            var candidates = await DevHubActivityRequest.RunAsync(() => _getCandidateRemoteUrls(cancellationToken), urls => urls.Count > 0, cancellationToken);
            workProgress?.Report((++completedWork, totalWork));
            options = options ?? new DevHubActivityOptions();
            if (candidates.Status != DevHubActivityStatus.Success || generation != _getCredentialGeneration() || queryContext != _getQueryContext()
                || cached.CacheContext != CreateContext(cached.Users, options, candidates.Value))
                return _currentDashboard = null;

            var refreshContext = new DevHubRefreshContext(candidates.Value, candidates.Status);
            var dashboard = new DevHubDashboard
            {
                FetchedAt = cached.FetchedAt,
                ActivityLimit = cached.ActivityLimit,
                IncludesCompleted = cached.IncludesCompleted,
                CredentialGeneration = generation,
                QueryContext = queryContext,
            };
            var providers = _getProviders();
            totalWork += providers.Count;
            workProgress?.Report((completedWork, totalWork));
            var authentications = providers.Select(async provider =>
                (Provider: provider, Result: await AuthenticateAsync(provider, cancellationToken, refreshContext))).ToList();
            var restored = false;
            while (authentications.Count > 0)
            {
                var completed = await Task.WhenAny(authentications).WithCancellation(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                authentications.Remove(completed);
                var (provider, result) = await completed;
                workProgress?.Report((++completedWork, totalWork));
                if (generation != _getCredentialGeneration() || queryContext != _getQueryContext())
                {
                    SetCurrentDashboard(null);
                    progress?.Report(null);
                    return null;
                }
                var users = result.Value ?? Array.Empty<DevHubUser>();
                _cacheNeedsRefresh |= result.Status != DevHubActivityStatus.Success;
                dashboard.Users.AddRange(users);
                var previousUsers = cached.Users.Where(user => user.ProviderName == provider.DisplayName).ToList();
                if (users.Count > 0 && CreateIdentityContext(users) == CreateIdentityContext(previousUsers))
                {
                    var prefix = provider.DisplayName + "/";
                    foreach (var source in cached.Sources.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
                        dashboard.Sources[source.Key] = source.Value;
                    restored = true;
                }
                else if (previousUsers.Count > 0 || users.Count > 0)
                    _cacheNeedsRefresh = true;

                if (restored)
                {
                    dashboard.IsRefreshing = authentications.Count > 0;
                    dashboard.IdentityContext = CreateIdentityContext(dashboard.Users);
                    dashboard.CacheContext = CreateContext(dashboard.Users, options, candidates.Value);
                    Publish(dashboard, progress);
                }
            }
            if (!restored)
            {
                SetCurrentDashboard(null);
                return null;
            }
            _cacheNeedsRefresh |= dashboard.CacheContext != cached.CacheContext;
            return CurrentDashboard;
        }

        /// <summary>
        /// Returns true if the cache is stale and a refresh should be triggered.
        /// </summary>
        public bool IsCacheStale()
        {
            var current = CurrentDashboard;
            return _cacheNeedsRefresh || current == null || current.Status != DevHubActivityStatus.Success || _cache.IsCacheStale();
        }

        /// <summary>
        /// Fetches fresh data from all authenticated providers and updates the dashboard.
        /// Reports progress incrementally so the UI can render data as it arrives.
        /// Categories and per-repository activity are published independently as they complete.
        /// A forced refresh waits for an active refresh and then fetches fresh authentication and data.
        /// </summary>
        public async Task<DevHubDashboard> RefreshAsync(CancellationToken cancellationToken, IProgress<DevHubDashboard> progress = null,
            bool forceRefresh = false, DevHubActivityOptions options = null, IProgress<(int Completed, int Total)> workProgress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options = options ?? new DevHubActivityOptions();

            if (!await _refreshGate.WaitAsync(forceRefresh ? Timeout.Infinite : 0, cancellationToken))
                return CurrentDashboard;

            _isRefreshing = true;

            try
            {
                var completedWork = 0;
                var totalWork = 2;
                workProgress?.Report((completedWork, totalWork));
                var previousDashboard = _currentDashboard;
                var authenticationGeneration = _getCredentialGeneration();
                var queryContext = _getQueryContext();
                var candidates = await DevHubActivityRequest.RunAsync(() => _getCandidateRemoteUrls(cancellationToken),
                    urls => urls.Count > 0, cancellationToken);
                workProgress?.Report((++completedWork, totalWork));
                var refreshContext = new DevHubRefreshContext(candidates.Value, candidates.Status);
                options = new DevHubActivityOptions(options.MaxItems, options.IncludeCompleted, refreshContext);
                var dashboard = new DevHubDashboard
                {
                    FetchedAt = DateTime.UtcNow,
                    ActivityLimit = options.MaxItems,
                    IncludesCompleted = options.IncludeCompleted,
                    IsRefreshing = true,
                    CredentialGeneration = authenticationGeneration,
                    QueryContext = queryContext,
                };

                // Phase 1: Authenticate all providers in parallel. A single provider may
                // surface multiple authenticated users when it spans multiple hosts (e.g.
                // Azure DevOps cloud + on-premises Server).
                var providers = _getProviders();
                totalWork += providers.Count;
                workProgress?.Report((completedWork, totalWork));
                var authTasks = providers.Select(async provider =>
                {
                    var result = await AuthenticateAsync(provider, cancellationToken, refreshContext);
                    workProgress?.Report((Interlocked.Increment(ref completedWork), totalWork));
                    return (provider, result);
                }).ToList();

                var authResults = await Task.WhenAll(authTasks);
                cancellationToken.ThrowIfCancellationRequested();
                if (authenticationGeneration != _getCredentialGeneration())
                    return InvalidateContext(progress);
                var authenticated = new List<IDevHubProvider>();

                foreach (var (provider, result) in authResults)
                {
                    var users = result.Value;
                    if (users != null && users.Count > 0)
                    {
                        dashboard.Users.AddRange(users);
                        authenticated.Add(provider);
                    }
                    if (result.Status != DevHubActivityStatus.Success)
                    {
                        var key = provider.DisplayName + "/authentication";
                        var message = !string.IsNullOrEmpty(result.Message) ? result.Message
                            : $"{provider.DisplayName}: authentication {result.Status.ToString().ToLowerInvariant()}. Activity cannot be verified. Sign in or refresh to retry.";
                        dashboard.Sources[key] = new DevHubActivitySnapshot { Status = result.Status, Message = message };
                        dashboard.ActivityWarnings[key] = message;
                    }
                }

                dashboard.IdentityContext = CreateIdentityContext(dashboard.Users);

                if (authenticationGeneration != _getCredentialGeneration() || queryContext != _getQueryContext())
                    return InvalidateContext(progress);
                var candidateUrls = refreshContext.RemoteUrls;
                dashboard.CacheContext = candidates.Status == DevHubActivityStatus.Success
                    ? CreateContext(dashboard.Users, options, candidateUrls) : null;
                if (candidates.Status != DevHubActivityStatus.Success)
                {
                    var message = !string.IsNullOrEmpty(candidates.Message) ? candidates.Message : "Repository discovery is unavailable. Refresh to retry.";
                    dashboard.Sources["repositories/discovery"] = new DevHubActivitySnapshot { Status = candidates.Status, Message = message };
                    dashboard.ActivityWarnings["repositories/discovery"] = message;
                }
                if (string.IsNullOrEmpty(dashboard.CacheContext) || previousDashboard?.CacheContext != dashboard.CacheContext)
                    previousDashboard = null;
                var generation = authenticationGeneration;

                if (authenticated.Count == 0)
                {
                    dashboard.IsRefreshing = false;
                    await _cache.WriteDashboardAsync(dashboard, cancellationToken);
                    workProgress?.Report((++completedWork, totalWork));
                    _cacheNeedsRefresh = false;
                    SetCurrentDashboard(dashboard);
                    progress?.Report(dashboard);
                    return dashboard;
                }

                totalWork += authenticated.Count * 4;
                workProgress?.Report((completedWork, totalWork));
                foreach (var pair in previousDashboard?.Sources ?? new Dictionary<string, DevHubActivitySnapshot>())
                {
                    if (!pair.Key.EndsWith("/authentication", StringComparison.Ordinal))
                        dashboard.Sources[pair.Key] = pair.Value;
                }
                Publish(dashboard, progress);

                using var requests = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var requestToken = requests.Token;
                var remaining = new List<Task<(string Key, DevHubActivitySnapshot Snapshot)>>();
                var repositoryTasks = new HashSet<Task<(string Key, DevHubActivitySnapshot Snapshot)>>();
                var repositoryQueue = new Queue<(IDevHubProvider Provider, RemoteRepoIdentifier Repo)>();
                foreach (var provider in authenticated)
                {
                    remaining.Add(FetchSourceAsync(provider.DisplayName + "/issues",
                        () => FetchActivityAsync(provider, DevHubActivityKind.Issues, options, null, requestToken)));
                    remaining.Add(FetchSourceAsync(provider.DisplayName + "/pulls",
                        () => FetchActivityAsync(provider, DevHubActivityKind.PullRequests, options, null, requestToken)));
                    remaining.Add(FetchSourceAsync(provider.DisplayName + "/builds",
                        () => FetchActivityAsync(provider, DevHubActivityKind.CiRuns, options, null, requestToken)));
                    remaining.Add(FetchSourceAsync(provider.DisplayName + "/discovery", async () =>
                    {
                        var result = await DevHubActivityRequest.RunAsync(() => provider.GetActivityReposAsync(candidateUrls, requestToken),
                            items => items.Count > 0, requestToken);
                        return new DevHubActivitySnapshot { Status = result.Status, Message = result.Message,
                            ActivityRepositories = result.Value?.ToList() ?? new List<RemoteRepoIdentifier>() };
                    }));
                }

                try
                {
                    while (remaining.Count > 0 || repositoryQueue.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        while (repositoryQueue.Count > 0 && repositoryTasks.Count < DashboardRepositoryConcurrency)
                        {
                            var (provider, repo) = repositoryQueue.Dequeue();
                            var repoOptions = new DevHubActivityOptions(options.MaxItems == DevHubActivityOptions.DefaultLimit
                                ? _dashboardRepoItemCount : options.MaxItems, options.IncludeCompleted, refreshContext);
                            var task = FetchSourceAsync(provider.DisplayName + "/repo/" + repo.ToString().ToLowerInvariant(),
                                () => FetchActivityAsync(provider, DevHubActivityKind.Repository, repoOptions, repo, requestToken));
                            repositoryTasks.Add(task);
                            remaining.Add(task);
                        }

                        var completed = await Task.WhenAny(remaining);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (generation != _getCredentialGeneration() || queryContext != _getQueryContext())
                            return InvalidateContext(progress);
                        remaining.Remove(completed);
                        repositoryTasks.Remove(completed);
                        var (key, snapshot) = await completed;
                        dashboard.Sources.TryGetValue(key, out var previous);
                        dashboard.Sources[key] = RetainFailedSource(snapshot, previous);
                        if (key.EndsWith("/discovery", StringComparison.Ordinal))
                        {
                            var provider = authenticated.First(candidate => key == candidate.DisplayName + "/discovery");
                            var prefix = provider.DisplayName + "/repo/";
                            var validKeys = new HashSet<string>(snapshot.ActivityRepositories.Select(repo => prefix + repo.ToString().ToLowerInvariant()));
                            foreach (var pair in dashboard.Sources.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                            {
                                if (snapshot.IsStale)
                                    dashboard.Sources[pair.Key] = RetainFailedSource(new DevHubActivitySnapshot
                                    {
                                        RepoIdentifier = pair.Value.RepoIdentifier,
                                        Status = snapshot.Status,
                                    }, pair.Value);
                                else if (!validKeys.Contains(pair.Key))
                                    dashboard.Sources.Remove(pair.Key);
                            }

                            foreach (var repo in snapshot.ActivityRepositories)
                                repositoryQueue.Enqueue((provider, repo));
                            totalWork += snapshot.ActivityRepositories.Count;
                        }
                        workProgress?.Report((++completedWork, totalWork));
                        Publish(dashboard, progress);
                    }
                }
                finally
                {
                    requests.Cancel();
                    try
                    {
                        await Task.WhenAll(remaining);
                    }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                    }
                }

                // Cache the final result
                cancellationToken.ThrowIfCancellationRequested();
                dashboard.IsRefreshing = false;
                dashboard.FetchedAt = DateTime.UtcNow;
                var final = Publish(dashboard, progress);
                await _cache.WriteDashboardAsync(final, cancellationToken);
                workProgress?.Report((++completedWork, totalWork));
                _cacheNeedsRefresh = false;

                return final;
            }
            finally
            {
                _isRefreshing = false;
                _refreshGate.Release();
            }
        }

        private string CreateContext(IEnumerable<DevHubUser> users, DevHubActivityOptions options, IEnumerable<string> urls)
        {
            return HashContext(JsonSerializer.Serialize(new
            {
                Version = 3,
                Identities = CreateIdentityContext(users),
                options.MaxItems,
                options.IncludeCompleted,
                Query = _getQueryContext(),
                Repositories = urls.Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            }));
        }

        private static string CreateIdentityContext(IEnumerable<DevHubUser> users)
        {
            return HashContext(JsonSerializer.Serialize(users.Select(user => new { user.ProviderName, user.Host, user.IdentityId, user.Username })
                .OrderBy(user => user.ProviderName, StringComparer.Ordinal).ThenBy(user => user.Host, StringComparer.Ordinal)
                .ThenBy(user => user.IdentityId, StringComparer.Ordinal).ThenBy(user => user.Username, StringComparer.Ordinal).ToArray()));
        }

        private static string HashContext(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private void SetCurrentDashboard(DevHubDashboard dashboard)
        {
            lock (_scopeGate)
            {
                PruneScopedSources();
                if (_scopedIdentity != null && !string.IsNullOrEmpty(dashboard?.IdentityContext) && _scopedIdentity != dashboard.IdentityContext)
                    ClearScopedSources();
            }
            _currentDashboard = dashboard;
        }

        private DevHubDashboard InvalidateContext(IProgress<DevHubDashboard> progress = null)
        {
            lock (_scopeGate)
                PruneScopedSources();
            var dashboard = new DevHubDashboard { CredentialGeneration = _getCredentialGeneration(), QueryContext = _getQueryContext() };
            dashboard.Sources["authentication"] = new DevHubActivitySnapshot { Status = DevHubActivityStatus.Unauthorized };
            dashboard.ActivityWarnings["authentication"] = "The account or activity settings changed during refresh. Refresh again to load the current account.";
            SetCurrentDashboard(dashboard);
            progress?.Report(dashboard);
            return dashboard;
        }

        private static Task<DevHubActivityResult<IReadOnlyList<DevHubUser>>> AuthenticateAsync(IDevHubProvider provider,
            CancellationToken cancellationToken, DevHubRefreshContext context = null)
        {
            return DevHubActivityRequest.RunAsync(async () =>
            {
                var users = context != null && provider is IDevHubRefreshProvider refreshProvider
                    ? await refreshProvider.GetAuthenticatedUsersAsync(context, cancellationToken)
                    : await provider.GetAuthenticatedUsersAsync(cancellationToken);
                return users ?? Array.Empty<DevHubUser>();
            }, users => users.Count > 0, cancellationToken);
        }

        private static async Task<(string Key, DevHubActivitySnapshot Snapshot)> FetchSourceAsync(string key, Func<Task<DevHubActivitySnapshot>> fetch)
        {
            var snapshot = await fetch();
            snapshot.FetchedAt = DateTime.UtcNow;
            OutputLog.Info("Activity", $"{key}: {snapshot.Status}; {snapshot.PullRequests.Count} PRs, {snapshot.Issues.Count} issues, "
                + $"{snapshot.CiRuns.Count} builds, {snapshot.ActivityRepositories.Count} repositories.");
            return (key, snapshot);
        }

        internal static DevHubActivitySnapshot RetainFailedSource(DevHubActivitySnapshot fresh, DevHubActivitySnapshot previous)
        {
            if (!fresh.IsStale || previous == null)
                return fresh;
            if (!fresh.HasCategoryOutcomes || fresh.PullRequestsStatus != DevHubActivityStatus.Success)
                fresh.PullRequests = MergePullRequests(previous.PullRequests, fresh.PullRequests).ToList();
            if (!fresh.HasCategoryOutcomes || fresh.IssuesStatus != DevHubActivityStatus.Success)
                fresh.Issues = MergeIssues(previous.Issues, fresh.Issues).ToList();
            if (!fresh.HasCategoryOutcomes || fresh.CiRunsStatus != DevHubActivityStatus.Success)
                fresh.CiRuns = fresh.CiRuns.Concat(previous.CiRuns)
                    .GroupBy(run => run.WebUrl ?? $"{run.RepoIdentifier}/{run.Timestamp:o}/{run.Name}")
                    .Select(group => group.First()).ToList();
            fresh.ActivityRepositories = fresh.ActivityRepositories.Concat(previous.ActivityRepositories).Distinct().ToList();
            fresh.FetchedAt = previous.FetchedAt;
            return fresh;
        }

        private DevHubDashboard Publish(DevHubDashboard dashboard, IProgress<DevHubDashboard> progress)
        {
            dashboard.ActivityWarnings.Clear();
            var repos = dashboard.Sources.Values.Where(source => source.RepoIdentifier != null).ToList();
            var personal = dashboard.Sources.Values.Where(source => source.RepoIdentifier == null).ToList();
            var replacedRepos = new HashSet<RemoteRepoIdentifier>(repos.Select(source => source.RepoIdentifier));
            var sortOrder = _getSortOrder();
            dashboard.Issues = DevHubItemSorter.Sort(MergeIssues(personal.SelectMany(source => source.Issues)
                .Where(item => !replacedRepos.Contains(item.RepoIdentifier)), repos.SelectMany(source => source.Issues)),
                sortOrder, item => item.RepoIdentifier, item => item.UpdatedAt);
            dashboard.PullRequests = DevHubItemSorter.Sort(MergePullRequests(personal.SelectMany(source => source.PullRequests)
                .Where(item => item.IsAuthoredByCurrentUser || item.IsAssignedToCurrentUser || item.IsReviewRequestedForCurrentUser
                    || item.IsMentioned || !replacedRepos.Contains(item.RepoIdentifier)), repos.SelectMany(source => source.PullRequests)),
                sortOrder, item => item.RepoIdentifier, item => item.UpdatedAt);
            dashboard.CiRuns = personal.SelectMany(source => source.CiRuns).Where(item => !replacedRepos.Contains(item.RepoIdentifier))
                .Concat(repos.SelectMany(source => source.CiRuns)).OrderByDescending(item => item.Timestamp).ToList();
            foreach (var pair in dashboard.Sources)
            {
                var source = pair.Value;
                var key = source.RepoIdentifier?.ToString().ToLowerInvariant() ?? pair.Key;
                if (source.IsStale)
                    dashboard.ActivityWarnings[key] = !string.IsNullOrEmpty(source.Message) ? source.Message
                        : $"{pair.Key}: {source.Status}. Showing stale activity where available. Refresh to retry.";
                else if (!string.IsNullOrEmpty(source.Message))
                    dashboard.ActivityWarnings[key] = source.Message;
                else
                    dashboard.ActivityWarnings.Remove(key);
            }
            var published = new DevHubDashboard
            {
                Users = dashboard.Users.ToList(),
                PullRequests = dashboard.PullRequests.ToList(),
                Issues = dashboard.Issues.ToList(),
                CiRuns = dashboard.CiRuns.ToList(),
                Sources = dashboard.Sources.ToDictionary(pair => pair.Key, pair => pair.Value),
                ActivityWarnings = dashboard.ActivityWarnings.ToDictionary(pair => pair.Key, pair => pair.Value),
                FetchedAt = dashboard.FetchedAt,
                ActivityLimit = dashboard.ActivityLimit,
                IncludesCompleted = dashboard.IncludesCompleted,
                IsRefreshing = dashboard.IsRefreshing,
                CacheContext = dashboard.CacheContext,
                IdentityContext = dashboard.IdentityContext,
                CredentialGeneration = dashboard.CredentialGeneration,
                QueryContext = dashboard.QueryContext,
            };
            SetCurrentDashboard(published);
            progress?.Report(published);
            return published;
        }

        private static Task<DevHubActivitySnapshot> FetchActivityAsync(IDevHubProvider provider, DevHubActivityKind kind,
            DevHubActivityOptions options, RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return provider is IDevHubActivitySource source ? source.FetchActivityAsync(kind, options, repo, cancellationToken)
                : DevHubActivityRequest.FetchAsync(provider, kind, options, repo, cancellationToken);
        }

        /// <summary>
        /// Number of items fetched per repository during a full dashboard refresh. Kept small
        /// because many repos may be queried.
        /// </summary>
        private const int _dashboardRepoItemCount = 10;

        /// <summary>Maximum active repository detail operations across all dashboard providers.</summary>
        internal const int DashboardRepositoryConcurrency = 3;

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

            var dashboard = await ScopeToRepoAsync(repo, cancellationToken, new DevHubActivityOptions(_dashboardRepoItemCount));
            return dashboard?.FilterByRepo(repo);
        }

        /// <summary>
        /// Fetches a separate repository view without replacing the aggregate dashboard or
        /// its cache. This prevents overlapping MRU selections and global refreshes from
        /// overwriting each other's results.
        /// </summary>
        public async Task<DevHubDashboard> ScopeToRepoAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken,
            DevHubActivityOptions options = null)
        {
            if (repo == null)
                return CurrentDashboard;

            options = options ?? new DevHubActivityOptions(ScopedRepoItemCount);
            var requestId = Interlocked.Increment(ref _scopeRequestId);
            var authenticationGeneration = _getCredentialGeneration();
            var queryContext = _getQueryContext();
            var refreshContext = new DevHubRefreshContext(new[] { GetLookupUrl(repo) });
            var authResults = await Task.WhenAll(_getProviders().Select(async provider => (Provider: provider, Result: await AuthenticateAsync(provider, cancellationToken, refreshContext))));
            if (authenticationGeneration != _getCredentialGeneration() || queryContext != _getQueryContext())
                return InvalidateContext();
            var users = authResults.SelectMany(pair => pair.Result.Value ?? Array.Empty<DevHubUser>()).ToList();
            var context = CreateContext(users, options, new[] { GetLookupUrl(repo) });
            var generation = _getCredentialGeneration();
            long scopeEpoch;
            lock (_scopeGate)
            {
                var scopeContext = generation + ":" + CreateContext(users, options, Array.Empty<string>());
                PruneScopedSources();
                if (_scopedContext != scopeContext && requestId >= _scopedContextRequestId)
                {
                    ClearScopedSources();
                    _scopedContext = scopeContext;
                    _scopedIdentity = CreateIdentityContext(users);
                    _scopedQuery = queryContext;
                    _scopedCredentialGeneration = generation;
                }
                scopeEpoch = _scopedContext == scopeContext ? _scopedEpoch : -1;
                _scopedContextRequestId = Math.Max(_scopedContextRequestId, requestId);
            }
            var dashboard = new DevHubDashboard
            {
                Users = users,
                IdentityContext = CreateIdentityContext(users),
                CredentialGeneration = authenticationGeneration,
                QueryContext = queryContext,
                FetchedAt = DateTime.UtcNow,
                ActivityLimit = options.MaxItems,
                IncludesCompleted = options.IncludeCompleted,
                CacheContext = context,
            };
            var matching = authResults.FirstOrDefault(pair => pair.Provider.CanHandle(GetLookupUrl(repo)));
            var verified = matching.Provider != null && matching.Result.Value?.Any(user => string.IsNullOrEmpty(user.Host)
                || string.Equals(user.Host, repo.Host, StringComparison.OrdinalIgnoreCase)) == true;
            var snapshot = verified ? await FetchActivityAsync(matching.Provider, DevHubActivityKind.Repository, options, repo, cancellationToken)
                : new DevHubActivitySnapshot
                {
                    Status = matching.Provider == null ? DevHubActivityStatus.Unavailable
                        : matching.Result.Status == DevHubActivityStatus.Success || matching.Result.Status == DevHubActivityStatus.Partial
                            ? DevHubActivityStatus.Unauthorized : matching.Result.Status,
                    RepoIdentifier = repo,
                };
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _getCredentialGeneration() || queryContext != _getQueryContext()
                || context != CreateContext(users, options, new[] { GetLookupUrl(repo) }))
                return InvalidateContext();
            lock (_scopeGate)
            {
                PruneScopedSources();
                if (verified && scopeEpoch == _scopedEpoch)
                {
                    _scopedSources.TryGetValue(context, out var previous);
                    snapshot = RetainFailedSource(snapshot, previous?.Snapshot);
                    _scopedSources[context] = new ScopedSource(snapshot, _utcNow());
                    while (_scopedSources.Count > ScopedSourceCapacity)
                        _scopedSources.Remove(_scopedSources.OrderBy(pair => pair.Value.StoredAt).First().Key);
                }
            }
            dashboard.Sources[repo.ToString().ToLowerInvariant()] = snapshot;
            dashboard.PullRequests = snapshot.PullRequests;
            dashboard.Issues = snapshot.Issues;
            dashboard.CiRuns = snapshot.CiRuns;
            if (snapshot.IsStale)
                dashboard.ActivityWarnings[repo.ToString().ToLowerInvariant()] = !string.IsNullOrEmpty(snapshot.Message) ? snapshot.Message
                    : $"Repository activity {snapshot.Status.ToString().ToLowerInvariant()}. Showing stale activity where available. Refresh to retry.";
            return dashboard;
        }

        /// <summary>
        /// Builds a representative URL the registry's host-based matching can recognize (including
        /// the "/_git/" segment for ADO so on-prem servers route to the Azure DevOps provider) and
        /// fetches repo detail from the matching provider.
        /// </summary>
        private static string GetLookupUrl(RemoteRepoIdentifier repo)
        {
            return !string.IsNullOrEmpty(repo.Project) && !string.IsNullOrEmpty(repo.BaseUrl)
                ? $"{repo.BaseUrl}/{repo.Project}/_git/{repo.Repo}"
                : !string.IsNullOrEmpty(repo.Project) ? $"https://{repo.Host}/{repo.Owner}/{repo.Project}/_git/{repo.Repo}"
                : $"https://{repo.Host}/{repo.Owner}/{repo.Repo}";
        }

        /// <summary>
        /// Replaces the dashboard's items for <paramref name="repo"/> with the freshly fetched set.
        /// </summary>
        internal static void MergeRepoDetail(DevHubDashboard dashboard, RemoteRepoIdentifier repo, DevHubRepoDetail detail)
        {
            dashboard.PullRequests = MergePullRequests(dashboard.PullRequests
                .Where(pr => pr.IsAuthoredByCurrentUser || pr.IsAssignedToCurrentUser || pr.IsReviewRequestedForCurrentUser || pr.IsMentioned
                    || !repo.Equals(pr.RepoIdentifier)), detail.PullRequests).ToList();
            dashboard.Issues = MergeIssues(dashboard.Issues.Where(i => !repo.Equals(i.RepoIdentifier)), detail.Issues).ToList();
            dashboard.CiRuns = dashboard.CiRuns
                .Where(c => !repo.Equals(c.RepoIdentifier))
                .Concat(detail.CiRuns)
                .ToList();
            var warningKey = repo.ToString().ToLowerInvariant();
            if (string.IsNullOrEmpty(detail.ActivityWarning))
                dashboard.ActivityWarnings.Remove(warningKey);
            else
                dashboard.ActivityWarnings[warningKey] = detail.ActivityWarning;
        }

        internal static IEnumerable<DevHubPullRequest> MergePullRequests(IEnumerable<DevHubPullRequest> pullRequests,
            IEnumerable<DevHubPullRequest> repositoryPullRequests)
        {
            var seen = new Dictionary<string, DevHubPullRequest>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in repositoryPullRequests.Concat(pullRequests))
            {
                if (string.IsNullOrEmpty(pr.WebUrl))
                {
                    yield return pr;
                    continue;
                }
                if (seen.TryGetValue(pr.WebUrl, out var existing))
                {
                    existing.IsAuthoredByCurrentUser |= pr.IsAuthoredByCurrentUser;
                    existing.IsAssignedToCurrentUser |= pr.IsAssignedToCurrentUser;
                    existing.IsReviewRequestedForCurrentUser |= pr.IsReviewRequestedForCurrentUser;
                    existing.IsMentioned |= pr.IsMentioned;
                    existing.UnresolvedThreadCount = existing.UnresolvedThreadCount ?? pr.UnresolvedThreadCount;
                    existing.CanMerge = existing.CanMerge ?? pr.CanMerge;
                    existing.MergeBlockReason = existing.MergeBlockReason ?? pr.MergeBlockReason;
                    existing.ChecksSummary = existing.ChecksSummary ?? pr.ChecksSummary;
                    existing.ChecksUrl = existing.ChecksUrl ?? pr.ChecksUrl;
                    continue;
                }
                seen.Add(pr.WebUrl, pr);
                yield return pr;
            }
        }

        internal static IEnumerable<DevHubIssue> MergeIssues(IEnumerable<DevHubIssue> issues, IEnumerable<DevHubIssue> repositoryIssues)
        {
            // Project-level ADO results may include the same work item as linked repository activity.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var issue in repositoryIssues.Concat(issues))
            {
                if (string.IsNullOrEmpty(issue.WebUrl) || seen.Add(issue.WebUrl))
                    yield return issue;
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
                DevHubActivityRequest.Failed();
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
            lock (_scopeGate)
            {
                ClearScopedSources();
                _scopedContextRequestId = Interlocked.Increment(ref _scopeRequestId);
            }
        }

        internal int ScopedSourceCount
        {
            get
            {
                lock (_scopeGate)
                {
                    PruneScopedSources();
                    return _scopedSources.Count;
                }
            }
        }

        private void PruneScopedSources()
        {
            if (_scopedCredentialGeneration != _getCredentialGeneration() || _scopedQuery != _getQueryContext())
                ClearScopedSources();
            var cutoff = _utcNow().AddMinutes(-5);
            foreach (var key in _scopedSources.Where(pair => pair.Value.StoredAt <= cutoff).Select(pair => pair.Key).ToArray())
                _scopedSources.Remove(key);
        }

        private void ClearScopedSources()
        {
            _scopedSources.Clear();
            _scopedContext = null;
            _scopedIdentity = null;
            _scopedEpoch++;
        }

        private sealed class ScopedSource
        {
            internal ScopedSource(DevHubActivitySnapshot snapshot, DateTimeOffset storedAt)
            {
                Snapshot = snapshot;
                StoredAt = storedAt;
            }

            internal DevHubActivitySnapshot Snapshot { get; }
            internal DateTimeOffset StoredAt { get; }
        }
    }
}
