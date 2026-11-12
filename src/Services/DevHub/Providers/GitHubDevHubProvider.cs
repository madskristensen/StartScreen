using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Helpers;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub.Providers
{
    /// <summary>
    /// GitHub provider for the Dev Hub.
    /// Fetches PRs, issues, and CI status from the GitHub REST API.
    /// </summary>
    internal sealed class GitHubDevHubProvider : IDevHubProvider, IDevHubActivityProvider, IDevHubActivitySource, IDevHubAuthenticationProvider, IDevHubCachedAuthenticationProvider
    {
        // Shared HttpClient: reusing the connection pool across requests avoids repeated DNS,
        // TCP, and TLS handshakes, which are a significant chunk of the Dev Hub cold-start cost.
        // The Authorization header is per-request because the credential token can change at runtime
        // and DefaultRequestHeaders is not safe to mutate while parallel requests are in flight.
        private static readonly HttpClient _httpClient = CreateSharedHttpClient();
        internal const int MaximumSearchConcurrency = 3;
        private static readonly ConditionalWeakTable<HttpClient, SemaphoreSlim> _searchConcurrency = new();

        private static HttpClient CreateSharedHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            client.DefaultRequestHeaders.Add("User-Agent", "StartScreen-VS-Extension");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        private static async Task<HttpResponseMessage> SendGetAsync(DevHubCredential credential, string url, CancellationToken cancellationToken, HttpClient httpClient = null, bool observe = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.Token}");
            var client = httpClient ?? _httpClient;
            var gate = url.StartsWith("https://api.github.com/search/issues?", StringComparison.Ordinal)
                ? _searchConcurrency.GetValue(client, _ => new SemaphoreSlim(MaximumSearchConcurrency, MaximumSearchConcurrency)) : null;
            var response = await DevHubReadRequestSharing.Instance.SendAsync(client, request, credential, cancellationToken, readGate: gate);
            return observe ? DevHubActivityRequest.Observe(response) : response;
        }

        private readonly DevHubIdentityCache _identityCache = new DevHubIdentityCache();
        private readonly DevHubCredentialCache<DevHubUser> _users = new DevHubCredentialCache<DevHubUser>(TimeSpan.FromMinutes(5));
        private readonly HttpClient _authenticationClient;
        private readonly Func<CancellationToken, Task<DevHubCredential>> _acquireCredential;
        private readonly DevHubAccountBinding _accountBinding;

        internal GitHubDevHubProvider(HttpClient authenticationClient = null,
            Func<CancellationToken, Task<DevHubCredential>> acquireCredential = null, DevHubAccountBinding accountBinding = null)
        {
            _authenticationClient = authenticationClient ?? _httpClient;
            _acquireCredential = acquireCredential ?? (token => DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), token));
            _accountBinding = accountBinding ?? (authenticationClient == null ? DevHubAccountBinding.Default : null);
        }

        private static string GetPreferredGitHubAccount()
        {
            var account = Options.Instance.DevHubGitHubAccount;
            return string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        }

        public string DisplayName => "GitHub";

        public Task<DevHubActivitySnapshot> FetchActivityAsync(DevHubActivityKind kind, DevHubActivityOptions options,
            RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return DevHubActivityRequest.FetchAsync(this, kind, options, repo, cancellationToken);
        }

        public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return DevHubRepoSummaryFetcher.FetchAsync(repo,
                token => DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), token),
                (credential, url, token) => SendGetAsync(credential, url, token), true, cancellationToken, identityCache: _identityCache,
                cacheScope: DevHubMetadataCache.GetClientScope(_httpClient));
        }

        internal Task<DevHubRepoSummary> GetRepoSummaryAsync(HttpClient client, DevHubCredential credential,
            RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return DevHubRepoSummaryFetcher.FetchAsync(repo, _ => Task.FromResult(credential),
                (auth, url, token) => SendGetAsync(auth, url, token, client), true, cancellationToken, identityCache: _identityCache,
                cacheScope: DevHubMetadataCache.GetClientScope(client));
        }

        public bool CanHandle(string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return false;

            return remoteUrl.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl)
        {
            var parsed = RemoteRepoIdentifier.TryParse(remoteUrl);
            if (parsed != null && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return parsed;

            return null;
        }

        public async Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken)
        {
            var credential = await _acquireCredential(cancellationToken);
            return await GetAuthenticatedUserAsync(_authenticationClient, credential, cancellationToken, _acquireCredential);
        }

        internal async Task<DevHubUser> GetAuthenticatedUserAsync(HttpClient httpClient, DevHubCredential credential,
            CancellationToken cancellationToken, Func<CancellationToken, Task<DevHubCredential>> reacquireCredential = null,
            bool forceRefresh = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (credential == null)
                return null;

            try
            {
                var generation = _users.Generation;
                if (!_users.IsCurrent(credential, generation))
                {
                    DevHubActivityRequest.Failed(unauthorized: true);
                    return null;
                }
                var resource = DevHubMetadataCache.GetClientScope(httpClient) + "|https://api.github.com/user";
                if (!forceRefresh && _users.TryGetValue(credential, resource, out var cached))
                    return cached;
                if (forceRefresh)
                    _users.Remove(credential, resource);
                var response = await SendGetAsync(credential, "https://api.github.com/user", cancellationToken, httpClient, observe: false);

                // Token expired - invalidate cache and retry once with a fresh credential.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    _accountBinding?.Invalidate();
                    DevHubCredentialHelper.InvalidateCachedCredential("github.com");
                    credential = reacquireCredential != null ? await reacquireCredential(cancellationToken)
                        : await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
                    if (credential == null)
                    {
                        DevHubActivityRequest.Failed(unauthorized: true);
                        return null;
                    }

                    generation = _users.Generation;
                    response = await SendGetAsync(credential, "https://api.github.com/user", cancellationToken, httpClient);
                }

                using var finalResponse = response;
                DevHubActivityRequest.Observe(response);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await HttpResponseReader.ReadStringAsync(response, cancellationToken);
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var login = root.GetProperty("login").GetString();
                    var user = new DevHubUser
                    {
                        Username = login,
                        IdentityId = root.TryGetProperty("id", out var id) ? id.ToString() : login,
                        DisplayName = root.TryGetProperty("name", out var name) && name.ValueKind != System.Text.Json.JsonValueKind.Null
                            ? name.GetString()
                            : login,
                        AvatarUrl = root.TryGetProperty("avatar_url", out var avatar)
                            ? avatar.GetString()
                            : null,
                        ProviderName = DisplayName,
                        Host = "github.com",
                    };
                    cancellationToken.ThrowIfCancellationRequested();
                    user.CredentialContext = DevHubMetadataCache.GetKey(credential, resource);
                    if (ReferenceEquals(httpClient, _authenticationClient))
                        _accountBinding?.Remember(credential, "https://api.github.com/user", user);
                    if (!_users.Store(credential, resource, user, generation))
                    {
                        DevHubActivityRequest.Failed(unauthorized: true);
                        return null;
                    }
                    _identityCache.Remember(credential, "https://api.github.com/user", login);
                    return user;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return null;
            }
        }

        public async Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
        {
            var user = await GetAuthenticatedUserAsync(cancellationToken);
            return user != null ? new[] { user } : Array.Empty<DevHubUser>();
        }

        public async Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            var credential = await _acquireCredential(cancellationToken);
            var user = await GetAuthenticatedUserAsync(_authenticationClient, credential, cancellationToken,
                reacquireCredential: _acquireCredential, forceRefresh: forceRefresh);
            return user != null ? new[] { user } : Array.Empty<DevHubUser>();
        }

        public async Task<IReadOnlyList<DevHubUser>> GetCachedAuthenticatedUsersAsync(IReadOnlyList<DevHubUser> users,
            DevHubRefreshContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_accountBinding == null || users.Count != 1 || string.IsNullOrEmpty(users[0].AccountBinding))
                return null;
            var generation = _users.Generation;
            var credential = await _acquireCredential(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (credential == null)
                return Array.Empty<DevHubUser>();
            const string resource = "https://api.github.com/user";
            var user = users[0];
            if (!_users.IsCurrent(credential, generation) || !_accountBinding.Matches(credential, resource, user))
                return null;
            user.CredentialContext = DevHubMetadataCache.GetKey(credential, DevHubMetadataCache.GetClientScope(_authenticationClient) + "|" + resource);
            user.RestoredFromAccountBinding = true;
            return users;
        }

        /// <summary>
        /// Gets the cached GitHub login, or fetches it from the API if not yet cached.
        /// </summary>
        private async Task<(DevHubCredential credential, string login)> GetCredentialAndLoginAsync(CancellationToken cancellationToken)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
            if (credential == null)
            {
                DevHubActivityRequest.Failed(unauthorized: true);
                return (null, null);
            }
            var login = await _identityCache.GetAsync(credential, "https://api.github.com/user",
                (auth, url, token) => SendGetAsync(auth, url, token), true, cancellationToken);
            if (string.IsNullOrEmpty(login))
                DevHubActivityRequest.Failed();
            return (credential, login);
        }

        public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken)
        {
            return GetUserPullRequestsAsync(new DevHubActivityOptions(), cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            try
            {
                var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
                if (credential == null || string.IsNullOrEmpty(login))
                    return Array.Empty<DevHubPullRequest>();

                return await GetUserPullRequestsAsync(_httpClient, credential, login, options, cancellationToken,
                    customQuery: Options.Instance.DevHubSearchQuery);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return Array.Empty<DevHubPullRequest>();
            }
        }

        public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            return GetUserIssuesAsync(new DevHubActivityOptions(), cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            try
            {
                var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
                if (credential == null || string.IsNullOrEmpty(login))
                    return Array.Empty<DevHubIssue>();

                return await GetUserIssuesAsync(_httpClient, credential, login, options, cancellationToken,
                    customQuery: Options.Instance.DevHubSearchQuery);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return Array.Empty<DevHubIssue>();
            }
        }

        public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken)
        {
            return GetUserCiRunsAsync(new DevHubActivityOptions(), cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            try
            {
                var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
                if (credential == null || string.IsNullOrEmpty(login))
                    return Array.Empty<DevHubCiRun>();

                return await GetUserCiRunsAsync(_httpClient, credential, login, options, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return Array.Empty<DevHubCiRun>();
            }
        }

        internal async Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(
            HttpClient httpClient, DevHubCredential credential, CancellationToken cancellationToken)
        {
            const string reposUrl = "https://api.github.com/user/repos?sort=pushed&per_page=5&affiliation=owner,collaborator,organization_member";
            using var reposResponse = await SendGetAsync(credential, reposUrl, cancellationToken, httpClient);
            if (!reposResponse.IsSuccessStatusCode)
                return Array.Empty<DevHubCiRun>();

            var repos = ParseRepoList(await HttpResponseReader.ReadStringAsync(reposResponse, cancellationToken));
            using var semaphore = new SemaphoreSlim(3, 3);
            var results = await Task.WhenAll(repos.Select(async repo =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var runsUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/actions/runs?per_page=3";
                    using var response = await SendGetAsync(credential, runsUrl, cancellationToken, httpClient);
                    return response.IsSuccessStatusCode
                        ? ParseCiRuns(await HttpResponseReader.ReadStringAsync(response, cancellationToken), repo)
                        : new List<DevHubCiRun>();
                }
                finally
                {
                    semaphore.Release();
                }
            }));
            cancellationToken.ThrowIfCancellationRequested();
            return results.SelectMany(runs => runs).OrderByDescending(run => run.Timestamp).ToList();
        }

        private static int NormalizeMaxItems(int maxItems) => maxItems < 1 ? 1 : (maxItems > 100 ? 100 : maxItems);

        private static List<RemoteRepoIdentifier> ParseRepoList(string json)
        {
            var repos = new List<RemoteRepoIdentifier>();
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var fullName = item.GetProperty("full_name").GetString();
                    var parts = fullName?.Split('/');
                    if (parts?.Length == 2)
                    {
                        repos.Add(new RemoteRepoIdentifier("github.com", parts[0], parts[1]));
                    }
                }
            }
            return repos;
        }

        public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> candidateRemoteUrls, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repos = (candidateRemoteUrls ?? Array.Empty<string>())
                .Select(ParseRemoteUrl).Where(repo => repo != null).Distinct().ToArray();
            return Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>(repos);
        }

        public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken)
        {
            return GetRepoDetailAsync(repo, new DevHubActivityOptions(Math.Max(1, Math.Min(DevHubActivityOptions.MaximumLimit, maxItems))), cancellationToken);
        }

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (repo == null)
                return null;
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
            if (credential == null)
            {
                DevHubActivityRequest.Failed(unauthorized: true);
                return null;
            }
            var login = await _identityCache.GetAsync(credential, "https://api.github.com/user",
                (auth, url, token) => SendGetAsync(auth, url, token), true, cancellationToken);
            return await GetRepoDetailAsync(_httpClient, credential, login, repo, options, cancellationToken,
                Options.Instance.DevHubSearchQuery);
        }

        internal static async Task<DevHubRepoDetail> GetRepoDetailAsync(HttpClient client, DevHubCredential credential,
            string login, RemoteRepoIdentifier repo, DevHubActivityOptions options, CancellationToken cancellationToken,
            string customQuery = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = new DevHubRepoDetail { RepoIdentifier = repo, FetchedAt = DateTime.UtcNow };
            var root = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}";
            var hasCustomQuery = !string.IsNullOrWhiteSpace(customQuery);
            // Each category owns its outcomes while requests overlap; merge only after all have completed.
            var pullOutcome = new DevHubRepoDetail();
            var issueOutcome = new DevHubRepoDetail();
            var buildOutcome = new DevHubRepoDetail();
            // REST repository lists cannot enforce GitHub search syntax. Use the same scoped
            // activity searches as the dashboard, keeping personal work independent of the filter.
            var pulls = DevHubActivityRequest.ReadCategoryAsync(pullOutcome, DevHubActivityKind.PullRequests, async () =>
            {
                var prs = hasCustomQuery
                    ? new List<DevHubPullRequest>()
                    : await ReadPagesAsync(client, credential, root + "/pulls?state=open&sort=updated&direction=desc",
                        null, options.MaxItems, item => ParseRepoPullRequests("[" + item.GetRawText() + "]", repo, login), cancellationToken);
                if (!string.IsNullOrEmpty(login))
                {
                    var personal = await GetUserPullRequestsAsync(client, credential, login, options, cancellationToken, repo,
                        customQuery, populateMetadata: false);
                    MergePullRequests(prs, personal);
                }
                else if (hasCustomQuery)
                {
                    var incoming = await SearchPullRequestsAsync(client, credential,
                        BuildActivitySearchQuery("is:pr", login, customQuery, repo), login, options.MaxItems, cancellationToken);
                    MergePullRequests(prs, incoming.Where(pr => pr.IsOpen));
                }
                cancellationToken.ThrowIfCancellationRequested();
                DevHubActivityRequest.ReportPullRequests(options, prs, repo);
                await PopulateActivityMetadataAsync(client, credential, prs, cancellationToken);
                return prs;
            }, cancellationToken);
            var issuesTask = DevHubActivityRequest.ReadCategoryAsync(issueOutcome, DevHubActivityKind.Issues, async () =>
            {
                var issues = hasCustomQuery
                    ? new List<DevHubIssue>()
                    : await ReadPagesAsync(client, credential, root + "/issues?state=open&sort=updated&direction=desc",
                        null, options.MaxItems, item => ParseRepoIssues("[" + item.GetRawText() + "]", repo, login), cancellationToken);
                if (!string.IsNullOrEmpty(login))
                {
                    var personal = await GetUserIssuesAsync(client, credential, login, options, cancellationToken, repo, customQuery);
                    MergeIssues(issues, personal);
                }
                else if (hasCustomQuery)
                {
                    var incoming = await SearchIssuesAsync(client, credential,
                        BuildActivitySearchQuery("is:issue", login, customQuery, repo), login, options.MaxItems, cancellationToken);
                    MergeIssues(issues, incoming.Where(issue => !issue.IsClosed));
                }
                return issues;
            }, cancellationToken);
            var builds = DevHubActivityRequest.ReadCategoryAsync(buildOutcome, DevHubActivityKind.CiRuns,
                () => ReadCiRunsAsync(client, credential, repo, login, options.MaxItems, cancellationToken), cancellationToken);
            await Task.WhenAll(pulls, issuesTask, builds);
            cancellationToken.ThrowIfCancellationRequested();
            detail.PullRequests = await pulls;
            detail.Issues = await issuesTask;
            detail.CiRuns = await builds;
            detail.HasCategoryOutcomes = true;
            detail.PullRequestsStatus = pullOutcome.PullRequestsStatus;
            detail.IssuesStatus = issueOutcome.IssuesStatus;
            detail.CiRunsStatus = buildOutcome.CiRunsStatus;
            detail.ActivityWarning = string.Join(" ", new[] { pullOutcome.ActivityWarning, issueOutcome.ActivityWarning, buildOutcome.ActivityWarning }
                .Where(message => !string.IsNullOrWhiteSpace(message)));
            detail.Status = DevHubActivityRequest.AggregateStatus(detail.PullRequestsStatus, detail.IssuesStatus, detail.CiRunsStatus, detail.HasData);
            return detail;
        }

        // The limit applies independently to each activity lane. A busy incoming lane must
        // not crowd out authored/assigned work, or the separately bounded completed lane.
        internal static async Task<List<DevHubPullRequest>> GetUserPullRequestsAsync(HttpClient client,
            DevHubCredential credential, string login, DevHubActivityOptions options, CancellationToken cancellationToken,
            RemoteRepoIdentifier repo = null, string customQuery = null, bool populateMetadata = true)
        {
            var results = new List<DevHubPullRequest>();
            var scope = repo == null ? string.Empty : $" AND repo:{repo.Owner}/{repo.Repo}";
            var queries = new[] { BuildActivitySearchQuery("is:pr", login, customQuery, repo), BuildPersonalSearchQuery("is:pr", login, "author", repo),
                BuildActivitySearchQuery("is:pr", login, customQuery, repo, "review-requested"),
                BuildActivitySearchQuery("is:pr", login, customQuery, repo, "mentions"),
                BuildPersonalSearchQuery("is:pr", login, "assignee", repo) };
            var cutoff = DateTime.UtcNow.AddDays(-14);
            var searches = queries.Select(query => SearchPullRequestsAsync(client, credential, query, login, options.MaxItems, cancellationToken)).ToList();
            if (options.IncludeCompleted)
            {
                searches.AddRange(new[] { "author", "assignee" }.Select(qualifier => SearchPullRequestsAsync(client, credential,
                    $"is:pr is:merged {qualifier}:{login} merged:>={cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{scope}",
                    login, options.MaxItems, cancellationToken, cutoff)));
            }
            var pages = await Task.WhenAll(searches);
            for (var index = 0; index < queries.Length; index++)
            {
                var page = pages[index];
                foreach (var pr in page)
                {
                    pr.IsReviewRequestedForCurrentUser = index == 2 && pr.IsOpen;
                    pr.IsMentioned = index == 3;
                    pr.IsAssignedToCurrentUser |= index == 4;
                }
                MergePullRequests(results, page.Where(pr => pr.IsOpen));
            }
            if (options.IncludeCompleted)
            {
                foreach (var completed in pages.Skip(queries.Length))
                {
                    MergePullRequests(results, completed.Where(pr => pr.IsMerged && pr.UpdatedAt >= cutoff
                        && (pr.IsAuthoredByCurrentUser || pr.IsAssignedToCurrentUser)));
                }
            }
            if (populateMetadata)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DevHubActivityRequest.ReportPullRequests(options, results, repo);
                await PopulateActivityMetadataAsync(client, credential, results, cancellationToken);
            }
            return results;
        }

        internal static async Task<List<DevHubIssue>> GetUserIssuesAsync(HttpClient client,
            DevHubCredential credential, string login, DevHubActivityOptions options, CancellationToken cancellationToken,
            RemoteRepoIdentifier repo = null, string customQuery = null)
        {
            var results = new List<DevHubIssue>();
            var scope = repo == null ? string.Empty : $" AND repo:{repo.Owner}/{repo.Repo}";
            var queries = new[] { BuildActivitySearchQuery("is:issue", login, customQuery, repo), BuildPersonalSearchQuery("is:issue", login, "author", repo),
                BuildPersonalSearchQuery("is:issue", login, "assignee", repo),
                BuildActivitySearchQuery("is:issue", login, customQuery, repo, "mentions") };
            var cutoff = DateTime.UtcNow.AddDays(-14);
            var searches = queries.Select(query => SearchIssuesAsync(client, credential, query, login, options.MaxItems, cancellationToken)).ToList();
            if (options.IncludeCompleted)
            {
                searches.AddRange(new[] { "author", "assignee" }.Select(qualifier => SearchIssuesAsync(client, credential,
                    $"is:issue is:closed {qualifier}:{login} closed:>={cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{scope}",
                    login, options.MaxItems, cancellationToken, cutoff)));
            }
            var pages = await Task.WhenAll(searches);
            for (var index = 0; index < queries.Length; index++)
            {
                var page = pages[index];
                foreach (var issue in page)
                    issue.IsMentioned = index == 3;
                MergeIssues(results, page.Where(issue => !issue.IsClosed));
            }
            if (options.IncludeCompleted)
            {
                foreach (var completed in pages.Skip(queries.Length))
                {
                    MergeIssues(results, completed.Where(issue => issue.IsClosed && issue.UpdatedAt >= cutoff
                        && (issue.IsAuthoredByCurrentUser || issue.IsAssignedToCurrentUser)));
                }
            }
            return results;
        }

        private static string BuildActivitySearchQuery(string typePrefix, string login, string customQuery,
            RemoteRepoIdentifier repo, string qualifier = null)
        {
            var scope = repo == null ? string.Empty : $" AND repo:{repo.Owner}/{repo.Repo}";
            if (string.IsNullOrWhiteSpace(customQuery))
                return qualifier == null ? BuildSearchQuery(typePrefix, login) + scope
                    : $"{typePrefix} is:open {qualifier}:{login}{scope}";

            var query = BuildSearchQuery(typePrefix + " AND", login, "(" + customQuery + ")");
            return query + (qualifier == null ? string.Empty : $" AND is:open AND {qualifier}:{login}") + scope;
        }

        private static void MergePullRequests(List<DevHubPullRequest> results, IEnumerable<DevHubPullRequest> additions)
        {
            foreach (var pr in additions)
            {
                var existing = results.Find(item => item.NumericId == pr.NumericId && Equals(item.RepoIdentifier, pr.RepoIdentifier));
                if (existing == null)
                    results.Add(pr);
                else
                {
                    existing.IsAuthoredByCurrentUser |= pr.IsAuthoredByCurrentUser;
                    existing.IsAssignedToCurrentUser |= pr.IsAssignedToCurrentUser;
                    existing.IsReviewRequestedForCurrentUser |= pr.IsReviewRequestedForCurrentUser;
                    existing.IsMentioned |= pr.IsMentioned;
                }
            }
        }

        private static void MergeIssues(List<DevHubIssue> results, IEnumerable<DevHubIssue> additions)
        {
            foreach (var issue in additions)
            {
                var existing = results.Find(item => item.NumericId == issue.NumericId && Equals(item.RepoIdentifier, issue.RepoIdentifier));
                if (existing == null)
                    results.Add(issue);
                else
                {
                    existing.IsAuthoredByCurrentUser |= issue.IsAuthoredByCurrentUser;
                    existing.IsAssignedToCurrentUser |= issue.IsAssignedToCurrentUser;
                    existing.IsMentioned |= issue.IsMentioned;
                }
            }
        }

        private static Task<List<DevHubPullRequest>> SearchPullRequestsAsync(HttpClient client, DevHubCredential credential,
            string query, string login, int maxItems, CancellationToken cancellationToken, DateTime? completedSince = null)
        {
            return ReadPagesAsync(client, credential, SearchUrl(query), "items", maxItems,
                item => completedSince.HasValue && !HasRecentOutcome(item, "merged_at", completedSince.Value)
                    ? new List<DevHubPullRequest>() : ParsePullRequests("{\"items\":[" + item.GetRawText() + "]}", login), cancellationToken);
        }

        private static Task<List<DevHubIssue>> SearchIssuesAsync(HttpClient client, DevHubCredential credential,
            string query, string login, int maxItems, CancellationToken cancellationToken, DateTime? completedSince = null)
        {
            return ReadPagesAsync(client, credential, SearchUrl(query), "items", maxItems,
                item => completedSince.HasValue && !HasRecentOutcome(item, "closed_at", completedSince.Value)
                    ? new List<DevHubIssue>() : ParseIssues("{\"items\":[" + item.GetRawText() + "]}", login), cancellationToken);
        }

        private static bool HasRecentOutcome(JsonElement item, string property, DateTime cutoff)
        {
            if (property == "merged_at" && item.TryGetProperty("pull_request", out var pullRequest)
                && pullRequest.ValueKind == JsonValueKind.Object)
                item = pullRequest;
            return item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                && value.TryGetDateTime(out var date) && date.ToUniversalTime() >= cutoff;
        }

        private static void ObserveSearchCompleteness(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("incomplete_results", out var incomplete)
                && incomplete.ValueKind == JsonValueKind.True)
            {
                DevHubActivityRequest.Failed();
                DevHubActivityRequest.Warning("GitHub returned incomplete search results. Some activity may be missing. Refresh to retry.");
            }
        }

        private static string SearchUrl(string query) =>
            $"https://api.github.com/search/issues?q={Uri.EscapeDataString(query)}&advanced_search=true&sort=updated&order=desc";

        private static async Task<List<T>> ReadPagesAsync<T>(HttpClient client, DevHubCredential credential,
            string url, string collection, int maxItems, Func<JsonElement, IEnumerable<T>> parse, CancellationToken cancellationToken,
            bool requireComplete = false)
        {
            var results = new List<T>();
            var pageSize = Math.Min(100, maxItems);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Count source records, not just accepted records (the issues endpoint also returns PRs).
            for (var page = 1; page <= (maxItems + pageSize - 1) / pageSize; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var response = await SendGetAsync(credential, $"{url}&per_page={pageSize}&page={page}", cancellationToken, client);
                    response.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                    ObserveSearchCompleteness(doc.RootElement);
                    var items = collection == null ? doc.RootElement : doc.RootElement.GetProperty(collection);
                    var added = false;
                    foreach (var item in items.EnumerateArray().Take(Math.Min(pageSize, maxItems - (page - 1) * pageSize)))
                    {
                        if (!seen.Add(item.GetRawText()))
                            continue;
                        results.AddRange(parse(item));
                        added = true;
                    }
                    var hasNextLink = response.Headers.TryGetValues("Link", out var links)
                        && links.Any(link => link.IndexOf("rel=\"next\"", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (requireComplete && hasNextLink && page * pageSize >= maxItems)
                        throw new InvalidOperationException("Completed run details exceeded the request budget.");
                    if (!added || items.GetArrayLength() < pageSize && !hasNextLink)
                        break;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                    DevHubActivityRequest.Failed();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (requireComplete)
                        throw;
                    break;
                }
            }
            return results;
        }

        internal static async Task<List<DevHubCiRun>> GetUserCiRunsAsync(HttpClient client, DevHubCredential credential,
            string login, DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            var repos = await ReadPagesAsync(client, credential,
                "https://api.github.com/user/repos?sort=pushed&affiliation=owner,collaborator,organization_member", null,
                Math.Min(5, options.MaxItems), item => ParseRepoList("[" + item.GetRawText() + "]"), cancellationToken);
            using var semaphore = new SemaphoreSlim(3, 3);
            var pages = await Task.WhenAll(repos.Select(async repo =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    return await ReadCiRunsAsync(client, credential, repo, login, options.MaxItems, cancellationToken, false);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
            var visible = pages.SelectMany(page => page).OrderByDescending(run => run.Timestamp).Take(options.MaxItems).ToList();
            await EnrichCiRunFailuresAsync(client, credential, visible, cancellationToken);
            return visible;
        }

        private static async Task<List<DevHubCiRun>> ReadCiRunsAsync(HttpClient client, DevHubCredential credential,
            RemoteRepoIdentifier repo, string login, int maxItems, CancellationToken cancellationToken, bool enrich = true)
        {
            var root = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/actions/runs";
            var runs = await ReadPagesAsync(client, credential, root + "?exclude_pull_requests=false", "workflow_runs", maxItems, item =>
            {
                var run = ParseCiRuns("{\"workflow_runs\":[" + item.GetRawText() + "]}", repo, login).Single();
                return new[] { run };
            }, cancellationToken);
            if (enrich)
                await EnrichCiRunFailuresAsync(client, credential, runs, cancellationToken);
            return runs;
        }

        private static async Task EnrichCiRunFailuresAsync(HttpClient client, DevHubCredential credential,
            IReadOnlyList<DevHubCiRun> runs, CancellationToken cancellationToken)
        {
            foreach (var run in runs.Where(run => run.Status == "failure" && run.RepoIdentifier != null)
                .OrderByDescending(run => run.Timestamp).Take(DevHubActivityOptions.FailureDetailLimit))
            {
                try
                {
                    if (!Uri.TryCreate(run.WebUrl, UriKind.Absolute, out var uri)
                        || !long.TryParse(uri.Segments.Last().Trim('/'), out var id))
                        continue;
                    var repo = run.RepoIdentifier;
                    var root = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/actions/runs";
                    var jobsJson = await DevHubMetadataCache.GetCompletedRunAsync(credential,
                        DevHubMetadataCache.GetClientScope(client) + "|" + run.WebUrl + "|" + run.Timestamp.Ticks,
                        async token => JsonSerializer.Serialize(await ReadPagesAsync(client, credential,
                            $"{root}/{id}/jobs?filter=latest", "jobs", 100,
                            item => new[] { item.Clone() }, token, requireComplete: true)), cancellationToken);
                    var jobs = JsonSerializer.Deserialize<List<JsonElement>>(jobsJson);
                    var failed = jobs.Where(job => GetString(job, "conclusion") == "failure" || GetString(job, "conclusion") == "timed_out").ToList();
                    if (failed.Count > 0)
                    {
                        run.FailureSummary = string.Join(", ", failed.Select(job => GetString(job, "name")));
                        run.FailureUrl = failed.Select(job => GetString(job, "html_url")).FirstOrDefault(url => !string.IsNullOrEmpty(url));
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                    DevHubActivityRequest.Warning("Builds loaded, but failed-job details are unavailable. Refresh to retry.");
                }
            }
        }

        public string GetRepoWebUrl(RemoteRepoIdentifier repo) =>
            $"https://github.com/{repo.Owner}/{repo.Repo}";

        public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) =>
            $"https://github.com/{repo.Owner}/{repo.Repo}/pulls";

        public string GetIssuesWebUrl(RemoteRepoIdentifier repo) =>
            $"https://github.com/{repo.Owner}/{repo.Repo}/issues";

        internal static async Task PopulateOwnPullRequestsAsync(HttpClient client, DevHubCredential credential,
            List<DevHubPullRequest> results, string login, CancellationToken cancellationToken, RemoteRepoIdentifier repo = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var query = Uri.EscapeDataString(BuildPersonalSearchQuery("is:pr", login, "author", repo));
                using var response = await SendGetAsync(credential,
                    $"https://api.github.com/search/issues?q={query}&advanced_search=true&sort=updated&order=desc&per_page={DevHubActivityOptions.DefaultLimit}",
                    cancellationToken, client);
                response.EnsureSuccessStatusCode();
                foreach (var pr in ParsePullRequests(await HttpResponseReader.ReadStringAsync(response, cancellationToken), login)
                    .Where(item => item.IsOpen && item.IsAuthoredByCurrentUser && (repo == null || Equals(repo, item.RepoIdentifier))))
                {
                    var existing = results.Find(item => item.NumericId == pr.NumericId && Equals(item.RepoIdentifier, pr.RepoIdentifier));
                    if (existing == null)
                        results.Add(pr);
                    else
                        existing.IsAuthoredByCurrentUser = true;
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
            }
        }

        internal static async Task<string> PopulateOwnIssuesAsync(HttpClient client, DevHubCredential credential,
            List<DevHubIssue> results, string login, int maxItems,
            CancellationToken cancellationToken, RemoteRepoIdentifier repo = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Supplemental searches must not discard accessible REST data when search is throttled.
                foreach (var qualifier in new[] { "author", "assignee" })
                {
                    var query = Uri.EscapeDataString(BuildPersonalSearchQuery("is:issue", login, qualifier, repo));
                    using var response = await SendGetAsync(credential,
                        $"https://api.github.com/search/issues?q={query}&advanced_search=true&sort=updated&order=desc&per_page={NormalizeMaxItems(maxItems)}",
                        cancellationToken, client);
                    response.EnsureSuccessStatusCode();
                    foreach (var issue in ParseIssues(await HttpResponseReader.ReadStringAsync(response, cancellationToken), login))
                    {
                        if ((repo == null || Equals(issue.RepoIdentifier, repo))
                            && !results.Any(existing => existing.NumericId == issue.NumericId && Equals(existing.RepoIdentifier, issue.RepoIdentifier)))
                            results.Add(issue);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return "Some issue activity could not be loaded. Refresh to retry.";
            }
        }

        private static async Task PopulateActivityMetadataAsync(HttpClient client, DevHubCredential credential,
            IReadOnlyList<DevHubPullRequest> pullRequests, CancellationToken cancellationToken)
        {
            var active = pullRequests.Where(pr => pr.IsOpen && pr.RepoIdentifier != null)
                .OrderByDescending(pr => pr.IsAuthoredByCurrentUser && !pr.IsDraft)
                .ThenByDescending(pr => pr.UpdatedAt).ToList();
            for (var offset = 0; offset < active.Count; offset += 25)
            {
                try
                {
                    await PopulateActionMetadataAsync(client, credential, active.Skip(offset).Take(25).ToList(), cancellationToken, true);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                    var reason = ex is HttpRequestException || ex is JsonException ? ex.Message
                        : ex is OperationCanceledException ? "The GitHub metadata request timed out. Try again later."
                        : "An unexpected error occurred while reading GitHub metadata. See the extension log for details.";
                    DevHubActivityRequest.Warning($"Pull requests loaded, but some review and check details are unavailable. {reason}");
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        internal static async Task PopulateActionMetadataAsync(HttpClient client, DevHubCredential credential,
            IReadOnlyList<DevHubPullRequest> pullRequests, CancellationToken cancellationToken, bool includeIncoming = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authored = pullRequests.Where(pr => (includeIncoming || pr.IsAuthoredByCurrentUser && !pr.IsDraft)
                && pr.IsOpen && pr.RepoIdentifier != null).Take(100).ToList();
            if (authored.Count == 0)
                return;

            // One compact rollup batch avoids three REST requests per PR and obsolete reviews.
            var query = new StringBuilder("query {");
            for (var i = 0; i < authored.Count; i++)
            {
                var pr = authored[i];
                pr.CanMerge = null;
                pr.UnresolvedThreadCount = null;
                pr.MergeBlockReason = "Merge readiness could not be verified.";
                query.Append($"r{i}: repository(owner:{JsonSerializer.Serialize(pr.RepoIdentifier.Owner)}, name:{JsonSerializer.Serialize(pr.RepoIdentifier.Repo)}) {{ pullRequest(number:{pr.NumericId}) {{ headRefName headRefOid headRepository {{ url }} closingIssuesReferences(first:10) {{ nodes {{ url title }} }} state isDraft merged mergeable mergeStateStatus reviewDecision reviewThreads(first:100) {{ nodes {{ isResolved }} pageInfo {{ hasNextPage }} }} commits(last:1) {{ nodes {{ commit {{ oid statusCheckRollup {{ state contexts(first:100) {{ nodes {{ ... on CheckRun {{ name status conclusion detailsUrl }} ... on StatusContext {{ context state targetUrl }} }} pageInfo {{ hasNextPage }} }} }} }} }} }} }} }}");
            }
            query.Append('}');
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Token);
            request.Content = new StringContent(JsonSerializer.Serialize(new { query = query.ToString() }), Encoding.UTF8, "application/json");
            using var response = await DevHubReadRequestSharing.Instance.SendAsync(client, request, credential, cancellationToken, readOnlyPost: true);
            if (!response.IsSuccessStatusCode)
            {
                var rateLimited = (int)response.StatusCode == 429
                    || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0");
                var guidance = rateLimited ? "GitHub's API rate limit was reached. Wait before retrying."
                    : response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "Reconnect the GitHub account to renew its credentials."
                    : response.StatusCode == System.Net.HttpStatusCode.Forbidden ? "Check the GitHub account's repository and checks permissions. Refresh cannot grant missing permissions."
                    : "GitHub could not provide metadata. Try again later.";
                throw new HttpRequestException($"GitHub metadata returned HTTP {(int)response.StatusCode}. {guidance}");
            }
            using var doc = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            var hasErrors = doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0;
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                for (var i = 0; i < authored.Count; i++)
                {
                    if (data.TryGetProperty($"r{i}", out var repository) && repository.ValueKind == JsonValueKind.Object
                        && repository.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object)
                    {
                        ApplyActionMetadata(authored[i], pr);
                        if (hasErrors && errors.EnumerateArray().Any(error => AffectsPullRequest(error, i)))
                        {
                            authored[i].CanMerge = null;
                            authored[i].MergeBlockReason = "Merge readiness could not be verified because some GitHub metadata was unavailable.";
                        }
                    }
                }
            }
            if (hasErrors)
            {
                var samlErrors = errors.EnumerateArray().Where(IsSamlError).ToArray();
                if (samlErrors.Length > 0)
                {
                    var affected = authored.Where((pr, index) => samlErrors.Any(error => AffectsPullRequest(error, index)))
                        .Select(pr => $"{pr.RepoIdentifier.Owner}/{pr.RepoIdentifier.Repo}#{pr.NumericId}").Distinct();
                    OutputLog.Info("GitHub", "Review/check metadata is restricted by organization SAML SSO for "
                        + string.Join(", ", affected) + ". Authorize the existing OAuth app under GitHub Settings > Applications > "
                        + "Authorized OAuth Apps for that organization. Loaded pull requests remain available.");
                }
                var actionableErrors = errors.EnumerateArray().Where(error => !IsSamlError(error)).ToArray();
                if (actionableErrors.Length == 0)
                    return;
                var messages = actionableErrors.Select(error => GetString(error, "message"))
                    .Where(message => !string.IsNullOrWhiteSpace(message)).Distinct().Take(3)
                    .Select(message =>
                    {
                        var safe = string.IsNullOrEmpty(credential.Token) ? message : message.Replace(credential.Token, "[redacted]");
                        return safe.Length > 300 ? safe.Substring(0, 300) + "..." : safe;
                    });
                var details = string.Join(" ", messages);
                throw new JsonException("GitHub could not retrieve all pull request action metadata. " + details
                    + " Check the reported error and the account's repository/checks permissions before retrying.");
            }
        }

        private static bool IsSamlError(JsonElement error) =>
            (GetString(error, "message") ?? string.Empty).IndexOf("SAML", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool AffectsPullRequest(JsonElement error, int index) =>
            !error.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array || path.GetArrayLength() == 0
            || path[0].ValueKind != JsonValueKind.String || path[0].GetString() == $"r{index}";

        internal static void ApplyActionMetadata(DevHubPullRequest pr, JsonElement metadata)
        {
            pr.CiStatus = null;
            pr.CanMerge = null;
            pr.UnresolvedThreadCount = null;
            pr.MergeBlockReason = "Merge readiness could not be verified.";
            pr.ChecksSummary = null;
            pr.ChecksUrl = null;
            pr.ChecksCommitId = null;
            if (metadata.TryGetProperty("headRefName", out var sourceBranch))
                pr.SourceBranch = sourceBranch.ValueKind == JsonValueKind.String ? sourceBranch.GetString() : null;
            if (metadata.TryGetProperty("headRefOid", out var head))
                pr.HeadCommitId = head.ValueKind == JsonValueKind.String ? head.GetString() : null;
            if (metadata.TryGetProperty("headRepository", out var sourceRepository))
                pr.SourceRepoIdentifier = sourceRepository.ValueKind == JsonValueKind.Object
                    ? RemoteRepoIdentifier.TryParse(GetString(sourceRepository, "url")) : null;
            pr.LinkedWorkItems.Clear();
            if (metadata.TryGetProperty("closingIssuesReferences", out var linked) && linked.ValueKind == JsonValueKind.Object
                && linked.TryGetProperty("nodes", out var linkedNodes) && linkedNodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in linkedNodes.EnumerateArray().Where(issue => issue.ValueKind == JsonValueKind.Object))
                {
                    var url = GetString(issue, "url");
                    if (!string.IsNullOrEmpty(url))
                        pr.LinkedWorkItems[url] = GetString(issue, "title");
                }
            }
            if (metadata.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
                pr.Status = state.GetString() == "OPEN"
                    ? pr.IsDraft && (!metadata.TryGetProperty("isDraft", out var explicitDraft) || explicitDraft.ValueKind != JsonValueKind.False) ? "draft" : "open"
                    : state.GetString().ToLowerInvariant();
            if (pr.IsOpen && metadata.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True)
                pr.Status = "draft";
            pr.IsMerged = GetString(metadata, "state") == "MERGED"
                || metadata.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True;
            if (pr.IsMerged)
                pr.Status = "merged";
            if (pr.Status == "open" && metadata.TryGetProperty("mergeable", out var mergeable)
                && mergeable.ValueKind == JsonValueKind.String
                && mergeable.GetString() == "CONFLICTING")
                pr.Status = "conflicts";
            // reviewDecision is GitHub's current decision, not a historical CHANGES_REQUESTED review.
            var reviewDecision = metadata.TryGetProperty("reviewDecision", out var decision) && decision.ValueKind == JsonValueKind.String
                ? decision.GetString() : null;
            pr.HasChangesRequested = pr.IsOpen && reviewDecision == "CHANGES_REQUESTED";
            pr.IsApproved = pr.IsOpen && !pr.IsDraft && reviewDecision == "APPROVED";
            var checksComplete = false;
            if (metadata.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Object
                && commits.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                && nodes.GetArrayLength() == 1
                && nodes[0].TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
                && commit.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
                && rollup.TryGetProperty("state", out var checkState))
            {
                pr.ChecksCommitId = GetString(commit, "oid");
                pr.CiStatus = checkState.GetString() switch
                {
                    "FAILURE" or "ERROR" => "failure",
                    "PENDING" or "EXPECTED" => "pending",
                    "SUCCESS" => "success",
                    _ => null,
                };
                if (rollup.TryGetProperty("contexts", out var contexts) && contexts.ValueKind == JsonValueKind.Object
                    && contexts.TryGetProperty("nodes", out var checks) && checks.ValueKind == JsonValueKind.Array)
                {
                    var summaries = new List<string>();
                    foreach (var check in checks.EnumerateArray().Where(check => check.ValueKind == JsonValueKind.Object))
                    {
                        var name = GetString(check, "name") ?? GetString(check, "context");
                        var status = GetString(check, "conclusion") ?? GetString(check, "state") ?? GetString(check, "status");
                        summaries.Add($"{name}: {status}");
                        var url = GetString(check, "detailsUrl") ?? GetString(check, "targetUrl");
                        // Prefer a failing check's direct link over a successful check's link.
                        if (pr.ChecksUrl == null || status == "FAILURE" || status == "ERROR" || status == "TIMED_OUT")
                            pr.ChecksUrl = url ?? pr.ChecksUrl;
                    }
                    pr.ChecksSummary = string.Join(", ", summaries);
                    checksComplete = IsCompleteConnection(contexts);
                    if (!checksComplete)
                        pr.ChecksSummary += " (partial check list)";
                }
            }
            if (metadata.TryGetProperty("reviewThreads", out var threads) && threads.ValueKind == JsonValueKind.Object
                && threads.TryGetProperty("nodes", out var threadNodes) && threadNodes.ValueKind == JsonValueKind.Array
                && IsCompleteConnection(threads)
                && threadNodes.EnumerateArray().All(thread => thread.ValueKind == JsonValueKind.Object
                    && thread.TryGetProperty("isResolved", out var resolved)
                    && (resolved.ValueKind == JsonValueKind.True || resolved.ValueKind == JsonValueKind.False)))
                pr.UnresolvedThreadCount = threadNodes.EnumerateArray().Count(thread => thread.GetProperty("isResolved").ValueKind == JsonValueKind.False);

            var mergeState = GetString(metadata, "mergeStateStatus");
            var mergeability = GetString(metadata, "mergeable");
            if (!pr.IsOpen || pr.IsDraft || mergeability == "CONFLICTING" || pr.HasChangesRequested
                || pr.UnresolvedThreadCount > 0 || pr.CiStatus == "failure" || pr.CiStatus == "pending"
                || mergeState == "BLOCKED" || mergeState == "BEHIND" || mergeState == "DIRTY"
                || mergeState == "UNSTABLE" || mergeState == "DRAFT" || reviewDecision == "REVIEW_REQUIRED")
            {
                pr.CanMerge = false;
                pr.MergeBlockReason = !pr.IsOpen ? "Pull request is closed."
                    : pr.IsDraft ? "Pull request is a draft."
                    : mergeability == "CONFLICTING" ? "Merge conflicts must be resolved."
                    : pr.HasChangesRequested ? "Changes requested."
                    : pr.UnresolvedThreadCount > 0 ? "Unresolved review discussions."
                    : pr.CiStatus == "failure" ? "Checks failing."
                    : pr.CiStatus == "pending" ? "Checks pending."
                    : reviewDecision == "REVIEW_REQUIRED" ? "Required review pending."
                    : $"GitHub merge state: {mergeState}.";
            }
            else if (mergeState == "CLEAN" && mergeability == "MERGEABLE" && reviewDecision == "APPROVED"
                && GetString(metadata, "state") == "OPEN" && metadata.TryGetProperty("isDraft", out var isDraft)
                && isDraft.ValueKind == JsonValueKind.False && checksComplete
                && pr.CiStatus == "success" && pr.UnresolvedThreadCount == 0)
            {
                // CLEAN is GitHub's policy-aware state. Approval or green CI alone is not
                // proof of readiness; missing or paginated discussion data remains unknown.
                pr.CanMerge = true;
                pr.MergeBlockReason = null;
            }
            if (!pr.IsOpen)
                pr.IsReviewRequestedForCurrentUser = false;
        }

        private static string GetString(JsonElement element, string property)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static bool IsCompleteConnection(JsonElement connection)
        {
            return connection.TryGetProperty("pageInfo", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("hasNextPage", out var next) && next.ValueKind == JsonValueKind.False;
        }

        private static string GetPrStatus(JsonElement item)
        {
            if (IsMergedPullRequest(item))
                return "merged";
            if (item.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                && !string.Equals(state.GetString(), "open", StringComparison.OrdinalIgnoreCase))
                return state.GetString();
            return item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ? "draft" : "open";
        }

        private static bool IsMergedPullRequest(JsonElement item)
        {
            return item.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String
                || item.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True
                || item.TryGetProperty("pull_request", out var pullRequest) && pullRequest.ValueKind == JsonValueKind.Object
                    && pullRequest.TryGetProperty("merged_at", out var searchMergedAt) && searchMergedAt.ValueKind == JsonValueKind.String;
        }

        private static bool IsClosedIssue(JsonElement item)
        {
            return item.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                && string.Equals(state.GetString(), "closed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOpenIssue(JsonElement item)
        {
            return !item.TryGetProperty("state", out var state)
                || (state.ValueKind == JsonValueKind.String && string.Equals(state.GetString(), "open", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsIssueAuthor(JsonElement item, string currentUser)
        {
            return !string.IsNullOrEmpty(currentUser) && item.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("login", out var login)
                && string.Equals(login.GetString(), currentUser, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIssueAssignee(JsonElement item, string currentUser)
        {
            return !string.IsNullOrEmpty(currentUser) && item.TryGetProperty("assignees", out var assignees)
                && assignees.ValueKind == JsonValueKind.Array && assignees.EnumerateArray().Any(assignee =>
                    assignee.TryGetProperty("login", out var login)
                    && string.Equals(login.GetString(), currentUser, StringComparison.OrdinalIgnoreCase));
        }

        internal static List<DevHubPullRequest> ParsePullRequests(string json, string currentUser, bool reviewRequested = false)
        {
            var results = new List<DevHubPullRequest>();

            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                ObserveSearchCompleteness(doc.RootElement);
                if (!doc.RootElement.TryGetProperty("items", out var items))
                    return results;

                foreach (var item in items.EnumerateArray())
                {
                    var pr = ParsePrFromSearchItem(item, currentUser);
                    if (pr != null)
                    {
                        pr.IsReviewRequestedForCurrentUser = pr.IsOpen && reviewRequested;
                        results.Add(pr);
                    }
                }
            }

            return results;
        }

        private static DevHubPullRequest ParsePrFromSearchItem(System.Text.Json.JsonElement item, string currentUser)
        {
            var repoUrl = item.GetProperty("repository_url").GetString();
            var repoId = ParseRepoIdentifierFromApiUrl(repoUrl);

            var author = item.GetProperty("user").GetProperty("login").GetString();
            var number = item.GetProperty("number").GetInt32();

            return new DevHubPullRequest
            {
                ProviderName = "GitHub",
                RepoIdentifier = repoId,
                RepoDisplayName = repoId?.DisplayName ?? string.Empty,
                Title = item.GetProperty("title").GetString(),
                Number = $"#{number}",
                NumericId = number,
                Author = author,
                Status = GetPrStatus(item),
                IsMerged = IsMergedPullRequest(item),
                UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                WebUrl = item.GetProperty("html_url").GetString(),
                IsAuthoredByCurrentUser = !string.IsNullOrEmpty(currentUser)
                    && string.Equals(author, currentUser, StringComparison.OrdinalIgnoreCase),
                IsAssignedToCurrentUser = IsIssueAssignee(item, currentUser),
            };
        }

        internal static List<DevHubPullRequest> ParseRepoPullRequests(string json, RemoteRepoIdentifier repo, string currentUser)
        {
            var results = new List<DevHubPullRequest>();

            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var author = item.GetProperty("user").GetProperty("login").GetString();
                    var number = item.GetProperty("number").GetInt32();

                    results.Add(new DevHubPullRequest
                    {
                        ProviderName = "GitHub",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Title = item.GetProperty("title").GetString(),
                        Number = $"#{number}",
                        NumericId = number,
                        Author = author,
                        TargetBranch = item.GetProperty("base").GetProperty("ref").GetString(),
                        SourceBranch = item.GetProperty("head").GetProperty("ref").GetString(),
                        SourceRepoIdentifier = item.GetProperty("head").TryGetProperty("repo", out var headRepo)
                            && headRepo.ValueKind == JsonValueKind.Object ? RemoteRepoIdentifier.TryParse(GetString(headRepo, "html_url")) : null,
                        HeadCommitId = GetString(item.GetProperty("head"), "sha"),
                        Status = GetPrStatus(item),
                        IsMerged = IsMergedPullRequest(item),
                        UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                        WebUrl = item.GetProperty("html_url").GetString(),
                        IsAuthoredByCurrentUser = !string.IsNullOrEmpty(currentUser)
                            && string.Equals(author, currentUser, StringComparison.OrdinalIgnoreCase),
                        IsAssignedToCurrentUser = IsIssueAssignee(item, currentUser),
                        IsReviewRequestedForCurrentUser = (GetPrStatus(item) == "open" || GetPrStatus(item) == "draft")
                            && DevHubRepoSummaryFetcher.IsRequested(item, currentUser, true) == true,
                    });
                }
            }

            return results;
        }

        internal static List<DevHubIssue> ParseIssues(string json, string currentUser = null)
        {
            var results = new List<DevHubIssue>();

            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                ObserveSearchCompleteness(doc.RootElement);
                if (!doc.RootElement.TryGetProperty("items", out var items))
                    return results;

                foreach (var item in items.EnumerateArray())
                {
                    // Skip pull requests (GitHub search returns both)
                    if (item.TryGetProperty("pull_request", out _))
                        continue;

                    var issue = ParseIssueFromSearchItem(item, currentUser);
                    if (issue != null)
                        results.Add(issue);
                }
            }

            return results;
        }

        private static DevHubIssue ParseIssueFromSearchItem(System.Text.Json.JsonElement item, string currentUser)
        {
            var repoUrl = item.GetProperty("repository_url").GetString();
            var repoId = ParseRepoIdentifierFromApiUrl(repoUrl);

            var number = item.GetProperty("number").GetInt32();

            var labels = new List<DevHubLabel>();
            if (item.TryGetProperty("labels", out var labelsArray))
            {
                foreach (var label in labelsArray.EnumerateArray())
                {
                    labels.Add(new DevHubLabel
                    {
                        Name = label.GetProperty("name").GetString(),
                        Color = label.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : null,
                    });
                }
            }

            return new DevHubIssue
            {
                ProviderName = "GitHub",
                RepoIdentifier = repoId,
                RepoDisplayName = repoId?.DisplayName ?? string.Empty,
                Title = item.GetProperty("title").GetString(),
                Number = $"#{number}",
                NumericId = number,
                Author = item.GetProperty("user").GetProperty("login").GetString(),
                Labels = labels,
                State = item.TryGetProperty("state", out var state) ? state.GetString() : "open",
                IsClosed = IsClosedIssue(item),
                IsAuthoredByCurrentUser = IsIssueAuthor(item, currentUser),
                IsAssignedToCurrentUser = (IsOpenIssue(item) || IsClosedIssue(item)) && IsIssueAssignee(item, currentUser),
                WorkItemType = GetIssueType(item),
                AssignedTo = GetAssignees(item),
                Iteration = GetMilestone(item),
                IsBlocked = labels.Any(label => string.Equals(label.Name, "blocked", StringComparison.OrdinalIgnoreCase)),
                Priority = GetPriority(labels),
                UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                WebUrl = item.GetProperty("html_url").GetString(),
            };
        }

        internal static List<DevHubIssue> ParseRepoIssues(string json, RemoteRepoIdentifier repo, string currentUser = null)
        {
            var results = new List<DevHubIssue>();

            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    // Skip pull requests (GitHub issues API returns both)
                    if (item.TryGetProperty("pull_request", out _))
                        continue;

                    var number = item.GetProperty("number").GetInt32();

                    var labels = new List<DevHubLabel>();
                    if (item.TryGetProperty("labels", out var labelsArray))
                    {
                        foreach (var label in labelsArray.EnumerateArray())
                        {
                            labels.Add(new DevHubLabel
                            {
                                Name = label.GetProperty("name").GetString(),
                                Color = label.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : null,
                            });
                        }
                    }

                    results.Add(new DevHubIssue
                    {
                        ProviderName = "GitHub",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Title = item.GetProperty("title").GetString(),
                        Number = $"#{number}",
                        NumericId = number,
                        Author = item.GetProperty("user").GetProperty("login").GetString(),
                        Labels = labels,
                        State = item.TryGetProperty("state", out var state) ? state.GetString() : "open",
                        IsClosed = IsClosedIssue(item),
                        IsAuthoredByCurrentUser = IsIssueAuthor(item, currentUser),
                        IsAssignedToCurrentUser = (IsOpenIssue(item) || IsClosedIssue(item)) && IsIssueAssignee(item, currentUser),
                        WorkItemType = GetIssueType(item),
                        AssignedTo = GetAssignees(item),
                        Iteration = GetMilestone(item),
                        IsBlocked = labels.Any(label => string.Equals(label.Name, "blocked", StringComparison.OrdinalIgnoreCase)),
                        Priority = GetPriority(labels),
                        UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                        WebUrl = item.GetProperty("html_url").GetString(),
                    });
                }
            }

            return results;
        }

        private static string GetIssueType(JsonElement item)
        {
            return item.TryGetProperty("type", out var type) ? GetString(type, "name") : null;
        }

        private static string GetAssignees(JsonElement item)
        {
            return item.TryGetProperty("assignees", out var assignees) && assignees.ValueKind == JsonValueKind.Array
                ? string.Join(", ", assignees.EnumerateArray().Select(assignee => GetString(assignee, "login")).Where(login => login != null)) : null;
        }

        private static string GetMilestone(JsonElement item)
        {
            return item.TryGetProperty("milestone", out var milestone) ? GetString(milestone, "title") : null;
        }

        private static string GetPriority(List<DevHubLabel> labels)
        {
            var priorities = labels.Select(label => label.Name).Where(name => name != null
                && (name.StartsWith("priority:", StringComparison.OrdinalIgnoreCase)
                    || name.Length == 2 && (name[0] == 'P' || name[0] == 'p') && name[1] >= '0' && name[1] <= '4'))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return priorities.Count == 1 ? priorities[0] : null;
        }

        internal static List<DevHubCiRun> ParseCiRuns(string json, RemoteRepoIdentifier repo, string currentUser = null)
        {
            var results = new List<DevHubCiRun>();

            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs))
                    return results;

                foreach (var run in runs.EnumerateArray())
                {
                    var conclusion = run.TryGetProperty("conclusion", out var c) && c.ValueKind != System.Text.Json.JsonValueKind.Null
                        ? c.GetString()
                        : null;

                    var status = run.GetProperty("status").GetString();
                    var timestamp = run.GetProperty("updated_at").GetDateTime().ToUniversalTime();
                    TimeSpan? duration = null;
                    if (status == "completed" && run.TryGetProperty("run_started_at", out var started)
                        && started.ValueKind != JsonValueKind.Null)
                    {
                        var startedAt = started.GetDateTime().ToUniversalTime();
                        if (timestamp >= startedAt)
                            duration = timestamp - startedAt;
                    }

                    results.Add(new DevHubCiRun
                    {
                        ProviderName = "GitHub",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Name = run.GetProperty("name").GetString(),
                        Branch = run.TryGetProperty("head_branch", out var branch) ? branch.GetString() : null,
                        Status = MapGitHubCiStatus(status, conclusion),
                        Timestamp = timestamp,
                        Duration = duration,
                        WebUrl = run.GetProperty("html_url").GetString(),
                        IsTriggeredByCurrentUser = !string.IsNullOrEmpty(currentUser)
                            && string.Equals(GetRunActor(run), currentUser, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            return results;
        }

        private static string GetRunActor(JsonElement run)
        {
            var triggeringLogin = run.TryGetProperty("triggering_actor", out var triggeringActor) ? GetString(triggeringActor, "login") : null;
            return triggeringLogin ?? (run.TryGetProperty("actor", out var actor) ? GetString(actor, "login") : null);
        }

        private static string MapGitHubCiStatus(string status, string conclusion)
        {
            if (status == "in_progress" || status == "queued" || status == "waiting" || status == "requested" || status == "pending")
                return "pending";

            return conclusion switch
            {
                "success" => "success",
                "failure" or "timed_out" => "failure",
                "cancelled" => "cancelled",
                "skipped" or "neutral" or "stale" => "skipped",
                "action_required" => "pending",
                _ => conclusion != null ? "pending" : "pending",
            };
        }

        internal static string BuildPersonalSearchQuery(string typePrefix, string login, string qualifier,
            RemoteRepoIdentifier repo = null)
        {
            var query = $"{typePrefix} is:open {qualifier}:{login}";
            if (repo != null)
                query += $" AND repo:{repo.Owner}/{repo.Repo}";
            return query;
        }

        /// <summary>
        /// Builds the search query for the GitHub Search API.
        /// When a custom query is provided, it is used with the type prefix prepended.
        /// Otherwise, falls back to the default "involves:{login}" query.
        /// The {login} placeholder in custom queries is replaced with the actual username.
        /// </summary>
        internal static string BuildSearchQuery(string typePrefix, string login, string customQuery = null)
        {
            if (string.IsNullOrWhiteSpace(customQuery))
            {
                return $"{typePrefix} is:open involves:{login}";
            }

            var resolved = customQuery.Replace("{login}", login);
            return $"{typePrefix} {resolved}";
        }

        internal static RemoteRepoIdentifier ParseRepoIdentifierFromApiUrl(string apiUrl)
        {
            // Format: https://api.github.com/repos/owner/repo
            if (string.IsNullOrWhiteSpace(apiUrl))
                return null;

            const string prefix = "https://api.github.com/repos/";
            if (!apiUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var path = apiUrl.Substring(prefix.Length);
            var parts = path.Split('/');
            if (parts.Length >= 2)
            {
                return new RemoteRepoIdentifier("github.com", parts[0], parts[1]);
            }

            return null;
        }
    }
}
