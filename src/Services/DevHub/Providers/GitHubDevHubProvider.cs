using System;
using System.Collections.Generic;
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
        private string _cachedLogin;

        public string DisplayName => "GitHub";

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
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", cancellationToken);
            if (credential == null)
                return null;

            try
            {
                using (var client = CreateHttpClient(credential))
                {
                    var response = await client.GetAsync("https://api.github.com/user", cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var json = await response.Content.ReadAsStringAsync();
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        var login = root.GetProperty("login").GetString();
                        _cachedLogin = login;

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
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        /// <summary>
        /// Gets the cached GitHub login, or fetches it from the API if not yet cached.
        /// </summary>
        private async Task<(DevHubCredential credential, string login)> GetCredentialAndLoginAsync(CancellationToken cancellationToken)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", cancellationToken);
            if (credential == null)
                return (null, null);

            if (_cachedLogin != null)
                return (credential, _cachedLogin);

            // Fetch login from API if not yet cached
            using (var client = CreateHttpClient(credential))
            {
                var response = await client.GetAsync("https://api.github.com/user", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (credential, null);

                var json = await response.Content.ReadAsStringAsync();
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    _cachedLogin = doc.RootElement.GetProperty("login").GetString();
                    return (credential, _cachedLogin);
                }
            }
        }

        public async Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken)
        {
            var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
            if (credential == null || string.IsNullOrEmpty(login))
                return Array.Empty<DevHubPullRequest>();

            try
            {
                using (var client = CreateHttpClient(credential))
                {
                    // Use "involves" to match author, assignee, commenter, and review-requested
                    var query = Uri.EscapeDataString($"is:pr is:open involves:{login}");
                    var url = $"https://api.github.com/search/issues?q={query}&sort=updated&order=desc&per_page=30";

                    var response = await client.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        return Array.Empty<DevHubPullRequest>();

                    var json = await response.Content.ReadAsStringAsync();
                    return ParsePullRequests(json, login);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubPullRequest>();
            }
        }

        public async Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
        {
            var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
            if (credential == null || string.IsNullOrEmpty(login))
                return Array.Empty<DevHubIssue>();

            try
            {
                using (var client = CreateHttpClient(credential))
                {
                    var query = Uri.EscapeDataString($"is:issue is:open involves:{login}");
                    var url = $"https://api.github.com/search/issues?q={query}&sort=updated&order=desc&per_page=20";

                    var response = await client.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        return Array.Empty<DevHubIssue>();

                    var json = await response.Content.ReadAsStringAsync();
                    return ParseIssues(json);
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubIssue>();
            }
        }

        public async Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken)
        {
            var (credential, login) = await GetCredentialAndLoginAsync(cancellationToken);
            if (credential == null || string.IsNullOrEmpty(login))
                return Array.Empty<DevHubCiRun>();

            try
            {
                using (var client = CreateHttpClient(credential))
                {
                    // Fetch user's recently pushed repos (personal + org) to find CI runs
                    var reposUrl = "https://api.github.com/user/repos?sort=pushed&per_page=5&affiliation=owner,collaborator,organization_member";
                    var reposResponse = await client.GetAsync(reposUrl, cancellationToken);
                    if (!reposResponse.IsSuccessStatusCode)
                        return Array.Empty<DevHubCiRun>();

                    var reposJson = await reposResponse.Content.ReadAsStringAsync();
                    var repos = ParseRepoList(reposJson);

                    var allRuns = new List<DevHubCiRun>();
                    foreach (var repo in repos)
                    {
                        var runsUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/actions/runs?per_page=3";
                        var runsResponse = await client.GetAsync(runsUrl, cancellationToken);
                        if (runsResponse.IsSuccessStatusCode)
                        {
                            var runsJson = await runsResponse.Content.ReadAsStringAsync();
                            allRuns.AddRange(ParseCiRuns(runsJson, repo));
                        }
                    }

                    allRuns.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                    return allRuns;
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return Array.Empty<DevHubCiRun>();
            }
        }

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

        public async Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken)
        {
            if (repo == null)
                return null;

            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", cancellationToken);
            if (credential == null)
                return null;

            try
            {
                using (var client = CreateHttpClient(credential))
                {
                    var detail = new DevHubRepoDetail
                    {
                        RepoIdentifier = repo,
                        FetchedAt = DateTime.UtcNow,
                    };

                    // Fetch PRs for this repo
                    var prUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/pulls?state=open&sort=updated&direction=desc&per_page=10";
                    var prResponse = await client.GetAsync(prUrl, cancellationToken);
                    if (prResponse.IsSuccessStatusCode)
                    {
                        var prJson = await prResponse.Content.ReadAsStringAsync();
                        detail.PullRequests = ParseRepoPullRequests(prJson, repo, credential.Username);
                    }

                    // Fetch issues for this repo
                    var issueUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/issues?state=open&sort=updated&direction=desc&per_page=10";
                    var issueResponse = await client.GetAsync(issueUrl, cancellationToken);
                    if (issueResponse.IsSuccessStatusCode)
                    {
                        var issueJson = await issueResponse.Content.ReadAsStringAsync();
                        detail.Issues = ParseRepoIssues(issueJson, repo);
                    }

                    // Fetch latest CI runs
                    var runsUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/actions/runs?per_page=5";
                    var runsResponse = await client.GetAsync(runsUrl, cancellationToken);
                    if (runsResponse.IsSuccessStatusCode)
                    {
                        var runsJson = await runsResponse.Content.ReadAsStringAsync();
                        detail.CiRuns = ParseCiRuns(runsJson, repo);
                    }

                    return detail;
                }
            }
            catch (Exception ex)
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

        private static System.Net.Http.HttpClient CreateHttpClient(DevHubCredential credential)
        {
            var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "StartScreen-VS-Extension");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {credential.Token}");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        private static List<DevHubPullRequest> ParsePullRequests(string json, string currentUser)
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
                        results.Add(pr);
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
                Status = item.TryGetProperty("draft", out var draft) && draft.GetBoolean() ? "draft" : "open",
                UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                WebUrl = item.GetProperty("html_url").GetString(),
                IsAuthoredByCurrentUser = string.Equals(author, currentUser, StringComparison.OrdinalIgnoreCase),
            };
        }

        private static List<DevHubPullRequest> ParseRepoPullRequests(string json, RemoteRepoIdentifier repo, string currentUser)
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
                        Status = item.TryGetProperty("draft", out var draft) && draft.GetBoolean() ? "draft" : "open",
                        UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                        WebUrl = item.GetProperty("html_url").GetString(),
                        IsAuthoredByCurrentUser = string.Equals(author, currentUser, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            return results;
        }

        private static List<DevHubIssue> ParseIssues(string json)
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

                    var issue = ParseIssueFromSearchItem(item);
                    if (issue != null)
                        results.Add(issue);
                }
            }

            return results;
        }

        private static DevHubIssue ParseIssueFromSearchItem(System.Text.Json.JsonElement item)
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
                State = "open",
                UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                WebUrl = item.GetProperty("html_url").GetString(),
            };
        }

        private static List<DevHubIssue> ParseRepoIssues(string json, RemoteRepoIdentifier repo)
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
                        State = "open",
                        UpdatedAt = item.GetProperty("updated_at").GetDateTime().ToUniversalTime(),
                        CreatedAt = item.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                        WebUrl = item.GetProperty("html_url").GetString(),
                    });
                }
            }

            return results;
        }

        private static List<DevHubCiRun> ParseCiRuns(string json, RemoteRepoIdentifier repo)
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
