using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub
{
    /// <summary>
    /// In-memory summary cache. Keep one instance for the screen lifetime. Each caller can
    /// cancel its wait without canceling a shared fetch. Shared work honors shutdown,
    /// CancelPendingRequests (screen hide), and a 60-second deadline.
    /// InvalidateAll after authentication changes (including default-account changes).
    /// </summary>
    internal sealed class DevHubRepoSummaryService
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, CacheEntry> _requests = new Dictionary<string, CacheEntry>();
        private readonly HashSet<CacheEntry> _pending = new HashSet<CacheEntry>();
        private readonly SemaphoreSlim _concurrency;
        private readonly Func<RemoteRepoIdentifier, CancellationToken, Task<DevHubRepoSummary>> _fetch;
        private readonly Func<string> _accountScope;
        private readonly TimeSpan _cacheLifetime;
        private readonly CancellationToken _shutdownToken;

        public DevHubRepoSummaryService()
            : this(CancellationToken.None)
        {
        }

        public DevHubRepoSummaryService(CancellationToken shutdownToken)
            : this(FetchFromProviderAsync, () => $"{Options.Instance.DevHubGitHubAccount}/{Options.Instance.DevHubAdoAuthentication}", TimeSpan.FromMinutes(2), 4, shutdownToken)
        {
        }

        internal DevHubRepoSummaryService(Func<RemoteRepoIdentifier, CancellationToken, Task<DevHubRepoSummary>> fetch,
            Func<string> accountScope, TimeSpan cacheLifetime, int maximumConcurrency, CancellationToken shutdownToken = default)
        {
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            _accountScope = accountScope ?? throw new ArgumentNullException(nameof(accountScope));
            if (cacheLifetime < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
            _cacheLifetime = cacheLifetime;
            _shutdownToken = shutdownToken;
            _concurrency = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        }

        public async Task<DevHubRepoSummary> GetSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null)
                throw new ArgumentNullException(nameof(repo));
            cancellationToken.ThrowIfCancellationRequested();
            _shutdownToken.ThrowIfCancellationRequested();
            var key = GetKey(repo, _accountScope());
            CacheEntry request;
            lock (_gate)
            {
                _shutdownToken.ThrowIfCancellationRequested();
                // Bound retained entries as well as network concurrency. In-flight requests
                // remain deduplicated; completed entries can always be fetched again.
                foreach (var stale in _requests.Where(pair => IsExpired(pair.Value)).Select(pair => pair.Key).ToArray())
                    _requests.Remove(stale);
                if (_requests.Count >= 256)
                {
                    var completed = _requests.FirstOrDefault(pair => pair.Value.Task.IsCompleted);
                    if (completed.Key != null)
                        _requests.Remove(completed.Key);
                }
                if (!_requests.TryGetValue(key, out request))
                {
                    request = new CacheEntry(_shutdownToken);
                    var entry = request;
                    _pending.Add(entry);
                    request.Task = Task.Run(() => FetchEntryAsync(repo, entry));
                    _requests.Add(key, request);
                }
            }

            var task = request.Task;
            if (!task.IsCompleted && cancellationToken.CanBeCanceled)
            {
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => canceled.TrySetResult(true)))
                {
                    await Task.WhenAny(task, canceled.Task);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            var result = await task;
            cancellationToken.ThrowIfCancellationRequested();
            _shutdownToken.ThrowIfCancellationRequested();
            return result;
        }

        /// <summary>
        /// Invalidates all account variants of this repository, including in-flight cache
        /// entries. Existing waiters may finish, but old work cannot repopulate the cache.
        /// </summary>
        public void Invalidate(RemoteRepoIdentifier repo)
        {
            if (repo == null)
                throw new ArgumentNullException(nameof(repo));
            var prefix = GetKey(repo, null);
            lock (_gate)
            {
                foreach (var key in _requests.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                    _requests.Remove(key);
            }
        }

        /// <summary>
        /// Cancels pending shared work when the screen hides, without discarding completed
        /// cache entries. A later request starts fresh work. Also covers invalidated fetches.
        /// </summary>
        public void CancelPendingRequests()
        {
            var sources = new List<CancellationTokenSource>();
            lock (_gate)
            {
                foreach (var key in _requests.Where(pair => !pair.Value.Task.IsCompleted).Select(pair => pair.Key).ToArray())
                    _requests.Remove(key);
                foreach (var entry in _pending)
                {
                    if (entry.Cancellation != null)
                    {
                        sources.Add(entry.Cancellation);
                        entry.Cancellation = null;
                    }
                }
            }
            // Never run arbitrary cancellation callbacks under the cache lock. Ownership
            // is transferred above so fetch completion cannot dispose these concurrently.
            foreach (var source in sources)
            {
                using (source)
                    source.Cancel();
            }
        }

        public void InvalidateAll()
        {
            lock (_gate)
            {
                _requests.Clear();
            }
        }

        private bool IsExpired(CacheEntry request)
        {
            if (!request.Task.IsCompleted)
                return false;
            if (request.Result == null)
                return true;
            var result = request.Result;
            return (result.Status != DevHubRepoSummaryStatus.Ready && result.Status != DevHubRepoSummaryStatus.Unsupported)
                || DateTime.UtcNow - result.FetchedAt >= _cacheLifetime;
        }

        private sealed class CacheEntry
        {
            internal CacheEntry(CancellationToken shutdownToken)
            {
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                LifetimeToken = Cancellation.Token;
            }

            internal Task<DevHubRepoSummary> Task { get; set; }
            internal DevHubRepoSummary Result { get; set; }
            internal CancellationTokenSource Cancellation { get; set; }
            internal CancellationToken LifetimeToken { get; }
        }

        private async Task<DevHubRepoSummary> FetchEntryAsync(RemoteRepoIdentifier repo, CacheEntry entry)
        {
            try
            {
                var result = await FetchAsync(repo, entry.LifetimeToken);
                lock (_gate)
                {
                    entry.LifetimeToken.ThrowIfCancellationRequested();
                    entry.Result = result;
                }
                return result;
            }
            finally
            {
                CancellationTokenSource source;
                lock (_gate)
                {
                    _pending.Remove(entry);
                    if (entry.Result == null)
                    {
                        foreach (var key in _requests.Where(pair => ReferenceEquals(pair.Value, entry)).Select(pair => pair.Key).ToArray())
                            _requests.Remove(key);
                    }
                    source = entry.Cancellation;
                    entry.Cancellation = null;
                }
                source?.Dispose();
            }
        }

        private static string GetKey(RemoteRepoIdentifier repo, string account)
        {
            // Length prefixes avoid collisions and include ports / collection paths that
            // RemoteRepoIdentifier.Equals intentionally does not consider.
            var parts = new[] { repo.Host, repo.Owner, repo.Project, repo.Repo, repo.BaseUrl, repo.IsAzureDevOpsServer.ToString() };
            return string.Concat(parts.Select(part => $"{(part ?? string.Empty).Length}:{(part ?? string.Empty).ToLowerInvariant()}"))
                + "/" + (account ?? string.Empty).Trim().ToLowerInvariant();
        }

        private async Task<DevHubRepoSummary> FetchAsync(RemoteRepoIdentifier repo, CancellationToken lifetimeToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                var entered = false;
                try
                {
                    await _concurrency.WaitAsync(deadline.Token);
                    entered = true;
                    return await _fetch(repo, deadline.Token);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !lifetimeToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                    return new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unavailable,
                        statusMessage: "Repository summary is unavailable or timed out.");
                }
                finally
                {
                    if (entered)
                        _concurrency.Release();
                }
            }
        }

        private static Task<DevHubRepoSummary> FetchFromProviderAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            var providers = DevHubProviderRegistry.GetEnabledProviders();
            var provider = providers.FirstOrDefault(candidate =>
                candidate.ParseRemoteUrl($"https://{repo.Host}/{repo.DisplayName}") != null);
            if (repo.IsAzureDevOpsServer)
                provider = providers.FirstOrDefault(candidate => candidate is Providers.AzureDevOpsDevHubProvider);
            return provider != null ? provider.GetRepoSummaryAsync(repo, cancellationToken)
                : Task.FromResult(new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unsupported,
                    statusMessage: "This repository host does not support summaries."));
        }
    }
}
