using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub.Providers
{
    /// <summary>
    /// GitHub provider for the Dev Hub.
    /// Fetches PRs, issues, and CI status from the GitHub REST API.
    /// </summary>
    internal sealed class GitHubDevHubProvider : IDevHubProvider
    {
        // Shared HttpClient: reusing the connection pool across requests avoids repeated DNS,
        // TCP, and TLS handshakes, which are a significant chunk of the Dev Hub cold-start cost.
        // The Authorization header is per-request because the credential token can change at runtime
        // and DefaultRequestHeaders is not safe to mutate while parallel requests are in flight.
        private static readonly HttpClient _httpClient = CreateSharedHttpClient();

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

        private static async Task<HttpResponseMessage> SendGetAsync(DevHubCredential credential, string url, CancellationToken cancellationToken, HttpClient httpClient = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.Token}");
            return await (httpClient ?? _httpClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private Tuple<DevHubCredential, string> _cachedIdentity;
        private readonly DevHubIdentityCache _identityCache = new DevHubIdentityCache();

        private static string GetPreferredGitHubAccount()
        {
            var account = Options.Instance.DevHubGitHubAccount;
            return string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        }

        public string DisplayName => "GitHub";

        public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return DevHubRepoSummaryFetcher.FetchAsync(repo,
                token => DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), token),
                (credential, url, token) => SendGetAsync(credential, url, token), true, cancellationToken, identityCache: _identityCache);
        }

        internal Task<DevHubRepoSummary> GetRepoSummaryAsync(HttpClient client, DevHubCredential credential,
            RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return DevHubRepoSummaryFetcher.FetchAsync(repo, _ => Task.FromResult(credential),
                (auth, url, token) => SendGetAsync(auth, url, token, client), true, cancellationToken, identityCache: _identityCache);
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
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
            return await GetAuthenticatedUserAsync(_httpClient, credential, cancellationToken);
        }

        internal async Task<DevHubUser> GetAuthenticatedUserAsync(HttpClient httpClient, DevHubCredential credential, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (credential == null)
                return null;

            try
            {
                var response = await SendGetAsync(credential, "https://api.github.com/user", cancellationToken, httpClient);

                // Token expired - invalidate cache and retry once with a fresh credential.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    DevHubCredentialHelper.InvalidateCachedCredential("github.com");
                    credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
                    if (credential == null)
                        return null;

                    response = await SendGetAsync(credential, "https://api.github.com/user", cancellationToken, httpClient);
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var login = root.GetProperty("login").GetString();
                    _cachedIdentity = Tuple.Create(credential, login);
                    _identityCache.Remember(credential, "https://api.github.com/user", login);

                    return new DevHubUser
                    {
                        Username = login,
                        DisplayName = root.TryGetProperty("name", out var name) && name.ValueKind != System.Text.Json.JsonValueKind.Null
                            ? name.GetString()
                            : login,
                        AvatarUrl = root.TryGetProperty("avatar_url", out var avatar)
                            ? avatar.GetString()
                            : null,
                        ProviderName = DisplayName,
                        Host = "github.com",
                    };
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                return null;
            }
        }

        public async Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
        {
            var user = await GetAuthenticatedUserAsync(cancellationToken);
            return user != null ? new[] { user } : Array.Empty<DevHubUser>();
        }

        /// <summary>
        /// Gets the cached GitHub login, or fetches it from the API if not yet cached.
        /// </summary>
        private async Task<(DevHubCredential credential, string login)> GetCredentialAndLoginAsync(CancellationToken cancellationToken)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
            if (credential == null)
                return (null, null);

            var cachedIdentity = _cachedIdentity;
            if (cachedIdentity != null && ReferenceEquals(cachedIdentity.Item1, credential))
                return (credential, cachedIdentity.Item2);

            // Fetch login from API if not yet cached
            var response = await SendGetAsync(credential, "https://api.github.com/user", cancellationToken);

            // Token expired - invalidate cache and retry once.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                DevHubCredentialHelper.InvalidateCachedCredential("github.com");
                credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
                if (credential == null)
                    return (null, null);

                response = await SendGetAsync(credential, "https://api.github.com/user", cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
                return (credential, null);

            var json = await response.Content.ReadAsStringAsync();
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                var login = doc.RootElement.GetProperty("login").GetString();
                _cachedIdentity = Tuple.Create(credential, login);
                _identityCache.Remember(credential, "https://api.github.com/user", login);
                return (credential, login);
            }
        }

        public async Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
                if (credential == null || string.IsNullOrEmpty(login))
                    return Array.Empty<DevHubPullRequest>();

                var query = Uri.EscapeDataString(BuildSearchQuery("is:pr", login, Options.Instance.DevHubSearchQuery));
                var results = new List<DevHubPullRequest>();
                try
                {
                    using var response = await SendGetAsync(credential,
                        $"https://api.github.com/search/issues?q={query}&advanced_search=true&sort=updated&order=desc&per_page=30", cancellationToken);
                    response.EnsureSuccessStatusCode();
                    results = ParsePullRequests(await response.Content.ReadAsStringAsync(), login);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                // Search items do not contain requested_reviewers. Only the dedicated
                // authenticated review-requested query establishes this flag reliably.
                var reviewQuery = Uri.EscapeDataString($"is:pr is:open review-requested:{login}");
                try
                {
                    using (var reviewsResponse = await SendGetAsync(credential,
                        $"https://api.github.com/search/issues?q={reviewQuery}&per_page=30", cancellationToken))
                    {
                        reviewsResponse.EnsureSuccessStatusCode();
                        if (reviewsResponse.IsSuccessStatusCode)
                        {
                            var reviews = ParsePullRequests(await reviewsResponse.Content.ReadAsStringAsync(), login, true);
                            foreach (var review in reviews)
                            {
                                var existing = results.Find(pr => pr.NumericId == review.NumericId && Equals(pr.RepoIdentifier, review.RepoIdentifier));
                                if (existing != null)
                                    existing.IsReviewRequestedForCurrentUser = true;
                                else
                                    results.Add(review);
                            }
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                await PopulateOwnPullRequestsAsync(_httpClient, credential, results, login, cancellationToken);
                await TryPopulateActionMetadataAsync(credential, results, cancellationToken);
                return results;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubPullRequest>();
            }
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
                if (credential == null || string.IsNullOrEmpty(login))
                    return Array.Empty<DevHubIssue>();

                var query = Uri.EscapeDataString(BuildSearchQuery("is:issue", login, Options.Instance.DevHubSearchQuery));
                var results = new List<DevHubIssue>();
                try
                {
                    using var response = await SendGetAsync(credential,
                        $"https://api.github.com/search/issues?q={query}&advanced_search=true&sort=updated&order=desc&per_page=20", cancellationToken);
                    response.EnsureSuccessStatusCode();
                    results = ParseIssues(await response.Content.ReadAsStringAsync(), login);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                {
                    await ex.LogAsync();
                }
                await PopulateOwnIssuesAsync(_httpClient, credential, results, login, 20, cancellationToken);
                return results;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubIssue>();
            }
        }

        public async Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
                if (credential == null || string.IsNullOrEmpty(login))
                    return Array.Empty<DevHubCiRun>();

                return await GetUserCiRunsAsync(_httpClient, credential, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
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

            var repos = ParseRepoList(await reposResponse.Content.ReadAsStringAsync());
            using var semaphore = new SemaphoreSlim(3, 3);
            var results = await Task.WhenAll(repos.Select(async repo =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var runsUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/actions/runs?per_page=3";
                    using var response = await SendGetAsync(credential, runsUrl, cancellationToken, httpClient);
                    return response.IsSuccessStatusCode
                        ? ParseCiRuns(await response.Content.ReadAsStringAsync(), repo)
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
            // GitHub aggregates the user's activity across all repositories via the search API
            // (GetUserPullRequestsAsync / GetUserIssuesAsync), so no per-repo fetch is needed.
            return Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>(Array.Empty<RemoteRepoIdentifier>());
        }

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken)
        {
            if (repo == null)
                return null;

            var perPage = NormalizeMaxItems(maxItems);

            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", GetPreferredGitHubAccount(), cancellationToken);
            if (credential == null)
                return null;

            try
            {
                var detail = new DevHubRepoDetail
                {
                    RepoIdentifier = repo,
                    FetchedAt = DateTime.UtcNow,
                };
                var login = await _identityCache.GetAsync(credential, "https://api.github.com/user",
                    (auth, url, token) => SendGetAsync(auth, url, token), true, cancellationToken);

                // Fetch PRs for this repo
                var prUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/pulls?state=open&sort=updated&direction=desc&per_page={perPage}";
                var prResponse = await SendGetAsync(credential, prUrl, cancellationToken);
                if (prResponse.IsSuccessStatusCode)
                {
                    var prJson = await prResponse.Content.ReadAsStringAsync();
                    detail.PullRequests = ParseRepoPullRequests(prJson, repo, login);
                }
                if (!string.IsNullOrEmpty(login))
                    await PopulateOwnPullRequestsAsync(_httpClient, credential, detail.PullRequests, login, cancellationToken, repo);
                await TryPopulateActionMetadataAsync(credential, detail.PullRequests, cancellationToken);

                // Fetch issues for this repo
                var issueUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/issues?state=open&sort=updated&direction=desc&per_page={perPage}";
                var issueResponse = await SendGetAsync(credential, issueUrl, cancellationToken);
                if (issueResponse.IsSuccessStatusCode)
                {
                    var issueJson = await issueResponse.Content.ReadAsStringAsync();
                    detail.Issues = ParseRepoIssues(issueJson, repo, login);
                    if (!string.IsNullOrEmpty(login))
                        detail.ActivityWarning = await PopulateOwnIssuesAsync(_httpClient, credential, detail.Issues,
                            login, perPage, cancellationToken, repo);
                }

                // Fetch latest CI runs
                var runsUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/actions/runs?per_page={perPage}";
                var runsResponse = await SendGetAsync(credential, runsUrl, cancellationToken);
                if (runsResponse.IsSuccessStatusCode)
                {
                    var runsJson = await runsResponse.Content.ReadAsStringAsync();
                    detail.CiRuns = ParseCiRuns(runsJson, repo);
                }

                return detail;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                return null;
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
                    $"https://api.github.com/search/issues?q={query}&advanced_search=true&sort=updated&order=desc&per_page=30",
                    cancellationToken, client);
                response.EnsureSuccessStatusCode();
                foreach (var pr in ParsePullRequests(await response.Content.ReadAsStringAsync(), login)
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
                    foreach (var issue in ParseIssues(await response.Content.ReadAsStringAsync(), login))
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
                return "Some issue activity could not be loaded. Refresh to retry.";
            }
        }

        private static async Task TryPopulateActionMetadataAsync(DevHubCredential credential,
            IReadOnlyList<DevHubPullRequest> pullRequests, CancellationToken cancellationToken)
        {
            try
            {
                await PopulateActionMetadataAsync(_httpClient, credential, pullRequests, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                // Optional rollups must not hide otherwise accessible PRs (for example with REST-only tokens).
                await ex.LogAsync();
            }
        }

        internal static async Task PopulateActionMetadataAsync(HttpClient client, DevHubCredential credential,
            IReadOnlyList<DevHubPullRequest> pullRequests, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authored = pullRequests.Where(pr => pr.IsAuthoredByCurrentUser && pr.IsOpen
                && !pr.IsDraft && pr.RepoIdentifier != null).Take(100).ToList();
            if (authored.Count == 0)
                return;

            // One compact rollup batch avoids three REST requests per PR and obsolete reviews.
            var query = new StringBuilder("query {");
            for (var i = 0; i < authored.Count; i++)
            {
                var pr = authored[i];
                query.Append($"r{i}: repository(owner:{JsonSerializer.Serialize(pr.RepoIdentifier.Owner)}, name:{JsonSerializer.Serialize(pr.RepoIdentifier.Repo)}) {{ pullRequest(number:{pr.NumericId}) {{ state isDraft mergeable reviewDecision commits(last:1) {{ nodes {{ commit {{ statusCheckRollup {{ state }} }} }} }} }} }}");
            }
            query.Append('}');
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Token);
            request.Content = new StringContent(JsonSerializer.Serialize(new { query = query.ToString() }), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            cancellationToken.ThrowIfCancellationRequested();
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                throw new JsonException("GitHub could not retrieve pull request action metadata.");
            var data = doc.RootElement.GetProperty("data");
            for (var i = 0; i < authored.Count; i++)
            {
                if (data.TryGetProperty($"r{i}", out var repository) && repository.ValueKind == JsonValueKind.Object
                    && repository.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object)
                    ApplyActionMetadata(authored[i], pr);
            }
        }

        internal static void ApplyActionMetadata(DevHubPullRequest pr, JsonElement metadata)
        {
            pr.CiStatus = null;
            if (metadata.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
                pr.Status = state.GetString() == "OPEN" ? "open" : state.GetString().ToLowerInvariant();
            if (pr.IsOpen && metadata.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True)
                pr.Status = "draft";
            if (pr.Status == "open" && metadata.TryGetProperty("mergeable", out var mergeable)
                && mergeable.ValueKind == JsonValueKind.String
                && mergeable.GetString() == "CONFLICTING")
                pr.Status = "conflicts";
            // reviewDecision is GitHub's current decision, not a historical CHANGES_REQUESTED review.
            var reviewDecision = metadata.TryGetProperty("reviewDecision", out var decision) && decision.ValueKind == JsonValueKind.String
                ? decision.GetString() : null;
            pr.HasChangesRequested = pr.IsOpen && reviewDecision == "CHANGES_REQUESTED";
            pr.IsApproved = pr.IsOpen && !pr.IsDraft && reviewDecision == "APPROVED";
            if (metadata.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Object
                && commits.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                && nodes.GetArrayLength() == 1
                && nodes[0].TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
                && commit.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
                && rollup.TryGetProperty("state", out var checkState))
            {
                pr.CiStatus = checkState.GetString() switch
                {
                    "FAILURE" or "ERROR" => "failure",
                    "PENDING" or "EXPECTED" => "pending",
                    "SUCCESS" => "success",
                    _ => null,
                };
            }
            if (!pr.IsOpen)
                pr.IsReviewRequestedForCurrentUser = false;
        }

        private static string GetPrStatus(JsonElement item)
        {
            if (item.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                && !string.Equals(state.GetString(), "open", StringComparison.OrdinalIgnoreCase))
                return state.GetString();
            return item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ? "draft" : "open";
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
                UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                WebUrl = item.GetProperty("html_url").GetString(),
                IsAuthoredByCurrentUser = !string.IsNullOrEmpty(currentUser)
                    && string.Equals(author, currentUser, StringComparison.OrdinalIgnoreCase),
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
                        Status = GetPrStatus(item),
                        UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                        WebUrl = item.GetProperty("html_url").GetString(),
                        IsAuthoredByCurrentUser = !string.IsNullOrEmpty(currentUser)
                            && string.Equals(author, currentUser, StringComparison.OrdinalIgnoreCase),
                        IsReviewRequestedForCurrentUser = GetPrStatus(item) != "closed"
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
                IsAssignedToCurrentUser = IsOpenIssue(item) && IsIssueAssignee(item, currentUser),
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
                        IsAssignedToCurrentUser = IsOpenIssue(item) && IsIssueAssignee(item, currentUser),
                        UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                        WebUrl = item.GetProperty("html_url").GetString(),
                    });
                }
            }

            return results;
        }

        internal static List<DevHubCiRun> ParseCiRuns(string json, RemoteRepoIdentifier repo)
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

                    results.Add(new DevHubCiRun
                    {
                        ProviderName = "GitHub",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Name = run.GetProperty("name").GetString(),
                        Branch = run.TryGetProperty("head_branch", out var branch) ? branch.GetString() : null,
                        Status = MapGitHubCiStatus(status, conclusion),
                        Timestamp = run.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                        WebUrl = run.GetProperty("html_url").GetString(),
                    });
                }
            }

            return results;
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
