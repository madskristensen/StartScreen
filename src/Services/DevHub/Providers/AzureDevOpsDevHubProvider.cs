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
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _getRemoteUrls;

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
            Action<string, DevHubCredential> invalidateCredential,
            Func<CancellationToken, Task<IReadOnlyList<string>>> getRemoteUrls = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _acquireCredential = acquireCredential ?? throw new ArgumentNullException(nameof(acquireCredential));
            _invalidateCredential = invalidateCredential ?? throw new ArgumentNullException(nameof(invalidateCredential));
            _getRemoteUrls = getRemoteUrls ?? MruService.GetRepositoryRemoteUrlsAsync;
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
            var results = await GetPersonalProjectActivityAsync(
                (repo, credential, identity, token) => GetPersonalPullRequestsAsync(repo, credential, identity, token), cancellationToken);
            return results.OrderByDescending(pr => pr.UpdatedAt).ToList();
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            var results = await GetPersonalProjectActivityAsync(
                (repo, credential, identity, token) => GetIssuesAsync(repo, credential, identity, 30, null, token), cancellationToken);
            return results.OrderByDescending(issue => issue.UpdatedAt).ToList();
        }

        private async Task<IReadOnlyList<T>> GetPersonalProjectActivityAsync<T>(
            Func<RemoteRepoIdentifier, DevHubCredential, string, CancellationToken, Task<List<T>>> fetch,
            CancellationToken cancellationToken)
        {
            try
            {
                var remotes = await _getRemoteUrls(cancellationToken);
                var repos = await GetActivityReposAsync(remotes, cancellationToken);
                var projects = repos.GroupBy(repo => $"{GetBaseUrl(repo)}/{repo.Project}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList();
                using var semaphore = new SemaphoreSlim(3, 3);
                var results = await Task.WhenAll(projects.Select(async repo =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var host = GetCredentialHost(repo);
                        var credential = await _acquireCredential(host, GetBaseUrl(repo), cancellationToken);
                        if (credential == null)
                            return new List<T>();
                        var identity = await _identityCache.GetAsync(credential,
                            $"{GetBaseUrl(repo).TrimEnd('/')}/_apis/connectionData?api-version=3.0",
                            (auth, url, token) => SendAuthenticatedGetAsync(host, auth, url, token), false, cancellationToken);
                        if (string.IsNullOrEmpty(identity))
                            throw new InvalidOperationException("Azure DevOps personal activity requires an authenticated identity.");
                        return await fetch(repo, credential, identity, cancellationToken);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ex.LogAsync();
                        return new List<T>();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
                return results.SelectMany(items => items).ToList();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                return Array.Empty<T>();
            }
        }

        private async Task<List<DevHubPullRequest>> GetPersonalPullRequestsAsync(RemoteRepoIdentifier project,
            DevHubCredential credential, string identity, CancellationToken cancellationToken, bool repositoryScoped = false)
        {
            var version = project.IsAzureDevOpsServer ? "5.0" : "7.1";
            var projectUrl = $"{GetBaseUrl(project).TrimEnd('/')}/{Uri.EscapeDataString(project.Project)}";
            var repositoryPath = repositoryScoped ? $"repositories/{Uri.EscapeDataString(project.Repo)}/" : string.Empty;
            using var response = await SendAuthenticatedGetAsync(GetCredentialHost(project), credential,
                $"{projectUrl}/_apis/git/{repositoryPath}pullrequests?searchCriteria.status=active&searchCriteria.creatorId={Uri.EscapeDataString(identity)}&$top=30&api-version={version}", cancellationToken);
            response.EnsureSuccessStatusCode();
            return ParseAdoPullRequests(await response.Content.ReadAsStringAsync(), project, identity, projectWide: !repositoryScoped)
                .Where(pr => pr.IsOpen && pr.IsAuthoredByCurrentUser).ToList();
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
                var identity = await _identityCache.GetAsync(credential,
                    $"{GetBaseUrl(repo).TrimEnd('/')}/_apis/connectionData?api-version=3.0",
                    (auth, url, token) => SendAuthenticatedGetAsync(credentialHost, auth, url, token), false, cancellationToken);
                string prJson = null;

                // Fetch PRs
                var prUrl = $"{projectUrl}/_apis/git/repositories/{repo.Repo}/pullrequests?searchCriteria.status=active&$top={top}&api-version={apiVersion}";
                using var prResponse = await SendAuthenticatedGetAsync(credentialHost, credential, prUrl, cancellationToken);
                if (prResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return null;
                prResponse.EnsureSuccessStatusCode();
                if (prResponse.IsSuccessStatusCode)
                {
                    prJson = await prResponse.Content.ReadAsStringAsync();
                    detail.PullRequests = ParseAdoPullRequests(prJson, repo, identity);
                }

                if (!string.IsNullOrEmpty(identity))
                {
                    try
                    {
                        var ownPullRequests = await GetPersonalPullRequestsAsync(repo, credential, identity, cancellationToken, repositoryScoped: true);
                        detail.PullRequests = DevHubService.MergePullRequests(ownPullRequests, detail.PullRequests).ToList();
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ex.LogAsync();
                        detail.ActivityWarning = "Personal pull requests could not be loaded. Refresh to retry.";
                    }
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
                if (prJson != null)
                    ApplyBuildStatuses(detail.PullRequests, prJson, buildJson);
                try
                {
                    detail.Issues = await GetIssuesAsync(repo, credential, identity, top, id.ToString("D"), cancellationToken);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ex.LogAsync();
                    detail.ActivityWarning = string.Join(" ", new[] { detail.ActivityWarning,
                        "Issue activity could not be loaded. Check Work Items read access and refresh to retry." }.Where(message => !string.IsNullOrEmpty(message)));
                }
                cancellationToken.ThrowIfCancellationRequested();

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
            return await SendAuthenticatedAsync(host, credential, url, null, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendAuthenticatedAsync(string host, DevHubCredential credential, string url, string json, CancellationToken cancellationToken)
        {
            if (credential.IsBearer)
            {
                credential = await _acquireCredential(host, url, cancellationToken);
                if (credential == null)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var response = await SendAsync(credential, url, json, cancellationToken);
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

            var retried = await SendAsync(fresh, url, json, cancellationToken);
            if (retried.StatusCode == HttpStatusCode.Unauthorized)
                _invalidateCredential(host, fresh);
            return retried;
        }

        private async Task<HttpResponseMessage> SendAsync(DevHubCredential credential, string url, string json, CancellationToken cancellationToken)
        {
            if (credential.IsBearer && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AzureDevOpsCredentialProvider.IsCloudUrl(uri)))
                throw new InvalidOperationException("Azure DevOps Entra tokens can only be sent to Azure DevOps Services over HTTPS.");

            using var request = new HttpRequestMessage(json == null ? HttpMethod.Get : HttpMethod.Post, url);
            if (json != null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Authorization = credential.IsBearer
                ? new AuthenticationHeaderValue("Bearer", credential.Token)
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{credential.Token}")));

            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private static int NormalizeMaxItems(int maxItems) => maxItems < 1 ? 1 : (maxItems > 100 ? 100 : maxItems);

        private async Task<List<DevHubIssue>> GetIssuesAsync(RemoteRepoIdentifier repo, DevHubCredential credential,
            string identity, int top, string repositoryId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(identity))
                return new List<DevHubIssue>();
            var host = GetCredentialHost(repo);
            var projectUrl = $"{GetBaseUrl(repo).TrimEnd('/')}/{Uri.EscapeDataString(repo.Project)}";
            var version = repo.IsAzureDevOpsServer ? "5.0" : "7.1";
            var query = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project "
                + "AND ([System.CreatedBy] = @Me OR [System.AssignedTo] = @Me) "
                + "AND [System.State] NOT IN ('Closed', 'Done', 'Removed') ORDER BY [System.ChangedDate] DESC";
            using var queryResponse = await SendAuthenticatedAsync(host, credential,
                $"{projectUrl}/_apis/wit/wiql?$top={top}&api-version={version}",
                JsonSerializer.Serialize(new { query }), cancellationToken);
            queryResponse.EnsureSuccessStatusCode();
            using var queryDoc = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
            var ids = queryDoc.RootElement.GetProperty("workItems").EnumerateArray()
                .Take(top).Select(item => item.GetProperty("id").GetInt32()).ToList();
            if (ids.Count == 0)
                return new List<DevHubIssue>();
            using var itemsResponse = await SendAuthenticatedGetAsync(host, credential,
                $"{projectUrl}/_apis/wit/workitems?ids={string.Join(",", ids)}&$expand=all&api-version={version}", cancellationToken);
            itemsResponse.EnsureSuccessStatusCode();
            return ParseAdoIssues(await itemsResponse.Content.ReadAsStringAsync(), repo, identity, repositoryId);
        }

        internal static List<DevHubIssue> ParseAdoIssues(string json, RemoteRepoIdentifier repo,
            string currentUserId, string repositoryId = null)
        {
            using var doc = JsonDocument.Parse(json);
            var results = new List<DevHubIssue>();
            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                // Work items belong to projects, not repos. A scoped view requires an explicit Git link.
                if (repositoryId != null && !HasRepositoryLink(item, repositoryId))
                    continue;
                var fields = item.GetProperty("fields");
                var state = fields.GetProperty("System.State").GetString();
                var closed = string.Equals(state, "Closed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "Done", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "Removed", StringComparison.OrdinalIgnoreCase)
                    || (fields.TryGetProperty("Microsoft.VSTS.Common.ClosedDate", out var closedDate)
                        && closedDate.ValueKind == JsonValueKind.String && closedDate.TryGetDateTime(out var date) && date != DateTime.MinValue);
                var authored = HasIdentity(fields, "System.CreatedBy", currentUserId);
                var assigned = HasIdentity(fields, "System.AssignedTo", currentUserId);
                if (!authored && !assigned)
                    continue;
                var id = item.GetProperty("id").GetInt32();
                results.Add(new DevHubIssue
                {
                    ProviderName = "Azure DevOps",
                    RepoIdentifier = repositoryId != null ? repo : null,
                    RepoDisplayName = repositoryId != null ? repo.DisplayName : $"{repo.Owner}/{repo.Project}",
                    NumericId = id,
                    Number = $"#{id}",
                    Title = fields.GetProperty("System.Title").GetString(),
                    Author = fields.TryGetProperty("System.CreatedBy", out var creator) && creator.ValueKind == JsonValueKind.Object
                        && creator.TryGetProperty("displayName", out var name) ? name.GetString() : null,
                    State = state,
                    IsClosed = closed,
                    IsAuthoredByCurrentUser = authored,
                    IsAssignedToCurrentUser = !closed && assigned,
                    CreatedAt = fields.GetProperty("System.CreatedDate").GetDateTime().ToUniversalTime(),
                    UpdatedAt = fields.GetProperty("System.ChangedDate").GetDateTime().ToUniversalTime(),
                    WebUrl = $"{GetBaseUrl(repo)}/{Uri.EscapeDataString(repo.Project)}/_workitems/edit/{id}",
                });
            }
            return results;
        }

        private static bool HasIdentity(JsonElement fields, string field, string identity)
        {
            return !string.IsNullOrEmpty(identity) && fields.TryGetProperty(field, out var user)
                && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String && string.Equals(id.GetString(), identity, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasRepositoryLink(JsonElement item, string repositoryId)
        {
            if (!item.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var relation in relations.EnumerateArray())
            {
                if (!relation.TryGetProperty("rel", out var kind) || kind.GetString() != "ArtifactLink"
                    || !relation.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                    continue;
                var link = Uri.UnescapeDataString(url.GetString());
                foreach (var prefix in new[] { "vstfs:///Git/PullRequestId/", "vstfs:///Git/Commit/", "vstfs:///Git/Ref/" })
                {
                    if (!link.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parts = link.Substring(prefix.Length).Split('/');
                    if (parts.Length >= 3 && string.Equals(parts[1], repositoryId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        internal static void ApplyBuildStatuses(IReadOnlyList<DevHubPullRequest> pullRequests, string prJson, string buildJson)
        {
            using var prs = JsonDocument.Parse(prJson);
            using var builds = JsonDocument.Parse(buildJson);
            foreach (var pr in pullRequests.Where(item => item.IsOpen && !item.IsDraft && item.IsAuthoredByCurrentUser))
            {
                var metadata = prs.RootElement.GetProperty("value").EnumerateArray()
                    .FirstOrDefault(item => item.GetProperty("pullRequestId").GetInt32() == pr.NumericId);
                if (metadata.ValueKind != JsonValueKind.Object
                    || !metadata.TryGetProperty("lastMergeCommit", out var merge) || merge.ValueKind != JsonValueKind.Object
                    || !merge.TryGetProperty("commitId", out var commit) || commit.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(commit.GetString()))
                    continue;
                // A failed build from an old PR iteration or another branch is not current action metadata.
                var latest = builds.RootElement.GetProperty("value").EnumerateArray()
                    .Where(build => build.TryGetProperty("sourceBranch", out var branch)
                        && branch.GetString() == $"refs/pull/{pr.NumericId}/merge"
                        && build.TryGetProperty("sourceVersion", out var version) && version.GetString() == commit.GetString()
                        && build.TryGetProperty("definition", out var definition) && definition.ValueKind == JsonValueKind.Object
                        && definition.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                    .GroupBy(build => build.GetProperty("definition").GetProperty("id").GetInt32())
                    .Select(group => group.OrderByDescending(build => build.GetProperty("id").GetInt32()).First())
                    .Select(build => MapAdoBuildStatus(build.GetProperty("status").GetString(),
                        build.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null))
                    .ToList();
                pr.CiStatus = latest.Contains("failure") ? "failure" : latest.Contains("pending") ? "pending"
                    : latest.Count > 0 && latest.All(status => status == "success") ? "success" : null;
            }
        }

        internal static List<DevHubPullRequest> ParseAdoPullRequests(string json, RemoteRepoIdentifier repo,
            string currentUserId = null, bool projectWide = false)
        {
            var results = new List<DevHubPullRequest>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("value", out var prs))
                    return results;

                foreach (var pr in prs.EnumerateArray())
                {
                    var prRepo = repo;
                    if (projectWide)
                    {
                        var repositoryName = pr.GetProperty("repository").GetProperty("name").GetString();
                        if (string.IsNullOrWhiteSpace(repositoryName))
                            throw new JsonException("Azure DevOps did not return a repository name for a personal pull request.");
                        prRepo = new RemoteRepoIdentifier(repo.Host, repo.Owner, repositoryName, repo.Project, repo.BaseUrl, repo.IsAzureDevOpsServer);
                    }
                    var id = pr.GetProperty("pullRequestId").GetInt32();
                    var author = pr.TryGetProperty("createdBy", out var createdBy)
                        ? createdBy.GetProperty("displayName").GetString()
                        : "Unknown";

                    var approvalCount = 0;
                    if (pr.TryGetProperty("reviewers", out var reviewers) && reviewers.ValueKind == JsonValueKind.Array)
                        approvalCount = reviewers.EnumerateArray().Count(reviewer => reviewer.TryGetProperty("vote", out var vote)
                            && vote.ValueKind == JsonValueKind.Number && vote.TryGetInt32(out var value) && value > 0);

                    var status = MapAdoPrStatus(pr.GetProperty("status").GetString());
                    if (status == "open" && pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True)
                        status = "draft";
                    if (status == "open" && pr.TryGetProperty("mergeStatus", out var mergeStatus)
                        && mergeStatus.ValueKind == JsonValueKind.String && mergeStatus.GetString() == "conflicts")
                        status = "conflicts";

                    results.Add(new DevHubPullRequest
                    {
                        ProviderName = "Azure DevOps",
                        RepoIdentifier = prRepo,
                        RepoDisplayName = prRepo.DisplayName,
                        Title = pr.GetProperty("title").GetString(),
                        Number = $"!{id}",
                        NumericId = id,
                        Author = author,
                        TargetBranch = StripRefsPrefix(pr.GetProperty("targetRefName").GetString()),
                        SourceBranch = StripRefsPrefix(pr.GetProperty("sourceRefName").GetString()),
                        Status = status,
                        UpdatedAt = pr.TryGetProperty("closedDate", out var closed) && closed.ValueKind != JsonValueKind.Null
                            && closed.GetDateTime() != DateTime.MinValue
                            ? closed.GetDateTime().ToUniversalTime()
                            : pr.GetProperty("creationDate").GetDateTime().ToUniversalTime(),
                        CreatedAt = pr.GetProperty("creationDate").GetDateTime().ToUniversalTime(),
                        WebUrl = $"{GetBaseUrl(prRepo)}/{Uri.EscapeDataString(prRepo.Project)}/_git/{Uri.EscapeDataString(prRepo.Repo)}/pullrequest/{id}",
                        ApprovalCount = approvalCount,
                        HasChangesRequested = (status == "open" || status == "conflicts" || status == "draft")
                            && reviewers.ValueKind == JsonValueKind.Array
                            && reviewers.EnumerateArray().Any(reviewer => reviewer.TryGetProperty("vote", out var vote)
                                && vote.ValueKind == JsonValueKind.Number && vote.TryGetInt32(out var value) && value == -10),
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
                _ => status,
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
