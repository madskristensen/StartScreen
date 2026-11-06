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
using StartScreen.Helpers;
using StartScreen.Models.DevHub;
using StartScreen.Services;

namespace StartScreen.Services.DevHub.Providers
{
    /// <summary>
    /// Azure DevOps provider for the Dev Hub.
    /// Fetches PRs, work items, and build status from the ADO REST API.
    /// </summary>
    internal sealed class AzureDevOpsDevHubProvider : IDevHubProvider, IDevHubActivityProvider, IDevHubActivitySource, IDevHubRefreshProvider
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
        private readonly Func<IReadOnlyList<string>> _getCredentialHosts;

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
            Func<CancellationToken, Task<IReadOnlyList<string>>> getRemoteUrls = null,
            Func<IReadOnlyList<string>> getCredentialHosts = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _acquireCredential = acquireCredential ?? throw new ArgumentNullException(nameof(acquireCredential));
            _invalidateCredential = invalidateCredential ?? throw new ArgumentNullException(nameof(invalidateCredential));
            _getRemoteUrls = getRemoteUrls ?? MruService.GetRepositoryRemoteUrlsAsync;
            _getCredentialHosts = getCredentialHosts ?? AzureDevOpsServerHelper.GetAllKnownHosts;
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

        public Task<DevHubActivitySnapshot> FetchActivityAsync(DevHubActivityKind kind, DevHubActivityOptions options,
            RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            return DevHubActivityRequest.FetchAsync(this, kind, options, repo, cancellationToken);
        }

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
        /// matching configured endpoint key (including port/path), or the server authority.
        /// </summary>
        internal string GetCredentialHost(RemoteRepoIdentifier repo)
        {
            if (repo == null || !repo.IsAzureDevOpsServer)
                return AzureDevOpsServerHelper.CloudHost;
            var repositoryUri = AzureDevOpsServerHelper.GetServerUri(repo.BaseUrl);
            if (repositoryUri == null)
                return repo.Host;
            // Settings store PATs under the entire persisted endpoint, including virtual directories.
            // Match URI authorities and path boundaries, never hostname substrings or sibling ports.
            return _getCredentialHosts().Select(host => (Host: host, Uri: AzureDevOpsServerHelper.GetServerUri(host)))
                .Where(entry => entry.Uri != null && entry.Uri.Scheme == repositoryUri.Scheme
                    && string.Equals(entry.Uri.Authority, repositoryUri.Authority, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(repositoryUri.AbsolutePath.TrimEnd('/'), entry.Uri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                        || repositoryUri.AbsolutePath.StartsWith(entry.Uri.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(entry => entry.Uri.AbsolutePath.Length).Select(entry => entry.Host).FirstOrDefault()
                ?? repositoryUri.Authority;
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

        public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
        {
            return GetAuthenticatedUsersAsync(null, cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(DevHubRefreshContext context, CancellationToken cancellationToken)
        {
            var results = new List<DevHubUser>();
            var credentialHosts = _getCredentialHosts();
            var profileTargets = new List<(string CredentialHost, string ProfileUrl)>();

            foreach (var host in credentialHosts)
            {
                if (host.Equals(AzureDevOpsServerHelper.CloudHost, StringComparison.OrdinalIgnoreCase))
                    profileTargets.Add((host, "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1"));
                else
                {
                    var server = AzureDevOpsServerHelper.GetServerUri(host);
                    if (server != null)
                        profileTargets.Add((host, GetConnectionDataUrl(server.AbsoluteUri)));
                }
            }

            var organizationUrls = await GetCloudProfileBaseUrlsAsync(context, cancellationToken);
            foreach (var organizationUrl in organizationUrls)
            {
                var target = (AzureDevOpsServerHelper.CloudHost, GetConnectionDataUrl(organizationUrl));
                if (!profileTargets.Contains(target))
                    profileTargets.Add(target);
            }

            var attempts = new List<(string Host, DevHubActivityResult<DevHubUser[]> Result)>();
            foreach (var (credentialHost, profileUrl) in profileTargets)
            {
                var attempt = await DevHubActivityRequest.RunAsync(async () =>
                {
                    var authenticatedUser = await GetUserForHostAsync(credentialHost, profileUrl, cancellationToken);
                    return authenticatedUser == null ? Array.Empty<DevHubUser>() : new[] { authenticatedUser };
                }, users => users.Length > 0, cancellationToken);
                attempts.Add((credentialHost, attempt));
                var user = attempt.Value?.FirstOrDefault();
                if (user != null && !results.Any(existing => string.Equals(existing.Host, user.Host, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
                    results.Add(user);
            }

            foreach (var host in attempts.GroupBy(attempt => attempt.Host, StringComparer.OrdinalIgnoreCase))
            {
                if (!host.Any(attempt => attempt.Result.Value?.Length > 0)
                    && host.Any(attempt => attempt.Result.Status != DevHubActivityStatus.Success))
                {
                    DevHubActivityRequest.Failed(unauthorized: host.Any(attempt => attempt.Result.Status == DevHubActivityStatus.Unauthorized));
                    foreach (var attempt in host)
                        DevHubActivityRequest.Warning(attempt.Result.Message);
                }
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

                var json = await HttpResponseReader.ReadStringAsync(response, cancellationToken);
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
                            IdentityId = profileId.GetString(),
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
                        IdentityId = userId.GetString(),
                        DisplayName = displayName ?? username,
                        AvatarUrl = null,
                        ProviderName = DisplayName,
                        Host = isCloud ? AzureDevOpsServerHelper.CloudHost : new Uri(profileUrl).Host,
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

        private async Task<IReadOnlyList<string>> GetCloudProfileBaseUrlsAsync(DevHubRefreshContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (context != null && context.Status != DevHubActivityStatus.Success)
                    DevHubActivityRequest.Failed();
                var remoteUrls = context?.RemoteUrls ?? await _getRemoteUrls(cancellationToken);
                return GetCloudProfileBaseUrls(remoteUrls);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return Array.Empty<string>();
            }
        }

        public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken)
        {
            return GetUserPullRequestsAsync(new DevHubActivityOptions(), cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            var results = await GetPersonalProjectActivityAsync(
                async (repo, credential, identity, token) =>
                {
                    var items = await GetPersonalPullRequestsAsync(repo, credential, identity, options, token);
                    await EnrichPullRequestsAsync(items, credential, token);
                    return items;
                }, options, cancellationToken);
            return results.OrderByDescending(pr => pr.IsOpen).ThenByDescending(pr => pr.UpdatedAt).ToList();
        }

        public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            return GetUserIssuesAsync(new DevHubActivityOptions(), cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            var results = await GetPersonalProjectActivityAsync(
                (repo, credential, identity, token) => GetIssuesAsync(repo, credential, identity, options, null, token), options, cancellationToken);
            return results.OrderBy(issue => issue.IsClosed).ThenByDescending(issue => issue.UpdatedAt).ToList();
        }

        private async Task<IReadOnlyList<T>> GetPersonalProjectActivityAsync<T>(
            Func<RemoteRepoIdentifier, DevHubCredential, string, CancellationToken, Task<List<T>>> fetch,
            DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            try
            {
                if (options.RefreshContext != null && options.RefreshContext.Status != DevHubActivityStatus.Success)
                    DevHubActivityRequest.Failed();
                var remotes = options.RefreshContext?.RemoteUrls ?? await _getRemoteUrls(cancellationToken);
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
                        {
                            DevHubActivityRequest.Failed(unauthorized: true);
                            return new List<T>();
                        }
                        var identity = await _identityCache.GetAsync(credential,
                            GetConnectionDataUrl(GetBaseUrl(repo)),
                            (auth, url, token) => SendAuthenticatedGetAsync(host, auth, url, token), false, cancellationToken);
                        if (string.IsNullOrEmpty(identity))
                            throw new InvalidOperationException("Azure DevOps personal activity requires an authenticated identity.");
                        return await fetch(repo, credential, identity, cancellationToken);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
                    {
                        await ex.LogAsync();
                        DevHubActivityRequest.Failed();
                        cancellationToken.ThrowIfCancellationRequested();
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
                DevHubActivityRequest.Failed();
                return Array.Empty<T>();
            }
        }

        private async Task<List<DevHubPullRequest>> GetPersonalPullRequestsAsync(RemoteRepoIdentifier project,
            DevHubCredential credential, string identity, DevHubActivityOptions options, CancellationToken cancellationToken, bool repositoryScoped = false)
        {
            var repositoryPath = repositoryScoped ? $"repositories/{Uri.EscapeDataString(project.Repo)}/" : string.Empty;
            var url = $"{GetProjectUrl(project)}/_apis/git/{repositoryPath}pullrequests";
            var authored = await OptionalAsync(() => GetPullRequestFeedAsync(project, credential, identity, url,
                $"searchCriteria.status=active&searchCriteria.creatorId={Uri.EscapeDataString(identity)}", options.MaxItems, !repositoryScoped, cancellationToken), cancellationToken);
            var reviewed = await OptionalAsync(() => GetPullRequestFeedAsync(project, credential, identity, url,
                $"searchCriteria.status=active&searchCriteria.reviewerId={Uri.EscapeDataString(identity)}", options.MaxItems, !repositoryScoped, cancellationToken), cancellationToken);
            var results = (authored ?? new List<DevHubPullRequest>()).Concat(reviewed ?? new List<DevHubPullRequest>())
                .Where(pr => pr.IsOpen && (pr.IsAuthoredByCurrentUser || pr.IsReviewRequestedForCurrentUser))
                .GroupBy(pr => pr.WebUrl, StringComparer.OrdinalIgnoreCase).Select(group =>
                {
                    var pr = group.First();
                    pr.IsAuthoredByCurrentUser = group.Any(item => item.IsAuthoredByCurrentUser);
                    pr.IsReviewRequestedForCurrentUser = group.Any(item => item.IsReviewRequestedForCurrentUser);
                    return pr;
                }).ToList();
            if (options.IncludeCompleted)
            {
                var completed = await OptionalAsync(() => GetPullRequestFeedAsync(project, credential, identity, url,
                    $"searchCriteria.status=completed&searchCriteria.creatorId={Uri.EscapeDataString(identity)}&searchCriteria.queryTimeRangeType=closed&searchCriteria.minTime={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-14).ToString("o"))}",
                    options.MaxItems, !repositoryScoped, cancellationToken), cancellationToken);
                results.AddRange((completed ?? new List<DevHubPullRequest>()).Where(pr => pr.IsMerged && pr.IsAuthoredByCurrentUser
                    && pr.UpdatedAt >= DateTime.UtcNow.AddDays(-14)));
            }
            return results.OrderByDescending(pr => pr.IsOpen).ThenByDescending(pr => pr.UpdatedAt).ToList();
        }

        public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken)
        {
            return GetUserCiRunsAsync(new DevHubActivityOptions(), cancellationToken);
        }

        public async Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            var results = await GetPersonalProjectActivityAsync(async (repo, credential, identity, token) =>
            {
                var json = await GetPagedValuesAsync(repo, credential,
                    $"{GetProjectUrl(repo)}/_apis/build/builds?requestedFor={Uri.EscapeDataString(identity)}&queryOrder=queueTimeDescending",
                    options.MaxItems, true, token);
                var runs = ParseAdoBuilds(json, repo, identity, projectWide: true).Where(run => run.IsTriggeredByCurrentUser).ToList();
                await EnrichBuildFailuresAsync(runs, repo, credential, json, token);
                return runs;
            }, options, cancellationToken);
            return results.OrderByDescending(run => run.Timestamp).ToList();
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
                    catch (Exception ex)
                    {
                        ex.LogAsync().FireAndForget();
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

        public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken)
        {
            return GetRepoDetailAsync(repo, new DevHubActivityOptions(NormalizeMaxItems(maxItems)), cancellationToken);
        }

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, DevHubActivityOptions options, CancellationToken cancellationToken)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (repo == null || string.IsNullOrEmpty(repo.Project))
                return null;

            var top = options.MaxItems;

            var credentialHost = GetCredentialHost(repo);
            try
            {
                var credential = await _acquireCredential(credentialHost, GetBaseUrl(repo), cancellationToken);
                if (credential == null)
                {
                    DevHubActivityRequest.Failed(unauthorized: true);
                    return null;
                }

                var detail = new DevHubRepoDetail
                {
                    RepoIdentifier = repo,
                    FetchedAt = DateTime.UtcNow,
                };

                var projectUrl = GetProjectUrl(repo);

                // api-version=5.0 is GA on Azure DevOps Server 2018+ (where modern Git PRs and
                // YAML builds actually exist) and is still accepted by Azure DevOps Services.
                // Newer numbers like 7.1 are preview on older on-prem releases and fail outright.
                var apiVersion = repo.IsAzureDevOpsServer ? "5.0" : "7.1";
                var identity = await _identityCache.GetAsync(credential,
                    GetConnectionDataUrl(GetBaseUrl(repo)),
                    (auth, url, token) => SendAuthenticatedGetAsync(credentialHost, auth, url, token), false, cancellationToken);
                string prJson = null;

                // Fetch PRs
                var prUrl = $"{projectUrl}/_apis/git/repositories/{Uri.EscapeDataString(repo.Repo)}/pullrequests?searchCriteria.status=active";
                detail.PullRequests = await DevHubActivityRequest.ReadCategoryAsync(detail, DevHubActivityKind.PullRequests, async () =>
                {
                    prJson = await GetPagedValuesAsync(repo, credential, prUrl, top, false, cancellationToken);
                    var prs = ParseAdoPullRequests(prJson, repo, identity);
                    if (!string.IsNullOrEmpty(identity))
                    {
                        var ownPullRequests = await GetPersonalPullRequestsAsync(repo, credential, identity, options, cancellationToken, repositoryScoped: true);
                        prs = DevHubService.MergePullRequests(ownPullRequests, prs).ToList();
                    }
                    await EnrichPullRequestsAsync(prs, credential, cancellationToken);
                    return prs;
                }, cancellationToken);

                // Build queries require the repository ID, not the name from the Git remote.
                var repositoryUrl = $"{projectUrl}/_apis/git/repositories/{Uri.EscapeDataString(repo.Repo)}?api-version={apiVersion}";
                var repository = await DevHubActivityRequest.RunAsync(async () =>
                {
                    using var repositoryResponse = await SendAuthenticatedGetAsync(credentialHost, credential, repositoryUrl, cancellationToken);
                    repositoryResponse.EnsureSuccessStatusCode();
                    using var repositoryJson = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(repositoryResponse, cancellationToken));
                    if (!repositoryJson.RootElement.TryGetProperty("id", out var repositoryId)
                        || repositoryId.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(repositoryId.GetString(), out var id) || id == Guid.Empty)
                        throw new JsonException("Azure DevOps did not return a valid repository ID for the build query.");
                    return id.ToString("D");
                }, identityValue => !string.IsNullOrEmpty(identityValue), cancellationToken);
                if (repository.Status != DevHubActivityStatus.Success)
                {
                    detail.CiRunsStatus = repository.Status;
                    detail.IssuesStatus = repository.Status;
                    detail.Status = DevHubActivityRequest.AggregateStatus(detail.PullRequestsStatus, detail.IssuesStatus, detail.CiRunsStatus, detail.HasData);
                    detail.ActivityWarning = "Repository identity could not be loaded. Build and issue activity are unavailable. Refresh to retry.";
                    return detail;
                }

                var buildUrl = $"{projectUrl}/_apis/build/builds?repositoryId={repository.Value}&repositoryType=TfsGit&queryOrder=queueTimeDescending";
                detail.CiRuns = await DevHubActivityRequest.ReadCategoryAsync(detail, DevHubActivityKind.CiRuns, async () =>
                {
                    var buildJson = await GetPagedValuesAsync(repo, credential, buildUrl, top, true, cancellationToken);
                    var runs = ParseAdoBuilds(buildJson, repo, identity);
                    if (prJson != null)
                        ApplyBuildStatuses(detail.PullRequests, prJson, buildJson);
                    await EnrichBuildFailuresAsync(runs, repo, credential, buildJson, cancellationToken);
                    return runs;
                }, cancellationToken);
                detail.Issues = await DevHubActivityRequest.ReadCategoryAsync(detail, DevHubActivityKind.Issues,
                    () => GetIssuesAsync(repo, credential, identity, options, repository.Value, cancellationToken), cancellationToken);
                if (detail.CiRunsStatus != DevHubActivityStatus.Success)
                {
                    detail.ActivityWarning = string.Join(" ", new[] { detail.ActivityWarning,
                        "Build activity could not be loaded. Check Build read access and refresh to retry." }.Where(message => !string.IsNullOrEmpty(message)));
                }
                if (detail.IssuesStatus != DevHubActivityStatus.Success)
                {
                    detail.ActivityWarning = string.Join(" ", new[] { detail.ActivityWarning,
                        "Issue activity could not be loaded. Check Work Items read access and refresh to retry." }.Where(message => !string.IsNullOrEmpty(message)));
                }
                cancellationToken.ThrowIfCancellationRequested();

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
                    return DevHubActivityRequest.Observe(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            var response = await SendAsync(credential, url, json, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return DevHubActivityRequest.Observe(response);

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
                return DevHubActivityRequest.Observe(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var retried = await SendAsync(fresh, url, json, cancellationToken);
            try
            {
                if (retried.StatusCode == HttpStatusCode.Unauthorized)
                    _invalidateCredential(host, fresh);
                return DevHubActivityRequest.Observe(retried);
            }
            catch (Exception ex)
            {
                retried.Dispose();
                await ex.LogAsync();
                throw;
            }
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

        internal static string GetConnectionDataUrl(string baseUrl) =>
            $"{baseUrl.TrimEnd('/')}/_apis/connectionData?api-version=3.0-preview";

        private static string GetProjectUrl(RemoteRepoIdentifier repo) =>
            $"{GetBaseUrl(repo).TrimEnd('/')}/{Uri.EscapeDataString(repo.Project)}";

        private static async Task<T> OptionalAsync<T>(Func<Task<T>> fetch, CancellationToken cancellationToken) where T : class
        {
            try
            {
                return await fetch();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }

        private async Task<string> GetPagedValuesAsync(RemoteRepoIdentifier repo, DevHubCredential credential,
            string url, int maxItems, bool continuationPaging, CancellationToken cancellationToken, string apiVersion = null)
        {
            var items = new List<JsonElement>();
            var continuation = string.Empty;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var version = apiVersion ?? (repo.IsAzureDevOpsServer ? "5.0" : "7.1");
            while (items.Count < maxItems)
            {
                var pageSize = Math.Min(100, maxItems - items.Count);
                var pageUrl = url + (url.Contains("?") ? "&" : "?") + $"$top={pageSize}&api-version={version}"
                    + (continuationPaging ? (string.IsNullOrEmpty(continuation) ? string.Empty : "&continuationToken=" + Uri.EscapeDataString(continuation))
                        : $"&$skip={items.Count}");
                using var response = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential, pageUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken));
                var page = doc.RootElement.GetProperty("value").EnumerateArray().ToList();
                items.AddRange(page.Take(pageSize).Select(item => item.Clone()));
                if (continuationPaging)
                {
                    continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var tokens) ? tokens.FirstOrDefault() : null;
                    if (page.Count == 0 || string.IsNullOrEmpty(continuation) || !seenTokens.Add(continuation))
                        break;
                }
                else if (page.Count < pageSize)
                {
                    break;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.Serialize(new { value = items });
        }

        private async Task<List<DevHubPullRequest>> GetPullRequestFeedAsync(RemoteRepoIdentifier repo, DevHubCredential credential,
            string identity, string url, string criteria, int maxItems, bool projectWide, CancellationToken cancellationToken)
        {
            var json = await GetPagedValuesAsync(repo, credential, url + "?" + criteria, maxItems, false, cancellationToken);
            return ParseAdoPullRequests(json, repo, identity, projectWide);
        }

        private async Task<List<DevHubIssue>> GetIssuesAsync(RemoteRepoIdentifier repo, DevHubCredential credential,
            string identity, DevHubActivityOptions options, string repositoryId, CancellationToken cancellationToken)
        {
            var completedStates = await OptionalAsync(() => GetCompletedStatesAsync(repo, credential, cancellationToken), cancellationToken);
            var activeCondition = BuildStateCondition(completedStates, false);
            var personalCondition = "([System.CreatedBy] = @Me OR [System.AssignedTo] = @Me)";
            var results = new List<DevHubIssue>();
            var querySucceeded = false;
            if (!string.IsNullOrEmpty(identity))
            {
                var personal = await OptionalAsync(() => GetIssueFeedAsync(repo, credential, identity, options.MaxItems, repositoryId,
                    personalCondition + " AND " + activeCondition, completedStates, cancellationToken), cancellationToken);
                querySucceeded = personal != null;
                results.AddRange(personal ?? new List<DevHubIssue>());
            }
            if (repositoryId != null)
            {
                // Project incoming work is independent of the current user's created/assigned work.
                var incoming = await OptionalAsync(() => GetIssueFeedAsync(repo, credential, identity, options.MaxItems, repositoryId,
                    activeCondition, completedStates, cancellationToken), cancellationToken);
                querySucceeded |= incoming != null;
                results.AddRange(incoming ?? new List<DevHubIssue>());
            }
            if (options.IncludeCompleted && !string.IsNullOrEmpty(identity))
            {
                var completed = await OptionalAsync(() => GetIssueFeedAsync(repo, credential, identity, options.MaxItems, repositoryId,
                    personalCondition + " AND " + BuildStateCondition(completedStates, true)
                    + " AND [Microsoft.VSTS.Common.ClosedDate] >= @Today - 14", completedStates, cancellationToken,
                    completedSince: DateTime.UtcNow.AddDays(-14)), cancellationToken);
                querySucceeded |= completed != null;
                results.AddRange(completed ?? new List<DevHubIssue>());
            }
            if (!querySucceeded)
                throw new HttpRequestException("Azure DevOps work item activity could not be loaded.");
            return results.GroupBy(issue => issue.WebUrl, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .OrderBy(issue => issue.IsClosed).ThenByDescending(issue => issue.UpdatedAt).ToList();
        }

        private async Task<Dictionary<string, HashSet<string>>> GetCompletedStatesAsync(RemoteRepoIdentifier repo,
            DevHubCredential credential, CancellationToken cancellationToken)
        {
            var version = repo.IsAzureDevOpsServer ? "5.0" : "7.1";
            using var response = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                $"{GetProjectUrl(repo)}/_apis/wit/workitemtypes?api-version={version}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var types = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken));
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in types.RootElement.GetProperty("value").EnumerateArray())
            {
                var name = type.GetProperty("name").GetString();
                using var statesResponse = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                    $"{GetProjectUrl(repo)}/_apis/wit/workitemtypes/{Uri.EscapeDataString(name)}/states?api-version={version}", cancellationToken);
                statesResponse.EnsureSuccessStatusCode();
                using var states = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(statesResponse, cancellationToken));
                result[name] = new HashSet<string>(states.RootElement.GetProperty("value").EnumerateArray()
                    .Where(state => string.Equals(ReadString(state, "category"), "Completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ReadString(state, "category"), "Removed", StringComparison.OrdinalIgnoreCase))
                    .Select(state => state.GetProperty("name").GetString()), StringComparer.OrdinalIgnoreCase);
            }
            return result.Count == 0 ? null : result;
        }

        private static string BuildStateCondition(Dictionary<string, HashSet<string>> completedStates, bool completed)
        {
            var comparison = completed ? "IN" : "NOT IN";
            if (completedStates == null)
                return $"[System.State] {comparison} ('Closed', 'Done', 'Removed')";
            var clauses = completedStates.Where(pair => !completed || pair.Value.Count > 0).Select(pair =>
                pair.Value.Count == 0 ? $"[System.WorkItemType] = '{EscapeWiql(pair.Key)}'"
                : $"([System.WorkItemType] = '{EscapeWiql(pair.Key)}' AND [System.State] {comparison} ({string.Join(",", pair.Value.Select(state => "'" + EscapeWiql(state) + "'"))}))").ToList();
            if (clauses.Count == 0)
                return completed ? "[System.Id] = 0" : "[System.Id] > 0";
            return "(" + string.Join(" OR ", clauses) + ")";
        }

        private static string EscapeWiql(string value) => value.Replace("'", "''");

        private async Task<List<DevHubIssue>> GetIssueFeedAsync(RemoteRepoIdentifier repo, DevHubCredential credential,
            string identity, int top, string repositoryId, string condition, Dictionary<string, HashSet<string>> completedStates,
            CancellationToken cancellationToken, DateTime? completedSince = null)
        {
            var results = new List<DevHubIssue>();
            var remaining = top;
            var beforeId = int.MaxValue;
            var version = repo.IsAzureDevOpsServer ? "5.0" : "7.1";
            while (remaining > 0)
            {
                var pageSize = Math.Min(100, remaining);
                // WIQL has no skip/continuation support. A stable ID cursor avoids repeating page one.
                var query = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND "
                    + condition + (beforeId == int.MaxValue ? string.Empty : $" AND [System.Id] < {beforeId}")
                    + " ORDER BY [System.Id] DESC";
                using var queryResponse = await SendAuthenticatedAsync(GetCredentialHost(repo), credential,
                    $"{GetProjectUrl(repo)}/_apis/wit/wiql?$top={pageSize}&api-version={version}", JsonSerializer.Serialize(new { query }), cancellationToken);
                queryResponse.EnsureSuccessStatusCode();
                using var queryDoc = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(queryResponse, cancellationToken));
                var ids = queryDoc.RootElement.GetProperty("workItems").EnumerateArray().Select(item => item.GetProperty("id").GetInt32())
                    .Where(id => id < beforeId).Distinct().Take(pageSize).ToList();
                if (ids.Count == 0)
                    break;
                using var itemsResponse = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                    $"{GetProjectUrl(repo)}/_apis/wit/workitems?ids={string.Join(",", ids)}&$expand=all&api-version={version}", cancellationToken);
                itemsResponse.EnsureSuccessStatusCode();
                var itemsJson = await HttpResponseReader.ReadStringAsync(itemsResponse, cancellationToken);
                if (completedSince.HasValue)
                {
                    using var items = JsonDocument.Parse(itemsJson);
                    var recentlyClosed = items.RootElement.GetProperty("value").EnumerateArray().Where(item =>
                        item.GetProperty("fields").TryGetProperty("Microsoft.VSTS.Common.ClosedDate", out var closed)
                        && closed.ValueKind == JsonValueKind.String && closed.TryGetDateTime(out var closedDate)
                        && closedDate.ToUniversalTime() >= completedSince.Value);
                    itemsJson = JsonSerializer.Serialize(new { value = recentlyClosed.ToList() });
                }
                results.AddRange(ParseAdoIssues(itemsJson, repo, identity, repositoryId,
                    includeIncoming: repositoryId != null, completedStates: completedStates)
                    .Where(issue => !completedSince.HasValue || issue.IsClosed));
                beforeId = ids.Min();
                remaining -= ids.Count;
                if (ids.Count < pageSize)
                    break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }

        internal static List<DevHubIssue> ParseAdoIssues(string json, RemoteRepoIdentifier repo,
            string currentUserId, string repositoryId = null, bool includeIncoming = false,
            Dictionary<string, HashSet<string>> completedStates = null)
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
                var type = ReadString(fields, "System.WorkItemType");
                var closed = completedStates != null && type != null && completedStates.TryGetValue(type, out var terminalStates)
                    ? terminalStates.Contains(state) : string.Equals(state, "Closed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "Done", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "Removed", StringComparison.OrdinalIgnoreCase);
                var authored = HasIdentity(fields, "System.CreatedBy", currentUserId);
                var assigned = HasIdentity(fields, "System.AssignedTo", currentUserId);
                if (!includeIncoming && !authored && !assigned)
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
                    WorkItemType = type,
                    AssignedTo = fields.TryGetProperty("System.AssignedTo", out var assignee) && assignee.ValueKind == JsonValueKind.Object
                        ? ReadString(assignee, "displayName") : ReadString(fields, "System.AssignedTo"),
                    Priority = fields.TryGetProperty("Microsoft.VSTS.Common.Priority", out var priority) ? priority.ToString() : null,
                    Iteration = ReadString(fields, "System.IterationPath"),
                    IsBlocked = string.Equals(ReadString(fields, "Microsoft.VSTS.CMMI.Blocked"), "Yes", StringComparison.OrdinalIgnoreCase)
                        || (fields.TryGetProperty("Microsoft.VSTS.CMMI.Blocked", out var blocked) && blocked.ValueKind == JsonValueKind.True)
                        || (ReadString(fields, "System.Tags") ?? string.Empty).Split(';').Any(tag => string.Equals(tag.Trim(), "Blocked", StringComparison.OrdinalIgnoreCase)),
                    IsClosed = closed,
                    IsAuthoredByCurrentUser = authored,
                    IsAssignedToCurrentUser = assigned,
                    CreatedAt = fields.GetProperty("System.CreatedDate").GetDateTime().ToUniversalTime(),
                    UpdatedAt = fields.GetProperty("System.ChangedDate").GetDateTime().ToUniversalTime(),
                    WebUrl = $"{GetBaseUrl(repo)}/{Uri.EscapeDataString(repo.Project)}/_workitems/edit/{id}",
                });
            }
            return results;
        }

        private static string ReadString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        private async Task EnrichPullRequestsAsync(IReadOnlyList<DevHubPullRequest> pullRequests, DevHubCredential credential,
            CancellationToken cancellationToken)
        {
            using var semaphore = new SemaphoreSlim(3, 3);
            await Task.WhenAll(pullRequests.Where(pr => pr.IsOpen).Select(async pr =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var repo = pr.RepoIdentifier;
                    var version = repo.IsAzureDevOpsServer ? "5.0" : "7.1";
                    var prUrl = $"{GetProjectUrl(repo)}/_apis/git/repositories/{Uri.EscapeDataString(repo.Repo)}/pullRequests/{pr.NumericId}";
                    await OptionalAsync(async () =>
                    {
                        using var response = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                            $"{prUrl}/threads?api-version={version}", cancellationToken);
                        response.EnsureSuccessStatusCode();
                        using var threads = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken));
                        var discussions = threads.RootElement.GetProperty("value").EnumerateArray()
                            .Where(thread => !(thread.TryGetProperty("isDeleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)).ToList();
                        var knownStates = new[] { "active", "pending", "fixed", "wontFix", "closed", "byDesign" };
                        if (!response.Headers.Contains("x-ms-continuationtoken")
                            && discussions.All(thread => knownStates.Contains(ReadString(thread, "status"))))
                            pr.UnresolvedThreadCount = discussions.Count(thread => ReadString(thread, "status") == "active"
                                || ReadString(thread, "status") == "pending");
                        return pr;
                    }, cancellationToken);

                    if (!pr.IsAuthoredByCurrentUser || pr.IsDraft)
                        return;
                    pr.MergeBlockReason = "Merge readiness could not be verified. Refresh to retry.";
                    await OptionalAsync(async () =>
                    {
                        using var response = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                            $"{prUrl}?api-version={version}", cancellationToken);
                        response.EnsureSuccessStatusCode();
                        using var metadata = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken));
                        var root = metadata.RootElement;
                        if (!string.Equals(ReadString(root, "status"), "active", StringComparison.OrdinalIgnoreCase)
                            || (root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True))
                            return pr;
                        var mergeStatus = ReadString(root, "mergeStatus");
                        var rejected = root.TryGetProperty("reviewers", out var reviewers) && reviewers.ValueKind == JsonValueKind.Array
                            && reviewers.EnumerateArray().Any(reviewer => reviewer.TryGetProperty("vote", out var vote)
                                && vote.ValueKind == JsonValueKind.Number && vote.GetInt32() < 0);
                        if (rejected || mergeStatus == "conflicts" || pr.UnresolvedThreadCount > 0)
                        {
                            pr.CanMerge = false;
                            pr.MergeBlockReason = rejected ? "Reviewer changes requested" : mergeStatus == "conflicts"
                                ? "Merge conflicts" : "Unresolved review threads";
                        }

                        // Only statuses explicitly associated with the current source iteration establish green checks.
                        using var iterationResponse = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                            $"{prUrl}/iterations?api-version={version}", cancellationToken);
                        iterationResponse.EnsureSuccessStatusCode();
                        using var iterations = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(iterationResponse, cancellationToken));
                        var iteration = iterations.RootElement.GetProperty("value").EnumerateArray()
                            .OrderByDescending(item => item.GetProperty("id").GetInt32()).FirstOrDefault();
                        if (iteration.ValueKind != JsonValueKind.Object || !iteration.TryGetProperty("sourceRefCommit", out var source)
                            || !root.TryGetProperty("lastMergeSourceCommit", out var currentSource)
                            || string.IsNullOrEmpty(ReadString(source, "commitId"))
                            || ReadString(source, "commitId") != ReadString(currentSource, "commitId"))
                            return pr;
                        var iterationId = iteration.GetProperty("id").GetInt32();
                        using var statusesResponse = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                            $"{prUrl}/statuses?api-version={version}", cancellationToken);
                        statusesResponse.EnsureSuccessStatusCode();
                        using var statuses = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(statusesResponse, cancellationToken));
                        if (statusesResponse.Headers.Contains("x-ms-continuationtoken"))
                            return pr;
                        var latest = statuses.RootElement.GetProperty("value").EnumerateArray()
                            .Where(status => status.TryGetProperty("iterationId", out var statusIteration)
                                && statusIteration.ValueKind == JsonValueKind.Number && statusIteration.GetInt32() == iterationId)
                            .GroupBy(status => status.TryGetProperty("context", out var context)
                                ? ReadString(context, "genre") + "/" + ReadString(context, "name") : string.Empty)
                            .Select(group => group.OrderByDescending(status => status.GetProperty("id").GetInt32()).First()).ToList();
                        if (latest.Count > 0)
                        {
                            var failed = latest.Any(status => ReadString(status, "state") == "failed" || ReadString(status, "state") == "error");
                            pr.CiStatus = failed ? "failure" : latest.All(status => ReadString(status, "state") == "succeeded") ? "success" : "pending";
                            pr.ChecksSummary = $"{latest.Count(status => ReadString(status, "state") == "succeeded")}/{latest.Count} checks passed";
                            pr.ChecksUrl = latest.Select(status => ReadString(status, "targetUrl")).FirstOrDefault(url => !string.IsNullOrEmpty(url)) ?? pr.WebUrl;
                        }
                        if (!root.TryGetProperty("repository", out var repository) || !repository.TryGetProperty("project", out var project)
                            || !Guid.TryParse(ReadString(project, "id"), out var projectId))
                            return pr;
                        var artifact = Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{projectId:D}/{pr.NumericId}");
                        var policyJson = await GetPagedValuesAsync(repo, credential,
                            $"{GetProjectUrl(repo)}/_apis/policy/evaluations?artifactId={artifact}&includeNotApplicable=true",
                            300, false, cancellationToken, repo.IsAzureDevOpsServer ? "5.0-preview.1" : "7.1-preview.1");
                        using var policies = JsonDocument.Parse(policyJson);
                        var evaluations = policies.RootElement.GetProperty("value").EnumerateArray().ToList();
                        // A truncated list or missing policy configuration cannot prove readiness.
                        if (evaluations.Count == 300 || evaluations.Any(policy => !policy.TryGetProperty("configuration", out var config)
                            || !config.TryGetProperty("isBlocking", out var blocking) || (blocking.ValueKind != JsonValueKind.True && blocking.ValueKind != JsonValueKind.False)
                            || !config.TryGetProperty("isEnabled", out var enabled) || (enabled.ValueKind != JsonValueKind.True && enabled.ValueKind != JsonValueKind.False)))
                            return pr;
                        var required = evaluations.Where(policy => policy.GetProperty("configuration").GetProperty("isBlocking").GetBoolean()
                            && policy.GetProperty("configuration").GetProperty("isEnabled").GetBoolean()).ToList();
                        var knownStates = new[] { "approved", "queued", "running", "rejected", "notApplicable", "broken" };
                        if (required.Any(policy => !knownStates.Contains(ReadString(policy, "status"))))
                            return pr;
                        if (required.Any(policy => ReadString(policy, "status") != "approved"))
                        {
                            pr.CanMerge = false;
                            pr.MergeBlockReason = "Required policies have not all been approved";
                        }
                        else if (required.Count > 0 && !rejected && reviewers.ValueKind == JsonValueKind.Array
                            && root.TryGetProperty("isDraft", out var currentDraft) && currentDraft.ValueKind == JsonValueKind.False && mergeStatus == "succeeded"
                            && pr.UnresolvedThreadCount == 0 && latest.Count > 0 && pr.CiStatus == "success")
                        {
                            pr.CanMerge = true;
                            pr.MergeBlockReason = null;
                        }
                        return pr;
                    }, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        private async Task EnrichBuildFailuresAsync(IReadOnlyList<DevHubCiRun> runs, RemoteRepoIdentifier repo,
            DevHubCredential credential, string buildJson, CancellationToken cancellationToken)
        {
            using var builds = JsonDocument.Parse(buildJson);
            var metadata = builds.RootElement.GetProperty("value").EnumerateArray().ToList();
            foreach (var run in runs.Where(item => item.Status == "failure").Take(10))
            {
                await OptionalAsync(async () =>
                {
                    var build = metadata.FirstOrDefault(item => item.TryGetProperty("_links", out var links)
                        && links.TryGetProperty("web", out var web) && ReadString(web, "href") == run.WebUrl);
                    if (build.ValueKind != JsonValueKind.Object || !build.TryGetProperty("id", out var id))
                        return run;
                    var version = repo.IsAzureDevOpsServer ? "5.0" : "7.1";
                    using var response = await SendAuthenticatedGetAsync(GetCredentialHost(repo), credential,
                        $"{GetProjectUrl(repo)}/_apis/build/builds/{id.GetInt32()}/timeline?api-version={version}", cancellationToken);
                    response.EnsureSuccessStatusCode();
                    using var timeline = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken));
                    var failed = timeline.RootElement.GetProperty("records").EnumerateArray()
                        .FirstOrDefault(record => ReadString(record, "result") == "failed");
                    if (failed.ValueKind != JsonValueKind.Object)
                        return run;
                    run.FailureSummary = ReadString(failed, "name");
                    if (failed.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
                    {
                        var message = issues.EnumerateArray().Where(issue => ReadString(issue, "type") == "error")
                            .Select(issue => ReadString(issue, "message")).FirstOrDefault(text => !string.IsNullOrEmpty(text));
                        run.FailureSummary = message ?? run.FailureSummary;
                    }
                    if (run.FailureSummary?.Length > 500)
                        run.FailureSummary = run.FailureSummary.Substring(0, 500);
                    run.FailureUrl = run.WebUrl;
                    if (failed.TryGetProperty("log", out var log) && log.TryGetProperty("id", out var logId) && logId.TryGetInt32(out var number))
                        run.FailureUrl = $"{GetProjectUrl(repo)}/_apis/build/builds/{id.GetInt32()}/logs/{number}?api-version={version}";
                    return run;
                }, cancellationToken);
            }
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
                        IsMerged = status == "merged",
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

        internal static List<DevHubCiRun> ParseAdoBuilds(string json, RemoteRepoIdentifier repo, string currentUserId = null, bool projectWide = false)
        {
            var results = new List<DevHubCiRun>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("value", out var builds))
                    return results;

                foreach (var build in builds.EnumerateArray())
                {
                    var buildRepo = repo;
                    if (projectWide)
                    {
                        if (!build.TryGetProperty("repository", out var repository) || string.IsNullOrEmpty(ReadString(repository, "name")))
                            continue;
                        buildRepo = new RemoteRepoIdentifier(repo.Host, repo.Owner, ReadString(repository, "name"), repo.Project, repo.BaseUrl, repo.IsAzureDevOpsServer);
                    }
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

                    TimeSpan? duration = null;
                    if (status == "completed" && build.TryGetProperty("startTime", out var durationStart)
                        && durationStart.ValueKind != JsonValueKind.Null
                        && build.TryGetProperty("finishTime", out var durationFinish) && durationFinish.ValueKind != JsonValueKind.Null)
                    {
                        var elapsed = durationFinish.GetDateTime().ToUniversalTime() - durationStart.GetDateTime().ToUniversalTime();
                        if (elapsed >= TimeSpan.Zero)
                            duration = elapsed;
                    }

                    results.Add(new DevHubCiRun
                    {
                        ProviderName = "Azure DevOps",
                        RepoIdentifier = buildRepo,
                        RepoDisplayName = buildRepo.DisplayName,
                        IsTriggeredByCurrentUser = HasIdentity(build, "requestedFor", currentUserId) || HasIdentity(build, "requestedBy", currentUserId),
                        Name = definition,
                        Branch = build.TryGetProperty("sourceBranch", out var branch)
                            ? StripRefsPrefix(branch.GetString())
                            : null,
                        Status = MapAdoBuildStatus(status, result),
                        Timestamp = timestamp,
                        Duration = duration,
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
