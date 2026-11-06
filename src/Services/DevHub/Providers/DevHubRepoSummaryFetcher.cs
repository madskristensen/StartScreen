using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartScreen.Helpers;
using StartScreen.Models.DevHub;

namespace StartScreen.Services.DevHub.Providers
{
    /// <summary>
    /// Enumerates uncapped REST PR lists. Neither provider's list response count is a total.
    /// </summary>
    internal static class DevHubRepoSummaryFetcher
    {
        internal static async Task<DevHubRepoSummary> FetchAsync(RemoteRepoIdentifier repo,
            Func<CancellationToken, Task<DevHubCredential>> acquireCredential,
            Func<DevHubCredential, string, CancellationToken, Task<HttpResponseMessage>> send,
            bool gitHub, CancellationToken cancellationToken, string signInMessage = null,
            DevHubIdentityCache identityCache = null)
        {
            if (repo == null)
                throw new ArgumentNullException(nameof(repo));
            cancellationToken.ThrowIfCancellationRequested();
            if (!gitHub && string.IsNullOrEmpty(repo.Project))
                return new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unavailable,
                    statusMessage: "An Azure DevOps project is required.");

            try
            {
                var credential = await acquireCredential(cancellationToken);
                if (credential == null)
                    return new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unauthorized,
                        statusMessage: signInMessage ?? "Sign in to retrieve repository totals.");

                var baseUrl = (repo.BaseUrl ?? $"https://dev.azure.com/{Uri.EscapeDataString(repo.Owner)}").TrimEnd('/');
                var apiVersion = repo.IsAzureDevOpsServer ? "5.0" : "7.1";
                var identityUrl = gitHub ? "https://api.github.com/user"
                    : AzureDevOpsDevHubProvider.GetConnectionDataUrl(baseUrl);
                var identity = identityCache != null
                    ? await identityCache.GetAsync(credential, identityUrl, send, gitHub, cancellationToken)
                    : await GetIdentityAsync(credential, identityUrl, send, gitHub, cancellationToken);
                int? requested = identity == null ? (int?)null : 0;
                var seen = new HashSet<int>();
                var offset = 0;
                var page = 1;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = gitHub
                        ? $"https://api.github.com/repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Repo)}/pulls?state=open&sort=created&direction=asc&per_page=100&page={page}"
                        : $"{baseUrl}/{Uri.EscapeDataString(repo.Project)}/_apis/git/repositories/{Uri.EscapeDataString(repo.Repo)}/pullrequests?searchCriteria.status=active&$top=100&$skip={offset}&api-version={apiVersion}";
                    using (var response = await send(credential, url, cancellationToken))
                    {
                        if (!response.IsSuccessStatusCode)
                            return Failure(repo, response.StatusCode, signInMessage);
                        using (var doc = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var items = gitHub ? doc.RootElement : doc.RootElement.GetProperty("value");
                            var length = items.GetArrayLength();
                            // Continue even after short pages: servers may impose a lower page size.
                            // An empty page is the only completion signal used by both APIs here.
                            if (length == 0)
                                return new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Ready, seen.Count, requested,
                                    requested.HasValue ? "Direct personal review requests only; team/group requests are excluded."
                                        : "Personal review requests are unavailable; repository total is complete.");

                            foreach (var item in items.EnumerateArray())
                            {
                                var id = item.GetProperty(gitHub ? "number" : "pullRequestId").GetInt32();
                                if (!seen.Add(id))
                                    return new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unavailable,
                                        statusMessage: "PR pagination repeated an item; refresh to retrieve a complete total.");
                                if (requested.HasValue)
                                {
                                    var isRequested = IsRequested(item, identity, gitHub);
                                    requested = isRequested.HasValue ? requested + (isRequested.Value ? 1 : 0) : null;
                                }
                            }
                            offset = checked(offset + length);
                            page = checked(page + 1);
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                return new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unavailable,
                    statusMessage: "Repository totals could not be retrieved.");
            }
        }

        private static DevHubRepoSummary Failure(RemoteRepoIdentifier repo, HttpStatusCode status, string signInMessage)
        {
            // A 403 can mean a rate limit, not a bad credential. A 404 may hide a private repo.
            var unauthorized = status == HttpStatusCode.Unauthorized;
            return new DevHubRepoSummary(repo,
                unauthorized ? DevHubRepoSummaryStatus.Unauthorized : DevHubRepoSummaryStatus.Unavailable,
                statusMessage: unauthorized ? signInMessage ?? "Repository access requires sign-in."
                    : $"Repository totals are unavailable (HTTP {(int)status}); check access or retry later.");
        }

        internal static async Task<string> GetIdentityAsync(DevHubCredential credential, string url,
            Func<DevHubCredential, string, CancellationToken, Task<HttpResponseMessage>> send,
            bool gitHub, CancellationToken cancellationToken, string identityProperty = null)
        {
            try
            {
                using (var response = await send(credential, url, cancellationToken))
                {
                    DevHubActivityRequest.Observe(response);
                    if (!response.IsSuccessStatusCode)
                        return null;
                    using (var doc = JsonDocument.Parse(await HttpResponseReader.ReadStringAsync(response, cancellationToken)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var identity = gitHub ? doc.RootElement : doc.RootElement.GetProperty("authenticatedUser");
                        var value = identityProperty == "uuid" ? BitbucketDevHubProvider.GetUserIdentity(identity)
                            : identity.GetProperty(identityProperty ?? (gitHub ? "login" : "id")).GetString();
                        return string.IsNullOrWhiteSpace(value) ? null : value;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
            {
                await ex.LogAsync();
                DevHubActivityRequest.Failed();
                return null;
            }
        }

        internal static bool? IsRequested(JsonElement item, string identity, bool gitHub)
        {
            if (string.IsNullOrEmpty(identity))
                return null;
            if (!item.TryGetProperty(gitHub ? "requested_reviewers" : "reviewers", out var reviewers)
                || reviewers.ValueKind != JsonValueKind.Array)
                return null;

            var unknown = false;
            foreach (var reviewer in reviewers.EnumerateArray())
            {
                if (!reviewer.TryGetProperty(gitHub ? "login" : "id", out var id)
                    || id.ValueKind != JsonValueKind.String)
                {
                    unknown = true;
                    continue;
                }
                if (!string.Equals(id.GetString(), identity, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (gitHub)
                    return true;
                if (reviewer.TryGetProperty("isContainer", out var container) && container.ValueKind == JsonValueKind.True)
                    continue;
                if (!reviewer.TryGetProperty("vote", out var vote) || vote.ValueKind != JsonValueKind.Number
                    || !vote.TryGetInt32(out var value))
                    return null;
                return value == 0;
            }
            return unknown ? (bool?)null : false;
        }
    }
}
