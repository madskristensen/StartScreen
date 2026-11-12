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
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _getCachedCandidateRemoteUrls;
        private readonly Func<DevHubSortOrder> _getSortOrder;
        private readonly Func<string> _getQueryContext;
        private readonly Func<int> _getCredentialGeneration;
        internal const int ScopedSourceCapacity = 32;
        private readonly Dictionary<string, ScopedSource> _scopedSources = new Dictionary<string, ScopedSource>();
        private readonly Func<DateTimeOffset> _utcNow;
        private string _scopedContext;
        private string _scopedIdentity;
        private string _scopedProviderName;
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
            _getCandidateRemoteUrls = getCandidateRemoteUrls ?? (token => GetCandidateRemoteUrlsAsync(token));
            _getCachedCandidateRemoteUrls = getCandidateRemoteUrls ?? (token => GetCandidateRemoteUrlsAsync(token, useCachedMetadata: true));
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
            IProgress<DevHubDashboard> progress = null, IProgress<(int Completed, int Total)> workProgress = null,
            IProgress<string> stepProgress = null)
        {
            stepProgress?.Report("Reading cached activity...");
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
            var providers = _getProviders();
            using var requests = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var requestToken = requests.Token;
            var authentications = new Dictionary<IDevHubProvider, Task<DevHubActivityResult<IReadOnlyList<DevHubUser>>>>();
            try
            {
                // Providers without repository-based identity discovery can verify accounts in parallel with the local scan.
                foreach (var provider in providers.Where(provider => !(provider is IDevHubRefreshProvider)))
                    authentications[provider] = AuthenticateAsync(provider, requestToken, cachedUsers: cached.Users);

                stepProgress?.Report("Finding workspace repositories...");
                var candidates = await DevHubActivityRequest.RunAsync(() => _getCachedCandidateRemoteUrls(requestToken), urls => urls.Count > 0, requestToken);
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
                totalWork += providers.Count;
                workProgress?.Report((completedWork, totalWork));
                stepProgress?.Report("Verifying cached activity accounts...");
                foreach (var provider in providers.Where(provider => provider is IDevHubRefreshProvider))
                    authentications[provider] = AuthenticateAsync(provider, requestToken, refreshContext, cached.Users);
                var remaining = providers.Select(async provider =>
                    (Provider: provider, Result: await authentications[provider])).ToList();
                var restored = false;
                while (remaining.Count > 0)
                {
                    var completed = await Task.WhenAny(remaining).WithCancellation(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    remaining.Remove(completed);
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
                        dashboard.IsRefreshing = remaining.Count > 0;
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
                _cacheNeedsRefresh |= dashboard.CacheContext != cached.CacheContext
                    || dashboard.Users.Any(user => user.RestoredFromAccountBinding);
                return CurrentDashboard;
            }
            finally
            {
                requests.Cancel();
                try
                {
                    await Task.WhenAll(authentications.Values);
                }
                catch (OperationCanceledException ex) when (requestToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
            }
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
        /// A forced refresh waits for an active refresh and fetches fresh activity, reusing current account verification.
        /// </summary>
        public async Task<DevHubDashboard> RefreshAsync(CancellationToken cancellationToken, IProgress<DevHubDashboard> progress = null,
            bool forceRefresh = false, DevHubActivityOptions options = null, IProgress<(int Completed, int Total)> workProgress = null,
            IProgress<string> stepProgress = null, bool forceAuthentication = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options = options ?? new DevHubActivityOptions();

            stepProgress?.Report("Waiting for the activity refresh...");
            if (!await _refreshGate.WaitAsync(forceRefresh || forceAuthentication ? Timeout.Infinite : 0, cancellationToken))
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
                stepProgress?.Report("Finding workspace repositories...");
                var candidates = await DevHubActivityRequest.RunAsync(() => _getCandidateRemoteUrls(cancellationToken),
                    urls => urls.Count > 0, cancellationToken);
                workProgress?.Report((++completedWork, totalWork));
                var refreshContext = new DevHubRefreshContext(candidates.Value, candidates.Status, forceAuthentication);
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

                var providers = _getProviders();
                totalWork += providers.Count;
                workProgress?.Report((completedWork, totalWork));
                var authenticated = new List<IDevHubProvider>();
                var candidateUrls = refreshContext.RemoteUrls;
                if (candidates.Status != DevHubActivityStatus.Success)
                {
                    var message = !string.IsNullOrEmpty(candidates.Message) ? candidates.Message : "Repository discovery is unavailable. Refresh to retry.";
                    dashboard.Sources["repositories/discovery"] = new DevHubActivitySnapshot { Status = candidates.Status, Message = message };
                    dashboard.ActivityWarnings["repositories/discovery"] = message;
                }
                if (candidates.Status != DevHubActivityStatus.Success || previousDashboard == null
                    || previousDashboard.CredentialGeneration != authenticationGeneration || previousDashboard.QueryContext != queryContext
                    || previousDashboard.CacheContext != CreateContext(previousDashboard.Users, options, candidateUrls))
                    previousDashboard = null;
                var generation = authenticationGeneration;

                if (previousDashboard != null)
                {
                    foreach (var provider in providers)
                    {
                        if (!(provider is IDevHubCachedAuthenticationProvider cachedProvider))
                            continue;
                        var previousUsers = previousDashboard.Users.Where(user => user.ProviderName == provider.DisplayName).ToArray();
                        if (previousUsers.Length == 0)
                            continue;
                        var restored = await DevHubActivityRequest.RunAsync(
                            () => cachedProvider.GetCachedAuthenticatedUsersAsync(previousUsers, refreshContext, cancellationToken),
                            values => values.Count > 0, cancellationToken);
                        if (restored.Status != DevHubActivityStatus.Success || restored.Value == null || restored.Value.Count == 0)
                            continue;
                        dashboard.Users.AddRange(restored.Value);
                        foreach (var pair in previousDashboard.Sources.Where(pair => pair.Key.StartsWith(provider.DisplayName + "/", StringComparison.Ordinal)))
                            dashboard.Sources[pair.Key] = pair.Value;
                    }
                }

                using var requests = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var requestToken = requests.Token;
                stepProgress?.Report("Checking connected provider accounts...");
                var authentications = providers.Select(async provider =>
                    (Provider: provider, Result: await AuthenticateAsync(provider, requestToken, refreshContext))).ToList();
                var remaining = new List<Task<(string Key, DevHubActivitySnapshot Snapshot)>>();
                var repositoryTasks = new HashSet<Task<(string Key, DevHubActivitySnapshot Snapshot)>>();
                var repositoryQueue = new Queue<(IDevHubProvider Provider, RemoteRepoIdentifier Repo)>();
                var steps = new Dictionary<Task<(string Key, DevHubActivitySnapshot Snapshot)>, string>();
                var publications = new DashboardPublicationWindow();
                var inventories = new AsyncQueue<(string Key, DevHubActivitySnapshot Snapshot)>();
                var inventoryUpdate = inventories.DequeueAsync(requestToken);
                var completedSources = new HashSet<string>(StringComparer.Ordinal);
                Task publicationDelay = null;
                void PublishUpdate(bool force = false)
                {
                    if (publications.ShouldPublish(force))
                    {
                        Publish(dashboard, progress);
                        publicationDelay = null;
                    }
                    else if (publicationDelay == null)
                        publicationDelay = Task.Delay(publications.Delay, requestToken);
                }
                void AddSource(string key, string step, Func<Task<DevHubActivitySnapshot>> fetch, bool repository = false)
                {
                    var task = FetchSourceAsync(key, fetch);
                    remaining.Add(task);
                    steps[task] = step;
                    if (repository)
                        repositoryTasks.Add(task);
                }

                void AddActivitySource(IDevHubProvider provider, DevHubActivityKind kind, DevHubActivityOptions activityOptions,
                    RemoteRepoIdentifier repo, string key, string step, bool repository = false)
                {
                    var progressiveOptions = new DevHubActivityOptions(activityOptions.MaxItems, activityOptions.IncludeCompleted,
                        activityOptions.RefreshContext, snapshot =>
                        {
                            if (requestToken.IsCancellationRequested)
                                return;
                            // A late preview can race with refresh completion.
                            inventories.TryEnqueue((key, snapshot));
                        });
                    AddSource(key, step, () => FetchActivityAsync(provider, kind, progressiveOptions, repo, requestToken), repository);
                }

                void AddProviderSources(IDevHubProvider provider)
                {
                    AddActivitySource(provider, DevHubActivityKind.PullRequests, options, null,
                        provider.DisplayName + "/pulls", $"Loading {provider.DisplayName} pull requests...");
                    AddActivitySource(provider, DevHubActivityKind.Issues, options, null,
                        provider.DisplayName + "/issues", $"Loading {provider.DisplayName} issues...");
                    AddActivitySource(provider, DevHubActivityKind.CiRuns, options, null,
                        provider.DisplayName + "/builds", $"Loading {provider.DisplayName} builds...");
                    AddSource(provider.DisplayName + "/discovery", $"Finding {provider.DisplayName} repositories...", async () =>
                    {
                        var result = await DevHubActivityRequest.RunAsync(() => provider.GetActivityReposAsync(candidateUrls, requestToken),
                            items => items.Count > 0, requestToken);
                        return new DevHubActivitySnapshot { Status = result.Status, Message = result.Message,
                            ActivityRepositories = result.Value?.ToList() ?? new List<RemoteRepoIdentifier>() };
                    });
                }

                try
                {
                    while (authentications.Count > 0 || remaining.Count > 0 || repositoryQueue.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        while (repositoryQueue.Count > 0 && repositoryTasks.Count < DashboardRepositoryConcurrency)
                        {
                            var (provider, repo) = repositoryQueue.Dequeue();
                            var repoOptions = new DevHubActivityOptions(options.MaxItems == DevHubActivityOptions.DefaultLimit
                                ? _dashboardRepoItemCount : options.MaxItems, options.IncludeCompleted, refreshContext);
                            AddActivitySource(provider, DevHubActivityKind.Repository, repoOptions, repo,
                                provider.DisplayName + "/repo/" + repo.ToString().ToLowerInvariant(),
                                $"Loading {provider.DisplayName} activity for {repo.DisplayName}...",
                                repository: true);
                        }

                        stepProgress?.Report(remaining.Count > 0 ? steps[remaining[0]] : "Checking connected provider accounts...");
                        var pendingTasks = authentications.Cast<Task>().Concat(remaining).Append(inventoryUpdate);
                        if (publicationDelay != null)
                            pendingTasks = pendingTasks.Append(publicationDelay);
                        var completedTask = await Task.WhenAny(pendingTasks).WithCancellation(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (generation != _getCredentialGeneration() || queryContext != _getQueryContext())
                            return InvalidateContext(progress);
                        if (ReferenceEquals(completedTask, inventoryUpdate))
                        {
                            var (inventoryKey, inventorySnapshot) = await inventoryUpdate;
                            inventoryUpdate = inventories.DequeueAsync(requestToken);
                            if (!completedSources.Contains(inventoryKey))
                            {
                                dashboard.Sources.TryGetValue(inventoryKey, out var previousInventory);
                                if (inventorySnapshot.RepoIdentifier != null && previousInventory != null)
                                {
                                    inventorySnapshot.Issues = previousInventory.Issues;
                                    inventorySnapshot.CiRuns = previousInventory.CiRuns;
                                }
                                dashboard.Sources[inventoryKey] = RetainFailedSource(inventorySnapshot, previousInventory);
                                PublishUpdate();
                            }
                            continue;
                        }
                        if (ReferenceEquals(completedTask, publicationDelay))
                        {
                            PublishUpdate(true);
                            continue;
                        }
                        var authentication = authentications.FirstOrDefault(task => ReferenceEquals(task, completedTask));
                        if (authentication != null)
                        {
                            authentications.Remove(authentication);
                            var (provider, result) = await authentication;
                            var users = result.Value ?? Array.Empty<DevHubUser>();
                            var previousUsers = previousDashboard?.Users.Where(user => user.ProviderName == provider.DisplayName).ToList();
                            var retainedIdentity = false;
                            if (result.Status == DevHubActivityStatus.Unavailable && users.Count == 0 && previousUsers?.Count > 0
                                && provider is IDevHubCachedAuthenticationProvider cachedProvider)
                            {
                                var restored = await DevHubActivityRequest.RunAsync(
                                    () => cachedProvider.GetCachedAuthenticatedUsersAsync(previousUsers, refreshContext, requestToken),
                                    values => values.Count > 0, requestToken);
                                if (restored.Status == DevHubActivityStatus.Success && restored.Value?.Count > 0)
                                {
                                    users = restored.Value;
                                    retainedIdentity = true;
                                }
                            }
                            if (generation != _getCredentialGeneration() || queryContext != _getQueryContext())
                                return InvalidateContext(progress);
                            var prefix = provider.DisplayName + "/";
                            dashboard.Users.RemoveAll(user => user.ProviderName == provider.DisplayName);
                            foreach (var cachedKey in dashboard.Sources.Keys.Where(sourceKey => sourceKey.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                                dashboard.Sources.Remove(cachedKey);
                            dashboard.Users.AddRange(users);
                            if (result.Status != DevHubActivityStatus.Success)
                            {
                                var authenticationKey = provider.DisplayName + "/authentication";
                                var message = !string.IsNullOrEmpty(result.Message) ? result.Message
                                    : $"{provider.DisplayName}: authentication {result.Status.ToString().ToLowerInvariant()}. Activity cannot be verified. Sign in or refresh to retry.";
                                dashboard.Sources[authenticationKey] = new DevHubActivitySnapshot { Status = result.Status, Message = message };
                            }
                            if (users.Count > 0)
                            {
                                if (previousUsers != null && CreateIdentityContext(users) == CreateIdentityContext(previousUsers))
                                {
                                    foreach (var pair in previousDashboard.Sources.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                                        && !pair.Key.EndsWith("/authentication", StringComparison.Ordinal)))
                                        dashboard.Sources[pair.Key] = retainedIdentity
                                            ? RetainFailedSource(new DevHubActivitySnapshot { Status = result.Status, Message = result.Message,
                                                RepoIdentifier = pair.Value.RepoIdentifier }, pair.Value) : pair.Value;
                                }
                                if (!retainedIdentity)
                                {
                                    authenticated.Add(provider);
                                    totalWork += 4;
                                    AddProviderSources(provider);
                                }
                            }
                            dashboard.IdentityContext = CreateIdentityContext(dashboard.Users);
                            dashboard.CacheContext = candidates.Status == DevHubActivityStatus.Success
                                ? CreateContext(dashboard.Users, options, candidateUrls) : null;
                            workProgress?.Report((++completedWork, totalWork));
                            PublishUpdate(force: true);
                            continue;
                        }
                        var completed = remaining.First(task => ReferenceEquals(task, completedTask));
                        remaining.Remove(completed);
                        repositoryTasks.Remove(completed);
                        steps.Remove(completed);
                        var (key, snapshot) = await completed;
                        completedSources.Add(key);
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
                        PublishUpdate();
                    }
                }
                finally
                {
                    requests.Cancel();
                    inventories.Complete();
                    try
                    {
                        await Task.WhenAll(authentications.Cast<Task>().Concat(remaining));
                    }
                    catch (Exception ex)
                    {
                        await ex.LogAsync();
                    }
                }

                // Cache the final result
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _getCredentialGeneration() || queryContext != _getQueryContext())
                    return InvalidateContext(progress);
                dashboard.IsRefreshing = false;
                dashboard.IdentityContext = CreateIdentityContext(dashboard.Users);
                dashboard.CacheContext = candidates.Status == DevHubActivityStatus.Success
                    ? CreateContext(dashboard.Users, options, candidateUrls) : null;
                dashboard.FetchedAt = DateTime.UtcNow;
                var final = Publish(dashboard, progress);
                stepProgress?.Report("Saving activity cache...");
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

        internal static bool HasScopedIdentityChanged(DevHubDashboard dashboard, DevHubDashboard scoped)
        {
            if (string.IsNullOrEmpty(dashboard?.IdentityContext))
                return false;
            return scoped.Users.GroupBy(user => user.ProviderName).Any(group =>
            {
                var users = dashboard.Users.Where(user => user.ProviderName == group.Key).ToList();
                return (users.Count > 0 || !dashboard.IsRefreshing)
                    && CreateIdentityContext(users) != CreateIdentityContext(group);
            });
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
                if (_scopedIdentity != null && !string.IsNullOrEmpty(dashboard?.IdentityContext))
                {
                    var users = dashboard.Users.Where(user => user.ProviderName == _scopedProviderName).ToList();
                    if ((users.Count > 0 || !dashboard.IsRefreshing) && _scopedIdentity != CreateIdentityContext(users))
                        ClearScopedSources();
                }
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

        private static async Task<DevHubActivityResult<IReadOnlyList<DevHubUser>>> AuthenticateAsync(IDevHubProvider provider,
            CancellationToken cancellationToken, DevHubRefreshContext context = null, IReadOnlyList<DevHubUser> cachedUsers = null)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            return await DevHubActivityRequest.RunAsync(async () =>
            {
                if (cachedUsers != null && provider is IDevHubCachedAuthenticationProvider cachedProvider)
                {
                    var previous = cachedUsers.Where(user => user.ProviderName == provider.DisplayName).ToArray();
                    if (previous.Length > 0)
                    {
                        var restored = await cachedProvider.GetCachedAuthenticatedUsersAsync(previous, context, cancellationToken);
                        if (restored != null)
                            return restored;
                    }
                }
                var users = context != null && provider is IDevHubRefreshProvider refreshProvider
                    ? await refreshProvider.GetAuthenticatedUsersAsync(context, cancellationToken)
                    : provider is IDevHubAuthenticationProvider authenticationProvider
                        ? await authenticationProvider.GetAuthenticatedUsersAsync(context?.ForceRefresh == true, cancellationToken)
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
        /// overwriting each other's results. Successful matching views are reused for five
        /// minutes unless forced; authentication still checks the current credential.
        /// </summary>
        public async Task<DevHubDashboard> ScopeToRepoAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken,
            DevHubActivityOptions options = null, bool forceRefresh = false, IProgress<DevHubDashboard> progress = null)
        {
            if (repo == null)
                return CurrentDashboard;

            options = options ?? new DevHubActivityOptions(ScopedRepoItemCount);
            var requestId = Interlocked.Increment(ref _scopeRequestId);
            var authenticationGeneration = _getCredentialGeneration();
            var queryContext = _getQueryContext();
            var refreshContext = new DevHubRefreshContext(new[] { GetLookupUrl(repo) });
            var provider = _getProviders().FirstOrDefault(candidate => candidate.CanHandle(GetLookupUrl(repo)));
            var authentication = provider == null ? null : await AuthenticateAsync(provider, cancellationToken, refreshContext);
            cancellationToken.ThrowIfCancellationRequested();
            if (authenticationGeneration != _getCredentialGeneration() || queryContext != _getQueryContext())
                return InvalidateContext();
            var users = (authentication?.Value ?? Array.Empty<DevHubUser>()).ToList();
            var context = CreateScopedContext(users, options, new[] { GetLookupUrl(repo) });
            var generation = _getCredentialGeneration();
            var verified = provider != null && users.Any(user => string.IsNullOrEmpty(user.Host)
                || string.Equals(user.Host, repo.Host, StringComparison.OrdinalIgnoreCase));
            ScopedSource cached = null;
            long scopeEpoch;
            lock (_scopeGate)
            {
                var scopeContext = generation + ":" + CreateScopedContext(users, options, Array.Empty<string>());
                PruneScopedSources();
                if (_scopedContext != scopeContext && requestId >= _scopedContextRequestId)
                {
                    ClearScopedSources();
                    _scopedContext = scopeContext;
                    _scopedIdentity = CreateIdentityContext(users);
                    _scopedProviderName = provider?.DisplayName;
                    _scopedQuery = queryContext;
                    _scopedCredentialGeneration = generation;
                }
                scopeEpoch = _scopedContext == scopeContext ? _scopedEpoch : -1;
                _scopedContextRequestId = Math.Max(_scopedContextRequestId, requestId);
                if (verified && !forceRefresh && authentication.Status == DevHubActivityStatus.Success
                    && scopeEpoch == _scopedEpoch && _scopedSources.TryGetValue(context, out var source) && !source.Snapshot.IsStale)
                    cached = source;
            }
            var dashboard = new DevHubDashboard
            {
                Users = users,
                IdentityContext = CreateIdentityContext(users),
                CredentialGeneration = authenticationGeneration,
                QueryContext = queryContext,
                FetchedAt = cached?.StoredAt.UtcDateTime ?? _utcNow().UtcDateTime,
                ActivityLimit = options.MaxItems,
                IncludesCompleted = options.IncludeCompleted,
                CacheContext = context,
            };
            var inventoryFinished = 0;
            var progressiveOptions = progress == null ? options : new DevHubActivityOptions(options.MaxItems, options.IncludeCompleted, options.RefreshContext, inventory =>
            {
                if (progress == null || Volatile.Read(ref inventoryFinished) != 0 || cancellationToken.IsCancellationRequested
                    || requestId != Volatile.Read(ref _scopeRequestId) || generation != _getCredentialGeneration()
                    || queryContext != _getQueryContext())
                    return;
                var preview = new DevHubDashboard
                {
                    Users = users.ToList(),
                    IdentityContext = dashboard.IdentityContext,
                    CredentialGeneration = authenticationGeneration,
                    QueryContext = queryContext,
                    FetchedAt = dashboard.FetchedAt,
                    ActivityLimit = options.MaxItems,
                    IncludesCompleted = options.IncludeCompleted,
                    CacheContext = context,
                    IsRefreshing = true,
                    PullRequests = inventory.PullRequests,
                    Issues = inventory.Issues,
                    CiRuns = inventory.CiRuns,
                };
                preview.Sources[repo.ToString().ToLowerInvariant()] = inventory;
                if (!string.IsNullOrEmpty(inventory.Message))
                    preview.ActivityWarnings[repo.ToString().ToLowerInvariant()] = inventory.Message;
                progress.Report(preview);
            });
            DevHubActivitySnapshot snapshot;
            try
            {
                snapshot = cached?.Snapshot ?? (verified ? await FetchActivityAsync(provider, DevHubActivityKind.Repository, progressiveOptions, repo, cancellationToken)
                    : new DevHubActivitySnapshot
                    {
                        Status = provider == null ? DevHubActivityStatus.Unavailable
                            : authentication.Status == DevHubActivityStatus.Success || authentication.Status == DevHubActivityStatus.Partial
                                ? DevHubActivityStatus.Unauthorized : authentication.Status,
                        RepoIdentifier = repo,
                    });
            }
            finally
            {
                Volatile.Write(ref inventoryFinished, 1);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _getCredentialGeneration() || queryContext != _getQueryContext()
                || context != CreateScopedContext(users, options, new[] { GetLookupUrl(repo) }))
                return InvalidateContext();
            lock (_scopeGate)
            {
                PruneScopedSources();
                if (verified && cached == null && scopeEpoch == _scopedEpoch)
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

        private string CreateScopedContext(IEnumerable<DevHubUser> users, DevHubActivityOptions options, IEnumerable<string> urls)
        {
            return HashContext(CreateContext(users, options, urls) + JsonSerializer.Serialize(users.Select(user => user.CredentialContext)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray()));
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
                var key = GetActivityItemKey(pr.WebUrl, pr.RepoIdentifier, pr.NumericId, false);
                if (string.IsNullOrEmpty(key))
                {
                    yield return pr;
                    continue;
                }
                if (seen.TryGetValue(key, out var existing))
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
                seen.Add(key, pr);
                yield return pr;
            }
        }

        internal static IEnumerable<DevHubIssue> MergeIssues(IEnumerable<DevHubIssue> issues, IEnumerable<DevHubIssue> repositoryIssues)
        {
            // Project-level ADO results may include the same work item as linked repository activity.
            var seen = new Dictionary<string, DevHubIssue>(StringComparer.OrdinalIgnoreCase);
            foreach (var issue in repositoryIssues.Concat(issues))
            {
                var key = GetActivityItemKey(issue.WebUrl, issue.RepoIdentifier, issue.NumericId, true);
                if (string.IsNullOrEmpty(key))
                {
                    yield return issue;
                    continue;
                }
                if (seen.TryGetValue(key, out var existing))
                {
                    existing.IsAuthoredByCurrentUser |= issue.IsAuthoredByCurrentUser;
                    existing.IsAssignedToCurrentUser |= issue.IsAssignedToCurrentUser;
                    existing.IsMentioned |= issue.IsMentioned;
                    continue;
                }
                seen.Add(key, issue);
                yield return issue;
            }
        }

        private static string GetActivityItemKey(string webUrl, RemoteRepoIdentifier repo, int id, bool workItem)
        {
            if (string.IsNullOrEmpty(webUrl) || repo == null || id <= 0)
                return webUrl;
            if (!repo.IsAzureDevOpsServer && !string.Equals(repo.Host, AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                return webUrl;
            var collection = repo.IsAzureDevOpsServer ? repo.BaseUrl?.TrimEnd('/') : $"https://dev.azure.com/{repo.Owner}";
            if (string.IsNullOrEmpty(collection))
                return webUrl;
            // ADO work item IDs are collection-wide; PRs retain repository scope.
            return workItem ? $"{collection}/workitem/{id}" : $"{collection}/{repo.Project}/_git/{repo.Repo}/pullrequest/{id}";
        }

        /// <summary>
        /// Reads distinct git remote URLs from the MRU list to use as the candidate set of
        /// repositories for per-repo activity fetching.
        /// </summary>
        private static async Task<IReadOnlyList<string>> GetCandidateRemoteUrlsAsync(CancellationToken cancellationToken,
            bool useCachedMetadata = false)
        {
            try
            {
                return await MruService.GetRepositoryRemoteUrlsAsync(cancellationToken, useCachedMetadata);
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
            var now = _utcNow();
            var cutoff = now.AddMinutes(-5);
            foreach (var key in _scopedSources.Where(pair => pair.Value.StoredAt <= cutoff || pair.Value.StoredAt > now).Select(pair => pair.Key).ToArray())
                _scopedSources.Remove(key);
        }

        private void ClearScopedSources()
        {
            _scopedSources.Clear();
            _scopedContext = null;
            _scopedIdentity = null;
            _scopedProviderName = null;
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
