using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models;
using StartScreen.Services.DevHub;

namespace StartScreen.Services
{
    internal static class CopilotCloudSessions
    {
        private const int _maximumTasks = 30;
        internal const int MaximumRepositoryConcurrency = 4;
        private static readonly DevHubCredentialCache<string> _repositories = new DevHubCredentialCache<string>(TimeSpan.FromHours(1));
        private static readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        internal static async Task<CopilotSessionDiscovery> ReadAsync(string account, CancellationToken token, bool forceRefresh = false)
        {
            var credential = await DevHubCredentialHelper.GetCredentialAsync("github.com", account, token);
            if (credential == null)
                return new CopilotSessionDiscovery { Status = "Connect GitHub to load cloud-agent sessions." };
            return await ReadAsync(_client, credential, token, forceRefresh);
        }

        internal static async Task<CopilotSessionDiscovery> ReadAsync(HttpClient client, DevHubCredential credential, CancellationToken token,
            bool forceRefresh = false, DevHubCredentialCache<string> repositoryCache = null)
        {
            await TaskScheduler.Default.SwitchTo(alwaysYield: true);
            token.ThrowIfCancellationRequested();
            var cache = repositoryCache ?? _repositories;
            var generation = cache.Generation;
            EnsureCredentialCurrent(cache, credential, generation);
            using var json = await GetAsync(client, credential,
                $"https://api.github.com/agents/tasks?per_page={_maximumTasks}&sort=updated_at&direction=desc", token);
            EnsureCredentialCurrent(cache, credential, generation);
            if (!json.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
                throw new JsonException("GitHub returned no agent task list.");
            var sessions = new List<CopilotActivitySession>();
            var unidentified = false;
            var pending = new List<(JsonElement Task, Guid Id, string Url, string Repository, long? RepositoryId)>();
            foreach (var task in tasks.EnumerateArray().Take(_maximumTasks))
            {
                token.ThrowIfCancellationRequested();
                if (!IsCloudTask(task))
                {
                    unidentified |= !task.TryGetProperty("agent_collaborators", out _)
                        && !task.TryGetProperty("clients", out _);
                    continue;
                }
                if (task.TryGetProperty("archived_at", out var archived) && archived.ValueKind == JsonValueKind.String)
                    continue;
                var id = task.GetProperty("id").GetString();
                if (!Guid.TryParseExact(id, "D", out var taskId))
                    throw new JsonException("GitHub returned an invalid agent task ID.");
                var url = Text(task, "html_url");
                var repository = RepositoryFromUrl(url);
                long? repositoryId = null;
                if (repository == null && task.TryGetProperty("repository", out var repo)
                    && repo.TryGetProperty("id", out var repoId) && repoId.TryGetInt64(out var number) && number > 0)
                {
                    repositoryId = number;
                }
                if (repository == null && repositoryId == null)
                    throw new JsonException("GitHub returned a cloud task without a repository or a compatible web URL.");
                pending.Add((task, taskId, url, repository, repositoryId));
            }
            using var concurrency = new SemaphoreSlim(MaximumRepositoryConcurrency);
            var resolved = await Task.WhenAll(pending.Where(item => item.RepositoryId.HasValue)
                .Select(item => item.RepositoryId.Value).Distinct().Select(async number =>
                {
                    var resource = DevHubMetadataCache.GetClientScope(client) + "|https://api.github.com/repositories/" + number;
                    EnsureCredentialCurrent(cache, credential, generation);
                    if (!forceRefresh && cache.TryGetValue(credential, resource, out var cached))
                        return (Id: number, Repository: cached);
                    if (forceRefresh)
                        cache.Remove(credential, resource);
                    await concurrency.WaitAsync(token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        using var detail = await GetAsync(client, credential, "https://api.github.com/repositories/" + number, token);
                        var repository = detail.RootElement.GetProperty("full_name").GetString();
                        if (!IsRepository(repository))
                            throw new JsonException("GitHub returned an invalid repository name.");
                        token.ThrowIfCancellationRequested();
                        EnsureCredentialCurrent(cache, credential, generation);
                        if (!cache.Store(credential, resource, repository, generation))
                            throw new UnauthorizedAccessException("GitHub credentials changed or expired. Refresh to retry.");
                        return (Id: number, Repository: repository);
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }));
            token.ThrowIfCancellationRequested();
            EnsureCredentialCurrent(cache, credential, generation);
            var repositories = resolved.ToDictionary(item => item.Id, item => item.Repository);
            foreach (var item in pending)
            {
                token.ThrowIfCancellationRequested();
                var task = item.Task;
                var taskId = item.Id;
                var url = item.Url;
                var repository = item.Repository ?? repositories[item.RepositoryId.Value];
                url ??= "https://github.com/" + repository + "/agents/tasks/" + taskId.ToString("D");
                if (!IsTaskUrl(url, taskId))
                    throw new JsonException("GitHub returned an invalid cloud-agent web URL.");
                sessions.Add(new CopilotActivitySession
                {
                    Id = taskId.ToString("D"),
                    Origin = CopilotSessionOrigin.GitHub,
                    Title = Text(task, "name") ?? "Untitled cloud task",
                    Repository = repository,
                    WebUrl = url,
                    RemoteState = task.GetProperty("state").GetString(),
                    LastActivity = task.TryGetProperty("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String
                        ? updated.GetDateTimeOffset() : task.GetProperty("created_at").GetDateTimeOffset(),
                });
            }
            return new CopilotSessionDiscovery
            {
                Sessions = sessions,
                Status = unidentified ? "GitHub omitted origin metadata for some tasks; unverified and synced local tasks are not shown as cloud agents." : null,
            };
        }

        private static void EnsureCredentialCurrent(DevHubCredentialCache<string> cache, DevHubCredential credential, int generation)
        {
            if (!cache.IsCurrent(credential, generation))
                throw new UnauthorizedAccessException("GitHub credentials changed or expired. Refresh to retry.");
        }

        internal static bool IsCloudTask(JsonElement task)
        {
            if (task.TryGetProperty("current_environment", out var environment) && environment.ValueKind == JsonValueKind.Object
                && (Text(environment, "kind") == "user-local" || Text(environment, "kind") == "managed-sandbox"))
                return false;
            if (task.TryGetProperty("compute", out var compute) && compute.ValueKind == JsonValueKind.Object
                && Text(compute, "provider") == "sandboxes")
                return false;
            if (task.TryGetProperty("agent_collaborators", out var collaborators) && collaborators.ValueKind == JsonValueKind.Array
                && collaborators.EnumerateArray().Any(agent => IsCloudSlug(Text(agent, "slug"))))
                return true;
            return task.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Array
                && clients.EnumerateArray().Any(client => client.TryGetProperty("creator", out var creator)
                    && creator.ValueKind == JsonValueKind.True && IsCloudSlug(Text(client, "slug")));
        }

        private static bool IsCloudSlug(string slug) => slug == "copilot-developer" || slug == "copilot-swe-agent";

        internal static bool IsTaskUrl(string url, Guid id) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && RepositoryFromUrl(url) != null
            && uri.AbsolutePath.EndsWith("/agents/tasks/" + id.ToString("D"), StringComparison.OrdinalIgnoreCase);

        private static string RepositoryFromUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo))
                return null;
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            var repository = segments.Length == 5 && segments[2] == "agents" && segments[3] == "tasks"
                ? segments[0] + "/" + segments[1] : null;
            return IsRepository(repository) ? repository : null;
        }

        private static bool IsRepository(string value) => value != null && value.Split('/').Length == 2
            && value.All(character => char.IsLetterOrDigit(character) || character == '/' || character == '-' || character == '_' || character == '.')
            && value.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

        private static string Text(JsonElement element, string property) => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static async Task<JsonDocument> GetAsync(HttpClient client, DevHubCredential credential, string url, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("StartScreen-VS-Extension");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("GitHub cloud sessions require a user token with Agent tasks read permission. Reconnect GitHub and refresh.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new NotSupportedException("The GitHub agent tasks API is unavailable for this account.");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, token, maxBytes: 4 * 1024 * 1024));
        }
    }
}