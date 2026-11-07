using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Helpers;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub.Providers
{
    /// <summary>
    /// Bitbucket Cloud provider for the Dev Hub.
    /// Fetches PRs, issues, and pipeline status from the Bitbucket REST API v2.
    /// </summary>
    internal sealed class BitbucketDevHubProvider : IDevHubProvider, IDevHubActivitySource, IDevHubRefreshProvider
    {
        // Shared HttpClient: reusing the connection pool across requests avoids repeated DNS,
        // TCP, and TLS handshakes, which are a significant chunk of the Dev Hub cold-start cost.
        // The Authorization header is per-request because the credential token can change at runtime
        // and DefaultRequestHeaders is not safe to mutate while parallel requests are in flight.
        private static readonly HttpClient _httpClient = CreateSharedHttpClient();
        private readonly HttpClient _client;
        private readonly Func<CancellationToken, Task<DevHubCredential>> _acquireCredential;
        private readonly DevHubIdentityCache _identityCache;
        private readonly Func<bool> _hasStoredCredential;

        public BitbucketDevHubProvider() : this(_httpClient,
            token => DevHubCredentialHelper.GetCredentialAsync("bitbucket.org", token))
        {
        }

        internal BitbucketDevHubProvider(HttpClient client, Func<CancellationToken, Task<DevHubCredential>> acquireCredential,
            DevHubIdentityCache identityCache = null, Func<bool> hasStoredCredential = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _acquireCredential = acquireCredential ?? throw new ArgumentNullException(nameof(acquireCredential));
            _identityCache = identityCache ?? new DevHubIdentityCache();
            _hasStoredCredential = hasStoredCredential ?? (() => DevHubCredentialHelper.HasCredential("bitbucket.org")
                || NativeCredentialManager.HasGitCredential("bitbucket.org"));
        }

        private static HttpClient CreateSharedHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            client.DefaultRequestHeaders.Add("User-Agent", "StartScreen-VS-Extension");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        }

        private async Task<HttpResponseMessage> SendGetAsync(DevHubCredential credential, string url, CancellationToken cancellationToken, bool observe = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.Token}");
            var response = await DevHubReadRequestSharing.Instance.SendAsync(_client, request, credential, cancellationToken);
            return observe ? DevHubActivityRequest.Observe(response) : response;
        }

        public string DisplayName => "Bitbucket";

        public Task<DevHubActivitySnapshot> FetchActivityAsync(DevHubActivityKind kind, DevHubActivityOptions options,
            RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return DevHubActivityRequest.FetchAsync(this, kind, options, repo, cancellationToken);
        }

        public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unsupported,
                statusMessage: "Bitbucket repository summaries are not supported. Repository activity is still available."));
        }

        public bool CanHandle(string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return false;

            return remoteUrl.IndexOf("bitbucket.org", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl)
        {
            var parsed = RemoteRepoIdentifier.TryParse(remoteUrl);
            if (parsed != null && parsed.Host.Equals("bitbucket.org", StringComparison.OrdinalIgnoreCase))
                return parsed;

            return null;
        }

        public async Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken)
        {
            var credential = await _acquireCredential(cancellationToken);
            if (credential == null)
                return null;

            try
            {
                var response = await SendGetAsync(credential, "https://api.bitbucket.org/2.0/user", cancellationToken, observe: false);

                // Token expired - invalidate cache and retry once with a fresh credential.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    DevHubCredentialHelper.InvalidateCachedCredential("bitbucket.org");
                    credential = await _acquireCredential(cancellationToken);
                    if (credential == null)
                    {
                        DevHubActivityRequest.Failed(unauthorized: true);
                        return null;
                    }

                    response = await SendGetAsync(credential, "https://api.bitbucket.org/2.0/user", cancellationToken);
                }

                using var finalResponse = response;
                DevHubActivityRequest.Observe(response);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await HttpResponseReader.ReadStringAsync(response, cancellationToken);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var username = root.TryGetProperty("username", out var uname) ? uname.GetString() : null;
                    var displayName = root.TryGetProperty("display_name", out var dn) ? dn.GetString() : username;
                    var avatarUrl = root.TryGetProperty("links", out var links)
                        && links.TryGetProperty("avatar", out var avatar)
                        && avatar.TryGetProperty("href", out var href)
                            ? href.GetString()
                            : null;

                    var identity = GetUserIdentity(root);
                    if (string.IsNullOrEmpty(identity))
                        throw new JsonException("Bitbucket did not return an authenticated identity.");
                    _identityCache.Remember(credential, "https://api.bitbucket.org/2.0/user", identity);

                    return new DevHubUser
                    {
                        Username = username ?? credential.Username,
                        IdentityId = identity,
                        DisplayName = displayName ?? credential.Username,
                        AvatarUrl = avatarUrl,
                        ProviderName = DisplayName,
                        Host = "bitbucket.org",
                    };
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return null;
            }
        }

        public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(DevHubRefreshContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.RemoteUrls.Any(url => ParseRemoteUrl(url) != null) && !_hasStoredCredential())
                return Task.FromResult<IReadOnlyList<DevHubUser>>(Array.Empty<DevHubUser>());
            return GetAuthenticatedUsersAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
        {
            var user = await GetAuthenticatedUserAsync(cancellationToken);
            return user != null ? new[] { user } : Array.Empty<DevHubUser>();
        }

        /// <summary>
        /// Gets the stable Bitbucket identity, with username compatibility for older responses.
        /// </summary>
        internal async Task<(DevHubCredential credential, string username)> GetCredentialAndUsernameAsync(CancellationToken cancellationToken)
        {
            var credential = await _acquireCredential(cancellationToken);
            if (credential == null)
            {
                DevHubActivityRequest.Failed(unauthorized: true);
                return (null, null);
            }
            var username = await _identityCache.GetAsync(credential, "https://api.bitbucket.org/2.0/user",
                (auth, url, token) => SendGetAsync(auth, url, token), true, cancellationToken, identityProperty: "uuid");
            if (string.IsNullOrEmpty(username))
                DevHubActivityRequest.Failed();
            return (credential, username);
        }

        public async Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken)
        {
            var (credential, username) = await GetCredentialAndUsernameAsync(cancellationToken);
            if (credential == null || string.IsNullOrEmpty(username))
                return Array.Empty<DevHubPullRequest>();

            try
            {
                // Bitbucket cross-repo PR endpoint for the authenticated user
                var url = $"https://api.bitbucket.org/2.0/pullrequests/{Uri.EscapeDataString(username)}?state=OPEN&pagelen=30";
                using var response = await SendGetAsync(credential, url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<DevHubPullRequest>();

                var json = await ReadPageAsync(response, cancellationToken);
                return ParsePullRequests(json, username);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return Array.Empty<DevHubPullRequest>();
            }
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            // Bitbucket issues are per-repository and there is no cross-repo issue search API.
            // We return empty here; repo-specific issues are fetched via GetRepoDetailAsync.
            await Task.CompletedTask;
            return Array.Empty<DevHubIssue>();
        }

        public async Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken)
        {
            // Bitbucket Pipelines are per-repository. Similar to issues, we return empty here
            // and rely on GetRepoDetailAsync for repo-specific pipeline data.
            await Task.CompletedTask;
            return Array.Empty<DevHubCiRun>();
        }

        public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> candidateRemoteUrls, CancellationToken cancellationToken)
        {
            // Bitbucket pull requests are aggregated for the user via the cross-repo PR endpoint
            // in GetUserPullRequestsAsync, so no per-repo activity fetch is wired up here.
            return Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>(Array.Empty<RemoteRepoIdentifier>());
        }

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken)
        {
            if (repo == null)
                return null;

            var pageLen = NormalizeMaxItems(maxItems);

            var credential = await _acquireCredential(cancellationToken);
            if (credential == null)
            {
                DevHubActivityRequest.Failed(unauthorized: true);
                return null;
            }

            try
            {
                var detail = new DevHubRepoDetail
                {
                    RepoIdentifier = repo,
                    FetchedAt = DateTime.UtcNow,
                };

                var repoSlug = $"{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Repo)}";

                // Fetch open PRs
                var prUrl = $"https://api.bitbucket.org/2.0/repositories/{repoSlug}/pullrequests?state=OPEN&pagelen={pageLen}";
                detail.PullRequests = await DevHubActivityRequest.ReadCategoryAsync(detail, DevHubActivityKind.PullRequests, async () =>
                {
                    using var prResponse = await SendGetAsync(credential, prUrl, cancellationToken);
                    if (!prResponse.IsSuccessStatusCode)
                        return new List<DevHubPullRequest>();
                    var prJson = await ReadPageAsync(prResponse, cancellationToken);
                    var username = await _identityCache.GetAsync(credential, "https://api.bitbucket.org/2.0/user",
                        (auth, url, token) => SendGetAsync(auth, url, token), true, cancellationToken, identityProperty: "uuid");
                    return ParseRepoPullRequests(prJson, repo, username);
                }, cancellationToken);

                // Fetch open issues (only if the repo has the issue tracker enabled)
                var issueUrl = $"https://api.bitbucket.org/2.0/repositories/{repoSlug}/issues?q=state%3D%22open%22&pagelen={pageLen}";
                detail.Issues = await DevHubActivityRequest.ReadCategoryAsync(detail, DevHubActivityKind.Issues, async () =>
                {
                    using var issueResponse = await SendGetAsync(credential, issueUrl, cancellationToken);
                    if (!issueResponse.IsSuccessStatusCode)
                        return new List<DevHubIssue>();
                    var issueJson = await ReadPageAsync(issueResponse, cancellationToken);
                    return ParseRepoIssues(issueJson, repo);
                }, cancellationToken);

                // Fetch recent pipelines
                var pipeUrl = $"https://api.bitbucket.org/2.0/repositories/{repoSlug}/pipelines/?sort=-created_on&pagelen={pageLen}";
                detail.CiRuns = await DevHubActivityRequest.ReadCategoryAsync(detail, DevHubActivityKind.CiRuns, async () =>
                {
                    using var pipeResponse = await SendGetAsync(credential, pipeUrl, cancellationToken);
                    if (!pipeResponse.IsSuccessStatusCode)
                        return new List<DevHubCiRun>();
                    var pipeJson = await ReadPageAsync(pipeResponse, cancellationToken);
                    return ParsePipelines(pipeJson, repo);
                }, cancellationToken);

                return detail;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return null;
            }
        }

        public string GetRepoWebUrl(RemoteRepoIdentifier repo) =>
            $"https://bitbucket.org/{repo.Owner}/{repo.Repo}";

        private static async Task<string> ReadPageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var json = await HttpResponseReader.ReadStringAsync(response, cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new JsonException("Bitbucket did not return an activity collection.");
            return json;
        }

        public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) =>
            $"https://bitbucket.org/{repo.Owner}/{repo.Repo}/pull-requests";

        public string GetIssuesWebUrl(RemoteRepoIdentifier repo) =>
            $"https://bitbucket.org/{repo.Owner}/{repo.Repo}/issues";

        internal static string GetUserIdentity(JsonElement user)
        {
            // Nicknames are mutable and not unique. Only legacy payloads fall back to username.
            foreach (var property in new[] { "uuid", "account_id", "username" })
            {
                if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
            }
            return null;
        }

        internal static List<DevHubPullRequest> ParsePullRequests(string json, string currentUser)
        {
            var results = new List<DevHubPullRequest>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("values", out var values))
                    return results;

                foreach (var item in values.EnumerateArray())
                {
                    var pr = ParsePrFromItem(item, currentUser);
                    if (pr != null)
                        results.Add(pr);
                }
            }

            return results;
        }

        private static DevHubPullRequest ParsePrFromItem(JsonElement item, string currentUser)
        {
            var id = item.GetProperty("id").GetInt32();
            var author = item.TryGetProperty("author", out var authorEl)
                && authorEl.TryGetProperty("display_name", out var authorName)
                    ? authorName.GetString()
                    : "Unknown";
            var authorUsername = item.TryGetProperty("author", out var authorEl2)
                    ? GetUserIdentity(authorEl2)
                    : null;

            var repo = ParseRepoFromPrSource(item);

            var sourceBranch = item.TryGetProperty("source", out var src)
                && src.TryGetProperty("branch", out var srcBranch)
                && srcBranch.TryGetProperty("name", out var srcName)
                    ? srcName.GetString()
                    : null;
            var targetBranch = item.TryGetProperty("destination", out var dst)
                && dst.TryGetProperty("branch", out var dstBranch)
                && dstBranch.TryGetProperty("name", out var dstName)
                    ? dstName.GetString()
                    : null;

            var webUrl = item.TryGetProperty("links", out var links)
                && links.TryGetProperty("html", out var html)
                && html.TryGetProperty("href", out var href)
                    ? href.GetString()
                    : null;

            return new DevHubPullRequest
            {
                ProviderName = "Bitbucket",
                RepoIdentifier = repo,
                RepoDisplayName = repo?.DisplayName ?? string.Empty,
                Title = item.GetProperty("title").GetString(),
                Number = $"#{id}",
                NumericId = id,
                Author = author,
                TargetBranch = targetBranch,
                SourceBranch = sourceBranch,
                SourceRepoIdentifier = ParsePrRepository(item, "source"),
                Status = MapBitbucketPrState(item.GetProperty("state").GetString()),
                UpdatedAt = item.GetProperty("updated_on").GetDateTime().ToUniversalTime(),
                CreatedAt = item.GetProperty("created_on").GetDateTime().ToUniversalTime(),
                WebUrl = webUrl,
                IsAuthoredByCurrentUser = !string.IsNullOrEmpty(currentUser) && string.Equals(authorUsername, currentUser, StringComparison.OrdinalIgnoreCase),
            };
        }

        private static int NormalizeMaxItems(int maxItems) => maxItems < 1 ? 1 : (maxItems > 100 ? 100 : maxItems);

        private static List<DevHubPullRequest> ParseRepoPullRequests(string json, RemoteRepoIdentifier repo, string currentUser)
        {
            var results = new List<DevHubPullRequest>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("values", out var values))
                    return results;

                foreach (var item in values.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetInt32();
                    var author = item.TryGetProperty("author", out var authorEl)
                        && authorEl.TryGetProperty("display_name", out var authorName)
                            ? authorName.GetString()
                            : "Unknown";
                    var authorUsername = item.TryGetProperty("author", out var authorEl2)
                            ? GetUserIdentity(authorEl2)
                            : null;

                    var sourceBranch = item.TryGetProperty("source", out var src)
                        && src.TryGetProperty("branch", out var srcBranch)
                        && srcBranch.TryGetProperty("name", out var srcName)
                            ? srcName.GetString()
                            : null;
                    var targetBranch = item.TryGetProperty("destination", out var dst)
                        && dst.TryGetProperty("branch", out var dstBranch)
                        && dstBranch.TryGetProperty("name", out var dstName)
                            ? dstName.GetString()
                            : null;

                    var webUrl = item.TryGetProperty("links", out var links)
                        && links.TryGetProperty("html", out var html)
                        && html.TryGetProperty("href", out var href)
                            ? href.GetString()
                            : null;

                    results.Add(new DevHubPullRequest
                    {
                        ProviderName = "Bitbucket",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Title = item.GetProperty("title").GetString(),
                        Number = $"#{id}",
                        NumericId = id,
                        Author = author,
                        TargetBranch = targetBranch,
                        SourceBranch = sourceBranch,
                        SourceRepoIdentifier = ParsePrRepository(item, "source"),
                        Status = MapBitbucketPrState(item.GetProperty("state").GetString()),
                        UpdatedAt = item.GetProperty("updated_on").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_on").GetDateTime().ToUniversalTime(),
                        WebUrl = webUrl,
                        IsAuthoredByCurrentUser = !string.IsNullOrEmpty(currentUser) && string.Equals(authorUsername, currentUser, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            return results;
        }

        internal static List<DevHubIssue> ParseRepoIssues(string json, RemoteRepoIdentifier repo)
        {
            var results = new List<DevHubIssue>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("values", out var values))
                    return results;

                foreach (var item in values.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetInt32();
                    var reporter = item.TryGetProperty("reporter", out var rep)
                        && rep.TryGetProperty("display_name", out var repName)
                            ? repName.GetString()
                            : "Unknown";
                    var priority = item.TryGetProperty("priority", out var pri) ? pri.GetString() : null;
                    var state = item.TryGetProperty("state", out var st) ? st.GetString() : "open";

                    var webUrl = item.TryGetProperty("links", out var links)
                        && links.TryGetProperty("html", out var html)
                        && html.TryGetProperty("href", out var href)
                            ? href.GetString()
                            : null;

                    results.Add(new DevHubIssue
                    {
                        ProviderName = "Bitbucket",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Title = item.GetProperty("title").GetString(),
                        Number = $"#{id}",
                        NumericId = id,
                        Author = reporter,
                        State = state,
                        Priority = priority,
                        UpdatedAt = item.GetProperty("updated_on").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_on").GetDateTime().ToUniversalTime(),
                        WebUrl = webUrl,
                    });
                }
            }

            return results;
        }

        internal static List<DevHubCiRun> ParsePipelines(string json, RemoteRepoIdentifier repo)
        {
            var results = new List<DevHubCiRun>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("values", out var values))
                    return results;

                foreach (var item in values.EnumerateArray())
                {
                    var state = item.TryGetProperty("state", out var stateEl) ? stateEl : default;
                    var stageName = state.ValueKind != JsonValueKind.Undefined
                        && state.TryGetProperty("name", out var sn) ? sn.GetString() : null;

                    // Result is nested under state.result for completed pipelines
                    string resultName = null;
                    if (state.ValueKind != JsonValueKind.Undefined
                        && state.TryGetProperty("result", out var resultEl)
                        && resultEl.ValueKind == JsonValueKind.Object
                        && resultEl.TryGetProperty("name", out var rn))
                    {
                        resultName = rn.GetString();
                    }

                    var branch = item.TryGetProperty("target", out var target)
                        && target.TryGetProperty("ref_name", out var refName)
                            ? refName.GetString()
                            : null;

                    var buildNumber = item.TryGetProperty("build_number", out var bn) ? bn.GetInt32().ToString() : null;

                    var webUrl = $"https://bitbucket.org/{repo.Owner}/{repo.Repo}/pipelines/results/{buildNumber}";

                    var timestamp = item.TryGetProperty("completed_on", out var completed)
                        && completed.ValueKind != JsonValueKind.Null
                            ? completed.GetDateTime().ToUniversalTime()
                            : item.TryGetProperty("created_on", out var created)
                                ? created.GetDateTime().ToUniversalTime()
                                : DateTime.UtcNow;

                    results.Add(new DevHubCiRun
                    {
                        ProviderName = "Bitbucket",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Name = $"Pipeline #{buildNumber}",
                        Branch = StripRefsPrefix(branch),
                        Status = MapBitbucketPipelineStatus(stageName, resultName),
                        Timestamp = timestamp,
                        Duration = stageName == "COMPLETED" && item.TryGetProperty("duration_in_seconds", out var duration)
                            && duration.ValueKind == JsonValueKind.Number && duration.GetDouble() >= 0
                            ? TimeSpan.FromSeconds(duration.GetDouble()) : (TimeSpan?)null,
                        WebUrl = webUrl,
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Extracts a <see cref="RemoteRepoIdentifier"/> from the nested repository object
        /// in a Bitbucket PR response.
        /// </summary>
        private static RemoteRepoIdentifier ParseRepoFromPrSource(JsonElement prItem)
        {
            return ParsePrRepository(prItem, "destination");
        }

        private static RemoteRepoIdentifier ParsePrRepository(JsonElement prItem, string side)
        {
            if (prItem.TryGetProperty(side, out var dest)
                && dest.TryGetProperty("repository", out var repoEl)
                && repoEl.TryGetProperty("full_name", out var fullName))
            {
                var parts = fullName.GetString()?.Split('/');
                if (parts?.Length == 2)
                {
                    return new RemoteRepoIdentifier("bitbucket.org", parts[0], parts[1]);
                }
            }

            return null;
        }

        internal static string MapBitbucketPrState(string state)
        {
            return state?.ToUpperInvariant() switch
            {
                "OPEN" => "open",
                "MERGED" => "merged",
                "DECLINED" => "closed",
                "SUPERSEDED" => "closed",
                _ => state?.ToLowerInvariant() ?? "open",
            };
        }

        internal static string MapBitbucketPipelineStatus(string stageName, string resultName)
        {
            // Stage names: PENDING, BUILDING, COMPLETED
            if (string.Equals(stageName, "PENDING", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stageName, "BUILDING", StringComparison.OrdinalIgnoreCase))
            {
                return "pending";
            }

            // Result names for COMPLETED stage: SUCCESSFUL, FAILED, STOPPED, EXPIRED
            return resultName?.ToUpperInvariant() switch
            {
                "SUCCESSFUL" => "success",
                "FAILED" or "ERROR" => "failure",
                "STOPPED" => "cancelled",
                "EXPIRED" => "cancelled",
                _ => resultName != null ? "pending" : "pending",
            };
        }

        private static string StripRefsPrefix(string refName)
        {
            if (refName == null) return null;
            if (refName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                return refName.Substring("refs/heads/".Length);
            return refName;
        }
    }
}
