using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub.Providers
{
    /// <summary>
    /// Azure DevOps provider for the Dev Hub.
    /// Fetches PRs, work items, and build status from the ADO REST API.
    /// </summary>
    internal sealed class AzureDevOpsDevHubProvider : IDevHubProvider
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
            return client;
        }

        public string DisplayName => "Azure DevOps";

        public bool CanHandle(string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return false;

            // Cloud / legacy / SSH heuristics
            if (remoteUrl.IndexOf("dev.azure.com", StringComparison.OrdinalIgnoreCase) >= 0
                || remoteUrl.IndexOf(".visualstudio.com", StringComparison.OrdinalIgnoreCase) >= 0
                || remoteUrl.IndexOf("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // On-premises Azure DevOps Server: any URL whose path contains a "/_git/" segment.
            // The "/_git/" path is unique to ADO; using it lets us claim arbitrary on-prem hosts
            // without requiring the user to register them up front.
            if (remoteUrl.IndexOf("/_git/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl)
        {
            var parsed = RemoteRepoIdentifier.TryParse(remoteUrl);
            if (parsed == null)
                return null;

            // Accept cloud (Host=dev.azure.com) and on-prem servers (IsAzureDevOpsServer).
            if (parsed.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) || parsed.IsAzureDevOpsServer)
                return parsed;

            return null;
        }

        /// <summary>
        /// Returns the credential-storage host for the given identifier. For cloud and legacy
        /// visualstudio.com URLs this is "dev.azure.com"; for on-premises servers it is the
        /// actual server host.
        /// </summary>
        internal static string GetCredentialHost(RemoteRepoIdentifier repo)
        {
            return repo != null && repo.IsAzureDevOpsServer
                ? repo.Host
                : "dev.azure.com";
        }

        /// <summary>
        /// Returns the collection-level base URL for the identifier, falling back to the
        /// cloud format when <see cref="RemoteRepoIdentifier.BaseUrl"/> is not set (e.g. the
        /// identifier was constructed manually in tests).
        /// </summary>
        private static string GetBaseUrl(RemoteRepoIdentifier repo)
        {
            return !string.IsNullOrEmpty(repo.BaseUrl)
                ? repo.BaseUrl
                : $"https://dev.azure.com/{repo.Owner}";
        }

        public async Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken)
        {
            // Returns the first authenticated host (cloud preferred) so legacy callers that
            // only need "is the user signed in to anything ADO" continue to work.
            var users = await GetAuthenticatedUsersAsync(cancellationToken);
            return users.Count > 0 ? users[0] : null;
        }

        public async Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
        {
            var hosts = AzureDevOpsServerHelper.GetAllKnownHosts();
            var results = new List<DevHubUser>();

            foreach (var host in hosts)
            {
                var user = await GetUserForHostAsync(host, cancellationToken);
                if (user != null)
                    results.Add(user);
            }

            return results;
        }

        private async Task<DevHubUser> GetUserForHostAsync(string host, CancellationToken cancellationToken)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync(host, cancellationToken);
            if (credential == null)
                return null;

            try
            {
                bool isCloud = host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase);

                // Cloud: SPS profile endpoint. On-prem: connectionData on the server itself,
                // which works against any collection URL we can probe.
                string profileUrl = isCloud
                    ? "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1"
                    : $"https://{host}/_apis/connectionData?api-version=6.0";

                var response = await SendGetAsync(credential, profileUrl, cancellationToken);

                // Token expired - invalidate cache and retry once with a fresh credential.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    DevHubCredentialHelper.InvalidateCachedCredential(host);
                    credential = await DevHubCredentialHelper.GetCredentialAsync(host, cancellationToken);
                    if (credential == null)
                        return null;

                    response = await SendGetAsync(credential, profileUrl, cancellationToken);
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    if (isCloud)
                    {
                        return new DevHubUser
                        {
                            Username = root.TryGetProperty("emailAddress", out var email) ? email.GetString() : credential.Username,
                            DisplayName = root.TryGetProperty("displayName", out var name) ? name.GetString() : credential.Username,
                            AvatarUrl = null,
                            ProviderName = DisplayName,
                            Host = AzureDevOpsServerHelper.CloudHost,
                        };
                    }

                    // On-prem connectionData payload: { authenticatedUser: { providerDisplayName, customDisplayName, properties: { Account: { $value } } } }
                    string username = credential.Username;
                    string displayName = credential.Username;
                    if (root.TryGetProperty("authenticatedUser", out var authUser))
                    {
                        if (authUser.TryGetProperty("providerDisplayName", out var pd) && pd.ValueKind == JsonValueKind.String)
                            displayName = pd.GetString();
                        if (authUser.TryGetProperty("customDisplayName", out var cd) && cd.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cd.GetString()))
                            displayName = cd.GetString();
                        if (authUser.TryGetProperty("properties", out var props)
                            && props.TryGetProperty("Account", out var acct)
                            && acct.TryGetProperty("$value", out var acctVal)
                            && acctVal.ValueKind == JsonValueKind.String)
                        {
                            username = acctVal.GetString();
                        }
                    }

                    return new DevHubUser
                    {
                        Username = username,
                        DisplayName = displayName,
                        AvatarUrl = null,
                        ProviderName = DisplayName,
                        Host = host,
                    };
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        public async Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken)
        {
            // Cross-org/cross-collection PR search isn't supported by ADO; the per-repo
            // detail view (driven by GetRepoDetailAsync) covers the common case.
            await Task.CompletedTask;
            return Array.Empty<DevHubPullRequest>();
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            // Same limitation as PRs - WIQL queries are scoped to a project.
            await Task.CompletedTask;
            return Array.Empty<DevHubIssue>();
        }

        public async Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            return Array.Empty<DevHubCiRun>();
        }

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null || string.IsNullOrEmpty(repo.Project))
                return null;

            var credentialHost = GetCredentialHost(repo);
            var credential = await DevHubCredentialHelper.GetCredentialAsync(credentialHost, cancellationToken);
            if (credential == null)
                return null;

            try
            {
                var detail = new DevHubRepoDetail
                {
                    RepoIdentifier = repo,
                    FetchedAt = DateTime.UtcNow,
                };

                var projectUrl = $"{GetBaseUrl(repo)}/{repo.Project}";

                // Fetch PRs
                var prUrl = $"{projectUrl}/_apis/git/repositories/{repo.Repo}/pullrequests?searchCriteria.status=active&$top=10&api-version=7.1";
                var prResponse = await SendGetAsync(credential, prUrl, cancellationToken);
                if (prResponse.IsSuccessStatusCode)
                {
                    var prJson = await prResponse.Content.ReadAsStringAsync();
                    detail.PullRequests = ParseAdoPullRequests(prJson, repo);
                }

                // Fetch recent builds
                var buildUrl = $"{projectUrl}/_apis/build/builds?repositoryId={repo.Repo}&repositoryType=TfsGit&$top=5&api-version=7.1";
                var buildResponse = await SendGetAsync(credential, buildUrl, cancellationToken);
                if (buildResponse.IsSuccessStatusCode)
                {
                    var buildJson = await buildResponse.Content.ReadAsStringAsync();
                    detail.CiRuns = ParseAdoBuilds(buildJson, repo);
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
            $"{GetBaseUrl(repo)}/{repo.Project}/_git/{repo.Repo}";

        public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) =>
            $"{GetBaseUrl(repo)}/{repo.Project}/_git/{repo.Repo}/pullrequests";

        public string GetIssuesWebUrl(RemoteRepoIdentifier repo) =>
            $"{GetBaseUrl(repo)}/{repo.Project}/_workitems";

        private static Task<HttpResponseMessage> SendGetAsync(DevHubCredential credential, string url, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // ADO uses Basic auth with PAT: base64("":{token}) or base64({user}:{token})
            var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{credential.Token}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {authValue}");

            return s_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private static List<DevHubPullRequest> ParseAdoPullRequests(string json, RemoteRepoIdentifier repo)
        {
            var results = new List<DevHubPullRequest>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("value", out var prs))
                    return results;

                foreach (var pr in prs.EnumerateArray())
                {
                    var id = pr.GetProperty("pullRequestId").GetInt32();
                    var author = pr.TryGetProperty("createdBy", out var createdBy)
                        ? createdBy.GetProperty("displayName").GetString()
                        : "Unknown";

                    var reviewerCount = 0;
                    if (pr.TryGetProperty("reviewers", out var reviewers) && reviewers.ValueKind == JsonValueKind.Array)
                        reviewerCount = reviewers.GetArrayLength();

                    results.Add(new DevHubPullRequest
                    {
                        ProviderName = "Azure DevOps",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Title = pr.GetProperty("title").GetString(),
                        Number = $"!{id}",
                        NumericId = id,
                        Author = author,
                        TargetBranch = StripRefsPrefix(pr.GetProperty("targetRefName").GetString()),
                        SourceBranch = StripRefsPrefix(pr.GetProperty("sourceRefName").GetString()),
                        Status = MapAdoPrStatus(pr.GetProperty("status").GetString()),
                        UpdatedAt = pr.TryGetProperty("closedDate", out var closed) && closed.ValueKind != JsonValueKind.Null
                            ? closed.GetDateTime().ToUniversalTime()
                            : pr.GetProperty("creationDate").GetDateTime().ToUniversalTime(),
                        CreatedAt = pr.GetProperty("creationDate").GetDateTime().ToUniversalTime(),
                        WebUrl = $"{GetBaseUrl(repo)}/{repo.Project}/_git/{repo.Repo}/pullrequest/{id}",
                        ApprovalCount = reviewerCount,
                    });
                }
            }

            return results;
        }

        private static List<DevHubCiRun> ParseAdoBuilds(string json, RemoteRepoIdentifier repo)
        {
            var results = new List<DevHubCiRun>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("value", out var builds))
                    return results;

                foreach (var build in builds.EnumerateArray())
                {
                    var definition = build.TryGetProperty("definition", out var def)
                        ? def.GetProperty("name").GetString()
                        : "Build";

                    var result = build.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null
                        ? r.GetString()
                        : null;
                    var status = build.GetProperty("status").GetString();

                    results.Add(new DevHubCiRun
                    {
                        ProviderName = "Azure DevOps",
                        RepoIdentifier = repo,
                        RepoDisplayName = repo.DisplayName,
                        Name = definition,
                        Branch = build.TryGetProperty("sourceBranch", out var branch)
                            ? StripRefsPrefix(branch.GetString())
                            : null,
                        Status = MapAdoBuildStatus(status, result),
                        Timestamp = build.TryGetProperty("finishTime", out var finish) && finish.ValueKind != JsonValueKind.Null
                            ? finish.GetDateTime().ToUniversalTime()
                            : build.GetProperty("startTime").GetDateTime().ToUniversalTime(),
                        WebUrl = build.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var web)
                            ? web.GetProperty("href").GetString()
                            : $"{GetBaseUrl(repo)}/{repo.Project}/_build",
                    });
                }
            }

            return results;
        }

        private static string StripRefsPrefix(string refName)
        {
            if (refName == null) return null;
            if (refName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                return refName.Substring("refs/heads/".Length);
            return refName;
        }

        private static string MapAdoPrStatus(string status)
        {
            return status switch
            {
                "active" => "open",
                "completed" => "merged",
                "abandoned" => "closed",
                _ => status ?? "open",
            };
        }

        private static string MapAdoBuildStatus(string status, string result)
        {
            if (status == "inProgress" || status == "notStarted")
                return "pending";

            return result switch
            {
                "succeeded" => "success",
                "failed" => "failure",
                "canceled" => "cancelled",
                "partiallySucceeded" => "success",
                _ => result ?? "pending",
            };
        }
    }
}
