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

            return remoteUrl.IndexOf("dev.azure.com", StringComparison.OrdinalIgnoreCase) >= 0
                || remoteUrl.IndexOf(".visualstudio.com", StringComparison.OrdinalIgnoreCase) >= 0
                || remoteUrl.IndexOf("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl)
        {
            var parsed = RemoteRepoIdentifier.TryParse(remoteUrl);
            if (parsed != null && parsed.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
                return parsed;

            return null;
        }

        public async Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync("dev.azure.com", cancellationToken);
            if (credential == null)
                return null;

            try
            {
                // Use the VS SPS endpoint to get user profile
                var response = await SendGetAsync(
                    credential,
                    "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1",
                    cancellationToken);

                // Token expired - invalidate cache and retry once with a fresh credential.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    DevHubCredentialHelper.InvalidateCachedCredential("dev.azure.com");
                    credential = await DevHubCredentialHelper.GetCredentialAsync("dev.azure.com", cancellationToken);
                    if (credential == null)
                        return null;

                    response = await SendGetAsync(
                        credential,
                        "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1",
                        cancellationToken);
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    return new DevHubUser
                    {
                        Username = root.TryGetProperty("emailAddress", out var email) ? email.GetString() : credential.Username,
                        DisplayName = root.TryGetProperty("displayName", out var name) ? name.GetString() : credential.Username,
                        AvatarUrl = null,
                        ProviderName = DisplayName,
                        Host = "dev.azure.com",
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
            var credential = await DevHubCredentialHelper.GetCredentialAsync("dev.azure.com", cancellationToken);
            if (credential == null)
                return Array.Empty<DevHubPullRequest>();

            try
            {
                // ADO doesn't have a cross-org PR search, so we use the "my pull requests" endpoint
                // This requires knowing the org. For now, we return empty and rely on repo-specific detail.
                // TODO: When the user has configured org(s), query each one.
                _ = credential;
                return Array.Empty<DevHubPullRequest>();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubPullRequest>();
            }
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync("dev.azure.com", cancellationToken);
            if (credential == null)
                return Array.Empty<DevHubIssue>();

            try
            {
                // Same limitation as PRs - need org context for WIQL queries
                _ = credential;
                return Array.Empty<DevHubIssue>();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubIssue>();
            }
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

            var credential = await DevHubCredentialHelper.GetCredentialAsync("dev.azure.com", cancellationToken);
            if (credential == null)
                return null;

            try
            {
                var detail = new DevHubRepoDetail
                {
                    RepoIdentifier = repo,
                    FetchedAt = DateTime.UtcNow,
                };

                var baseUrl = $"https://dev.azure.com/{repo.Owner}/{repo.Project}";

                // Fetch PRs
                var prUrl = $"{baseUrl}/_apis/git/repositories/{repo.Repo}/pullrequests?searchCriteria.status=active&$top=10&api-version=7.1";
                var prResponse = await SendGetAsync(credential, prUrl, cancellationToken);
                if (prResponse.IsSuccessStatusCode)
                {
                    var prJson = await prResponse.Content.ReadAsStringAsync();
                    detail.PullRequests = ParseAdoPullRequests(prJson, repo);
                }

                // Fetch recent builds
                var buildUrl = $"{baseUrl}/_apis/build/builds?repositoryId={repo.Repo}&repositoryType=TfsGit&$top=5&api-version=7.1";
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
            $"https://dev.azure.com/{repo.Owner}/{repo.Project}/_git/{repo.Repo}";

        public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) =>
            $"https://dev.azure.com/{repo.Owner}/{repo.Project}/_git/{repo.Repo}/pullrequests";

        public string GetIssuesWebUrl(RemoteRepoIdentifier repo) =>
            $"https://dev.azure.com/{repo.Owner}/{repo.Project}/_workitems";

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
                        WebUrl = $"https://dev.azure.com/{repo.Owner}/{repo.Project}/_git/{repo.Repo}/pullrequest/{id}",
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
                            : $"https://dev.azure.com/{repo.Owner}/{repo.Project}/_build",
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
