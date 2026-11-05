using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Models.DevHub;
using StartScreen.Services;

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
        private static readonly HttpClient _sharedHttpClient = CreateSharedHttpClient();
        private readonly HttpClient _httpClient;
        private readonly DevHubIdentityCache _identityCache = new DevHubIdentityCache();
        private readonly Func<string, string, CancellationToken, Task<DevHubCredential>> _acquireCredential;
        private readonly Action<string, DevHubCredential> _invalidateCredential;

        public AzureDevOpsDevHubProvider() : this(_sharedHttpClient, DevHubCredentialHelper.GetAzureDevOpsCredentialAsync,
            (host, credential) =>
            {
                if (credential.IsBearer)
                    DevHubCredentialHelper.InvalidateAzureDevOpsCredential(credential);
                else
                    DevHubCredentialHelper.InvalidateCachedCredential(host);
            })
        {
        }

        internal AzureDevOpsDevHubProvider(HttpClient httpClient,
            Func<string, string, CancellationToken, Task<DevHubCredential>> acquireCredential,
            Action<string, DevHubCredential> invalidateCredential)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _acquireCredential = acquireCredential ?? throw new ArgumentNullException(nameof(acquireCredential));
            _invalidateCredential = invalidateCredential ?? throw new ArgumentNullException(nameof(invalidateCredential));
        }

        private static HttpClient CreateSharedHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            client.DefaultRequestHeaders.Add("User-Agent", "StartScreen-VS-Extension");
            return client;
        }

        public string DisplayName => "Azure DevOps";

        public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null)
                throw new ArgumentNullException(nameof(repo));
            var host = GetCredentialHost(repo);
            return DevHubRepoSummaryFetcher.FetchAsync(repo,
                token => _acquireCredential(host, GetBaseUrl(repo), token),
                (credential, url, token) => SendAuthenticatedGetAsync(host, credential, url, token), false, cancellationToken,
                host == AzureDevOpsServerHelper.CloudHost ? "Connect Azure DevOps in Dev Hub settings using your Visual Studio account or an optional PAT." : null,
                _identityCache);
        }

        internal Task<DevHubRepoSummary> GetRepoSummaryAsync(HttpClient client, DevHubCredential credential,
            RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(credential), (_, _) => { })
                .GetRepoSummaryAsync(repo, cancellationToken);
        }

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
            var results = new List<DevHubUser>();
            var credentialHosts = AzureDevOpsServerHelper.GetAllKnownHosts().ToList();
            var profileTargets = new List<(string CredentialHost, string ProfileUrl)>();

            foreach (var host in credentialHosts)
            {
                profileTargets.Add((host, host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase)
                    ? "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1"
                    : $"https://{host.TrimEnd('/')}/_apis/connectionData?api-version=3.0"));
            }

            var organizationUrls = await GetCloudProfileBaseUrlsAsync(cancellationToken);
            foreach (var organizationUrl in organizationUrls)
            {
                var target = (AzureDevOpsServerHelper.CloudHost, $"{organizationUrl.TrimEnd('/')}/_apis/connectionData?api-version=3.0");
                if (!profileTargets.Contains(target))
                    profileTargets.Add(target);
            }

            foreach (var (credentialHost, profileUrl) in profileTargets)
            {
                var user = await GetUserForHostAsync(credentialHost, profileUrl, cancellationToken);
                if (user != null && !results.Any(existing => string.Equals(existing.Host, user.Host, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
                    results.Add(user);
            }

            return results;
        }

        internal async Task<DevHubUser> GetUserForHostAsync(string credentialHost, string profileUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(credentialHost))
                return null;

            try
            {
                var credential = await _acquireCredential(credentialHost, profileUrl, cancellationToken);
                if (credential == null)
                    return null;

                bool isCloud = credentialHost.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase);
                using var response = await SendAuthenticatedGetAsync(credentialHost, credential, profileUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    if (isCloud && profileUrl.Contains("/_apis/profile/profiles/me", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!root.TryGetProperty("id", out var profileId) || profileId.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(profileId.GetString()))
                            throw new JsonException("Azure DevOps did not return an authenticated profile.");

                        return new DevHubUser
                        {
                            Username = root.TryGetProperty("emailAddress", out var email) ? email.GetString() : credential.Username ?? profileId.GetString(),
                            DisplayName = root.TryGetProperty("displayName", out var name) ? name.GetString() : credential.Username ?? profileId.GetString(),
                            AvatarUrl = null,
                            ProviderName = DisplayName,
                            Host = AzureDevOpsServerHelper.CloudHost,
                        };
                    }

                    // Organization-scoped connectionData works for guest tenants and on-premises servers.
                    if (!root.TryGetProperty("authenticatedUser", out var authUser) || authUser.ValueKind != JsonValueKind.Object
                        || !authUser.TryGetProperty("id", out var userId) || userId.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(userId.GetString()) || userId.GetString() == Guid.Empty.ToString())
                        throw new JsonException("Azure DevOps did not return an authenticated identity.");

                    _identityCache.Remember(credential, profileUrl, userId.GetString());
                    string username = credential.Username ?? userId.GetString();
                    string displayName = credential.Username;
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

                    return new DevHubUser
                    {
                        Username = username,
                        DisplayName = displayName ?? username,
                        AvatarUrl = null,
                        ProviderName = DisplayName,
                        Host = credentialHost,
                    };
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                return null;
            }
        }

        internal static IReadOnlyList<string> GetCloudProfileBaseUrls(IEnumerable<string> remoteUrls)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in remoteUrls ?? Array.Empty<string>())
            {
                try
                {
                    var repo = RemoteRepoIdentifier.TryParse(url);
                    if (repo == null || repo.IsAzureDevOpsServer || string.IsNullOrWhiteSpace(repo.BaseUrl)
                        || !AzureDevOpsCredentialProvider.IsCloudUrl(new Uri(repo.BaseUrl)))
                        continue;

                    if (seen.Add(repo.BaseUrl))
                        results.Add(repo.BaseUrl);
                }
                catch (Exception ex)
                {
                    ex.LogAsync().FireAndForget();
                }
            }

            return results;
        }

        private static async Task<IReadOnlyList<string>> GetCloudProfileBaseUrlsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var remoteUrls = await MruService.GetRepositoryRemoteUrlsAsync(cancellationToken);
                return GetCloudProfileBaseUrls(remoteUrls);
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

        public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> candidateRemoteUrls, CancellationToken cancellationToken)
        {
            // Azure DevOps has no cross-project/cross-org activity query, so the dashboard is
            // populated by fetching each known repo individually. Use the candidate remote URLs
            // (the MRU repositories) as the set of repos to look at, keeping the request count
            // bounded instead of enumerating every repo the PAT can see.
            var repos = new List<RemoteRepoIdentifier>();

            if (candidateRemoteUrls != null)
            {
                var seen = new HashSet<RemoteRepoIdentifier>();
                foreach (var url in candidateRemoteUrls)
                {
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    RemoteRepoIdentifier repo;
                    try
                    {
                        repo = ParseRemoteUrl(url);
                    }
                    catch
                    {
                        continue;
                    }

                    // GetRepoDetailAsync needs the project to build the REST URLs.
                    if (repo == null || string.IsNullOrEmpty(repo.Project))
                        continue;

                    if (seen.Add(repo))
                        repos.Add(repo);
                }
            }

            return Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>(repos);
        }

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken)
        {
            if (repo == null || string.IsNullOrEmpty(repo.Project))
                return null;

            var top = NormalizeMaxItems(maxItems);

            var credentialHost = GetCredentialHost(repo);
            try
            {
                var credential = await _acquireCredential(credentialHost, GetBaseUrl(repo), cancellationToken);
                if (credential == null)
                    return null;

                var detail = new DevHubRepoDetail
                {
                    RepoIdentifier = repo,
                    FetchedAt = DateTime.UtcNow,
                };

                var projectUrl = $"{GetBaseUrl(repo)}/{repo.Project}";

                // api-version=5.0 is GA on Azure DevOps Server 2018+ (where modern Git PRs and
                // YAML builds actually exist) and is still accepted by Azure DevOps Services.
                // Newer numbers like 7.1 are preview on older on-prem releases and fail outright.
                var apiVersion = repo.IsAzureDevOpsServer ? "5.0" : "7.1";

                // Fetch PRs
                var prUrl = $"{projectUrl}/_apis/git/repositories/{repo.Repo}/pullrequests?searchCriteria.status=active&$top={top}&api-version={apiVersion}";
                using var prResponse = await SendAuthenticatedGetAsync(credentialHost, credential, prUrl, cancellationToken);
                if (prResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return null;
                if (prResponse.IsSuccessStatusCode)
                {
                    var prJson = await prResponse.Content.ReadAsStringAsync();
                    var identity = await _identityCache.GetAsync(credential,
                        $"{GetBaseUrl(repo).TrimEnd('/')}/_apis/connectionData?api-version=3.0",
                        (auth, url, token) => SendAuthenticatedGetAsync(credentialHost, auth, url, token), false, cancellationToken);
                    detail.PullRequests = ParseAdoPullRequests(prJson, repo, identity);
                }

                // Build queries require the repository ID, not the name from the Git remote.
                var repositoryUrl = $"{projectUrl}/_apis/git/repositories/{Uri.EscapeDataString(repo.Repo)}?api-version={apiVersion}";
                using var repositoryResponse = await SendAuthenticatedGetAsync(credentialHost, credential, repositoryUrl, cancellationToken);
                repositoryResponse.EnsureSuccessStatusCode();
                using var repositoryJson = JsonDocument.Parse(await repositoryResponse.Content.ReadAsStringAsync());
                if (!repositoryJson.RootElement.TryGetProperty("id", out var repositoryId)
                    || repositoryId.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(repositoryId.GetString(), out var id) || id == Guid.Empty)
                    throw new JsonException("Azure DevOps did not return a valid repository ID for the build query.");

                var buildUrl = $"{projectUrl}/_apis/build/builds?repositoryId={id:D}&repositoryType=TfsGit&$top={top}&api-version={apiVersion}";
                using var buildResponse = await SendAuthenticatedGetAsync(credentialHost, credential, buildUrl, cancellationToken);
                if (buildResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return null;
                buildResponse.EnsureSuccessStatusCode();
                var buildJson = await buildResponse.Content.ReadAsStringAsync();
                detail.CiRuns = ParseAdoBuilds(buildJson, repo);

                return detail;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
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

        private async Task<HttpResponseMessage> SendAuthenticatedGetAsync(string host, DevHubCredential credential, string url, CancellationToken cancellationToken)
        {
            if (credential.IsBearer)
            {
                credential = await _acquireCredential(host, url, cancellationToken);
                if (credential == null)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var response = await SendGetAsync(credential, url, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return response;

            DevHubCredential fresh;
            try
            {
                _invalidateCredential(host, credential);
                fresh = await _acquireCredential(host, url, cancellationToken);
            }
            finally
            {
                response.Dispose();
            }
            if (fresh == null)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var retried = await SendGetAsync(fresh, url, cancellationToken);
            if (retried.StatusCode == HttpStatusCode.Unauthorized)
                _invalidateCredential(host, fresh);
            return retried;
        }

        private async Task<HttpResponseMessage> SendGetAsync(DevHubCredential credential, string url, CancellationToken cancellationToken)
        {
            if (credential.IsBearer && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AzureDevOpsCredentialProvider.IsCloudUrl(uri)))
                throw new InvalidOperationException("Azure DevOps Entra tokens can only be sent to Azure DevOps Services over HTTPS.");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = credential.IsBearer
                ? new AuthenticationHeaderValue("Bearer", credential.Token)
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{credential.Token}")));

            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private static int NormalizeMaxItems(int maxItems) => maxItems < 1 ? 1 : (maxItems > 100 ? 100 : maxItems);

        internal static List<DevHubPullRequest> ParseAdoPullRequests(string json, RemoteRepoIdentifier repo, string currentUserId = null)
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
                        IsAuthoredByCurrentUser = !string.IsNullOrEmpty(currentUserId)
                            && createdBy.ValueKind == JsonValueKind.Object
                            && createdBy.TryGetProperty("id", out var authorId)
                            && string.Equals(authorId.GetString(), currentUserId, StringComparison.OrdinalIgnoreCase),
                        IsReviewRequestedForCurrentUser = string.Equals(pr.GetProperty("status").GetString(), "active", StringComparison.OrdinalIgnoreCase)
                            && DevHubRepoSummaryFetcher.IsRequested(pr, currentUserId, false) == true,
                    });
                }
            }

            return results;
        }

        internal static List<DevHubCiRun> ParseAdoBuilds(string json, RemoteRepoIdentifier repo)
        {
            var results = new List<DevHubCiRun>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("value", out var builds))
                    return results;

                foreach (var build in builds.EnumerateArray())
                {
                    var definition = build.TryGetProperty("definition", out var def) && def.ValueKind == JsonValueKind.Object
                        && def.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString()
                        : "Build";

                    var result = build.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null
                        ? r.GetString()
                        : null;
                    if (!build.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(statusElement.GetString()))
                        throw new JsonException("Azure DevOps did not return a valid status for a build.");
                    var status = statusElement.GetString();
                    // Queued builds have not started yet, so both execution timestamps can be absent.
                    var timestamp = build.TryGetProperty("finishTime", out var finish) && finish.ValueKind != JsonValueKind.Null
                        ? finish.GetDateTime().ToUniversalTime()
                        : build.TryGetProperty("startTime", out var start) && start.ValueKind != JsonValueKind.Null
                            ? start.GetDateTime().ToUniversalTime()
                            : build.TryGetProperty("queueTime", out var queued) && queued.ValueKind != JsonValueKind.Null
                                ? queued.GetDateTime().ToUniversalTime()
                                : throw new JsonException("Azure DevOps did not return a finish, start, or queue time for a build.");

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
                        Timestamp = timestamp,
                        WebUrl = build.TryGetProperty("_links", out var links) && links.ValueKind == JsonValueKind.Object
                            && links.TryGetProperty("web", out var web) && web.ValueKind == JsonValueKind.Object
                            && web.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String
                            ? href.GetString()
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
