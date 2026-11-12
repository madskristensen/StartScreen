using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class GitHubAggregateDashboardTests
    {
        [TestMethod]
        [DataRow(1)]
        [DataRow(100)]
        public async Task Dashboard_SearchBudgetDependsOnOwnersNotWorkspaceRepositoryCountAsync(int repositories)
        {
            var searches = new ConcurrentBag<string>();
            using var client = new HttpClient(new Handler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.StartsWith("/users/", StringComparison.Ordinal))
                    return Task.FromResult(Json("{\"type\":\"Organization\"}"));
                Assert.AreEqual("/search/issues", path, "Dashboard PRs/issues must not fetch per-repository detail endpoints.");
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                searches.Add(query);
                return Task.FromResult(Json(query.Contains("org:team") ? Items(query.Contains("is:pr")) : "{\"items\":[]}"));
            }));
            var options = new DevHubActivityOptions(refreshContext: new DevHubRefreshContext(
                Enumerable.Range(0, repositories).Select(index => "https://github.com/team/repo" + index)));
            var pulls = GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", options,
                CancellationToken.None, populateMetadata: false, includeWorkspaceOwners: true);
            var issues = GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me", options,
                CancellationToken.None, includeWorkspaceOwners: true);
            await Task.WhenAll(pulls, issues);

            Assert.HasCount(11, searches);
            Assert.AreEqual(2, searches.Count(query => query.Contains("org:team")));
            Assert.IsFalse(searches.Any(query => query.Contains("repo:")));
            Assert.HasCount(1, await pulls);
            Assert.HasCount(1, await issues);
            Assert.IsFalse((await pulls).Single().IsAuthoredByCurrentUser);
            Assert.AreEqual("outside-mru", (await pulls).Single().RepoIdentifier.Repo);
            Assert.IsEmpty(await new GitHubDevHubProvider().GetActivityReposAsync(options.RefreshContext.RemoteUrls, CancellationToken.None));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "All waits observe HTTP tasks owned by this test.")]
        public async Task OwnerSearches_RunConcurrentlyAndPublishBeforeSlowerOwnersAsync(bool pullRequests)
        {
            var first = Signal();
            var second = Signal();
            var published = Signal();
            var release = Signal();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.StartsWith("/users/", StringComparison.Ordinal))
                    return Json("{\"type\":\"User\"}");
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (query.Contains("user:first"))
                {
                    first.TrySetResult(true);
                    await release.Task.WithCancellation(token);
                }
                if (query.Contains("user:second"))
                {
                    second.TrySetResult(true);
                    return Json(Items(pullRequests));
                }
                return Json("{\"items\":[]}");
            }));
            var options = new DevHubActivityOptions(refreshContext: new DevHubRefreshContext(
                ["https://github.com/first/one", "https://github.com/second/two"]), reportProgress: snapshot =>
                {
                    if (snapshot.PullRequests.Count + snapshot.Issues.Count > 0)
                        published.TrySetResult(true);
                });
            var operation = pullRequests
                ? ReadPullsAsync(client, options, timeout.Token)
                : ReadIssuesAsync(client, options, timeout.Token);
            try
            {
                await Task.WhenAll(first.Task, second.Task, published.Task).WithCancellation(timeout.Token);
                Assert.IsFalse(operation.IsCompleted, "A slow owner must not block other owner searches or their inventories.");
            }
            finally
            {
                release.TrySetResult(true);
                await operation;
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "All waits observe HTTP tasks owned by this test.")]
        public async Task PersonalSearches_PublishWithoutWaitingForAnotherSearchOrMetadataAsync(bool pullRequests)
        {
            var started = Signal();
            var published = Signal();
            var release = Signal();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                if (query.Contains("author:me"))
                {
                    started.TrySetResult(true);
                    await release.Task.WithCancellation(token);
                }
                return Json(query.Contains("assignee:me") ? Items(pullRequests, "me") : "{\"items\":[]}");
            }));
            var options = new DevHubActivityOptions(reportProgress: snapshot =>
            {
                if (snapshot.PullRequests.Count + snapshot.Issues.Count > 0)
                    published.TrySetResult(true);
            });
            var operation = pullRequests ? ReadPullsAsync(client, options, timeout.Token) : ReadIssuesAsync(client, options, timeout.Token);
            try
            {
                await Task.WhenAll(started.Task, published.Task).WithCancellation(timeout.Token);
                Assert.IsFalse(operation.IsCompleted);
            }
            finally
            {
                release.TrySetResult(true);
                await operation;
            }
        }

        [TestMethod]
        public async Task CustomQuery_DoesNotAddUnfilteredOwnerSearchesAsync()
        {
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Assert.AreEqual("/search/issues", request.RequestUri!.AbsolutePath);
                Assert.DoesNotContain("org:", Uri.UnescapeDataString(request.RequestUri.Query));
                return Task.FromResult(Json("{\"items\":[]}"));
            }));
            var options = new DevHubActivityOptions(refreshContext: new DevHubRefreshContext(["https://github.com/team/repo"]));
            await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", options, CancellationToken.None,
                customQuery: "label:bug", populateMetadata: false, includeWorkspaceOwners: true);
            await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me", options, CancellationToken.None,
                customQuery: "label:bug", includeWorkspaceOwners: true);
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "All waits observe HTTP tasks owned by this test.")]
        public async Task Builds_RetainWorkspaceCoverageAndPublishWhileAnotherRepositoryIsPendingAsync()
        {
            var release = Signal();
            var started = Signal();
            var published = Signal();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var requests = new ConcurrentBag<string>();
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                requests.Add(path);
                if (path == "/user/repos")
                    return Json("[]");
                if (path == "/repos/first/repo/actions/runs")
                {
                    started.TrySetResult(true);
                    await release.Task.WithCancellation(token);
                    return Json("{\"workflow_runs\":[]}");
                }
                Assert.AreEqual("/repos/second/repo/actions/runs", path);
                return Json("{\"workflow_runs\":[{\"name\":\"Build\",\"status\":\"completed\",\"conclusion\":\"success\",\"updated_at\":\"2026-11-12T10:00:00Z\",\"html_url\":\"https://github.com/second/repo/actions/runs/1\"}]}");
            }));
            var options = new DevHubActivityOptions(refreshContext: new DevHubRefreshContext(
                ["https://github.com/first/repo", "https://github.com/second/repo"]), reportProgress: snapshot =>
                {
                    if (snapshot.CiRuns.Count > 0)
                        published.TrySetResult(true);
                });
            var operation = GitHubDevHubProvider.GetUserCiRunsAsync(client, Credential(), "me", options, timeout.Token);
            try
            {
                await Task.WhenAll(started.Task, published.Task).WithCancellation(timeout.Token);
                Assert.IsFalse(operation.IsCompleted);
            }
            finally
            {
                release.TrySetResult(true);
                await operation;
            }
            Assert.HasCount(3, requests);
            Assert.HasCount(1, await operation);
        }

        [TestMethod]
        public async Task OwnerFailure_IsExplicitWithoutDroppingOtherOwnersAsync()
        {
            using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/users/broken" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : request.RequestUri.AbsolutePath.StartsWith("/users/", StringComparison.Ordinal) ? Json("{\"type\":\"Organization\"}")
                    : Json(Uri.UnescapeDataString(request.RequestUri.Query).Contains("org:working") ? Items(false) : "{\"items\":[]}"))));
            var options = new DevHubActivityOptions(refreshContext: new DevHubRefreshContext(
                ["https://github.com/broken/repo", "https://github.com/working/repo"]));
            var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                options, CancellationToken.None, includeWorkspaceOwners: true), items => items.Count > 0, CancellationToken.None);
            Assert.AreEqual(DevHubActivityStatus.Partial, result.Status);
            Assert.HasCount(1, result.Value);
            Assert.Contains("503", result.Message);
        }

        [TestMethod]
        public void Inventory_DoesNotMutatePublishedIssuesOrBuildsDuringLaterEnrichment()
        {
            var issue = new DevHubIssue { IsMentioned = false };
            var run = new DevHubCiRun();
            DevHubActivitySnapshot? preview = null;
            DevHubActivityRequest.ReportInventory(new DevHubActivityOptions(reportProgress: snapshot => preview = snapshot),
                issues: [issue], runs: [run]);
            issue.IsMentioned = true;
            issue.Labels.Add(new DevHubLabel { Name = "Later label" });
            run.FailureSummary = "Later job details";

            Assert.IsNotNull(preview);
            Assert.IsFalse(preview.Issues.Single().IsMentioned);
            Assert.IsEmpty(preview.Issues.Single().Labels);
            Assert.IsNull(preview.CiRuns.Single().FailureSummary);
        }

        private static async Task ReadPullsAsync(HttpClient client, DevHubActivityOptions options, CancellationToken token)
            => await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", options, token, includeWorkspaceOwners: true);
        private static async Task ReadIssuesAsync(HttpClient client, DevHubActivityOptions options, CancellationToken token)
            => await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me", options, token, includeWorkspaceOwners: true);
        private static DevHubCredential Credential() => new("me", "synthetic-test-token");
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
        private static string Items(bool pullRequests, string author = "teammate") => "{\"items\":[{\"number\":1,\"title\":\"Loaded item\",\"state\":\"open\",\"user\":{\"login\":\"" + author
            + "\"},\"created_at\":\"2026-11-12T10:00:00Z\",\"updated_at\":\"2026-11-12T10:00:00Z\",\"repository_url\":\"https://api.github.com/repos/team/outside-mru\",\"html_url\":\"https://github.com/team/outside-mru/"
            + (pullRequests ? "pull/1\",\"pull_request\":{}" : "issues/1\"") + "}]}";
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        }
    }
}
