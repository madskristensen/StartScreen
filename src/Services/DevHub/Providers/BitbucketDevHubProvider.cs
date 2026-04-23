using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub.Providers
{
    /// <summary>
    /// Bitbucket Cloud provider for the Dev Hub.
    /// Fetches PRs, issues, and pipeline status from the Bitbucket REST API v2.
    /// </summary>
    internal sealed class BitbucketDevHubProvider : IDevHubProvider
    {
        // Shared HttpClient: reusing the connection pool across requests avoids repeated DNS,
        // TCP, and TLS handshakes, which are a significant chunk of the Dev Hub cold-start cost.
        // The Authorization header is per-request because the credential token can change at runtime
        // and DefaultRequestHeaders is not safe to mutate while parallel requests are in flight.
        private static readonly HttpClient s_httpClient = CreateSharedHttpClient();

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

        private static Task<HttpResponseMessage> SendGetAsync(DevHubCredential credential, string url, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.Token}");
            return s_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private string _cachedUsername;

        public string DisplayName => "Bitbucket";

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
            var credential = await DevHubCredentialHelper.GetCredentialAsync("bitbucket.org", cancellationToken);
            if (credential == null)
                return null;

            try
            {
                var response = await SendGetAsync(credential, "https://api.bitbucket.org/2.0/user", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
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

                    _cachedUsername = username;

                    return new DevHubUser
                    {
                        Username = username ?? credential.Username,
                        DisplayName = displayName ?? credential.Username,
                        AvatarUrl = avatarUrl,
                        ProviderName = DisplayName,
                        Host = "bitbucket.org",
                    };
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        /// <summary>
        /// Gets the cached Bitbucket username, or fetches it from the API if not yet cached.
        /// </summary>
        private async Task<(DevHubCredential credential, string username)> GetCredentialAndUsernameAsync(CancellationToken cancellationToken)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync("bitbucket.org", cancellationToken);
            if (credential == null)
                return (null, null);

            if (_cachedUsername != null)
                return (credential, _cachedUsername);

            var response = await SendGetAsync(credential, "https://api.bitbucket.org/2.0/user", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (credential, null);

            var json = await response.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(json))
            {
                _cachedUsername = doc.RootElement.TryGetProperty("username", out var uname)
                    ? uname.GetString()
                    : null;
                return (credential, _cachedUsername);
            }
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
                var response = await SendGetAsync(credential, url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<DevHubPullRequest>();

                var json = await response.Content.ReadAsStringAsync();
                return ParsePullRequests(json, username);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
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

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null)
                return null;

            var credential = await DevHubCredentialHelper.GetCredentialAsync("bitbucket.org", cancellationToken);
            if (credential == null)
                return null;

            try
            {
                var detail = new DevHubRepoDetail
                {
                    RepoIdentifier = repo,
                    FetchedAt = DateTime.UtcNow,
                };

                var repoSlug = $"{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Repo)}";

                // Fetch open PRs
                var prUrl = $"https://api.bitbucket.org/2.0/repositories/{repoSlug}/pullrequests?state=OPEN&pagelen=10";
                var prResponse = await SendGetAsync(credential, prUrl, cancellationToken);
                if (prResponse.IsSuccessStatusCode)
                {
                    var prJson = await prResponse.Content.ReadAsStringAsync();
                    detail.PullRequests = ParseRepoPullRequests(prJson, repo, credential.Username);
                }

                // Fetch open issues (only if the repo has the issue tracker enabled)
                var issueUrl = $"https://api.bitbucket.org/2.0/repositories/{repoSlug}/issues?q=state%3D%22open%22&pagelen=10";
                var issueResponse = await SendGetAsync(credential, issueUrl, cancellationToken);
                if (issueResponse.IsSuccessStatusCode)
                {
                    var issueJson = await issueResponse.Content.ReadAsStringAsync();
                    detail.Issues = ParseRepoIssues(issueJson, repo);
                }

                // Fetch recent pipelines
                var pipeUrl = $"https://api.bitbucket.org/2.0/repositories/{repoSlug}/pipelines/?sort=-created_on&pagelen=5";
                var pipeResponse = await SendGetAsync(credential, pipeUrl, cancellationToken);
                if (pipeResponse.IsSuccessStatusCode)
                {
                    var pipeJson = await pipeResponse.Content.ReadAsStringAsync();
                    detail.CiRuns = ParsePipelines(pipeJson, repo);
                }

                return detail;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        public string GetRepoWebUrl(RemoteRepoIdentifier repo) =>
            $"https://bitbucket.org/{repo.Owner}/{repo.Repo}";

        public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) =>
            $"https://bitbucket.org/{repo.Owner}/{repo.Repo}/pull-requests";

        public string GetIssuesWebUrl(RemoteRepoIdentifier repo) =>
            $"https://bitbucket.org/{repo.Owner}/{repo.Repo}/issues";

        private static List<DevHubPullRequest> ParsePullRequests(string json, string currentUser)
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
                && authorEl2.TryGetProperty("username", out var authorUname)
                    ? authorUname.GetString()
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
                Status = MapBitbucketPrState(item.GetProperty("state").GetString()),
                UpdatedAt = item.GetProperty("updated_on").GetDateTime().ToUniversalTime(),
                CreatedAt = item.GetProperty("created_on").GetDateTime().ToUniversalTime(),
                WebUrl = webUrl,
                IsAuthoredByCurrentUser = string.Equals(authorUsername, currentUser, StringComparison.OrdinalIgnoreCase),
            };
        }

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
                        && authorEl2.TryGetProperty("username", out var authorUname)
                            ? authorUname.GetString()
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
                        Status = MapBitbucketPrState(item.GetProperty("state").GetString()),
                        UpdatedAt = item.GetProperty("updated_on").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_on").GetDateTime().ToUniversalTime(),
                        WebUrl = webUrl,
                        IsAuthoredByCurrentUser = string.Equals(authorUsername, currentUser, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            return results;
        }

        private static List<DevHubIssue> ParseRepoIssues(string json, RemoteRepoIdentifier repo)
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

        private static List<DevHubCiRun> ParsePipelines(string json, RemoteRepoIdentifier repo)
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
            if (prItem.TryGetProperty("destination", out var dest)
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
