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
    public class GitHubRepositorySchedulingTests
    {
        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The waits observe test-owned HTTP requests without a UI synchronization context.")]
        public async Task RepositoryCategories_StartBeforePullRequestsFinishAsync()
        {
            var release = Signal();
            var issues = Signal();
            var builds = Signal();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/pulls", StringComparison.Ordinal))
                    await release.Task.WithCancellation(token);
                if (path == "/repos/owner/repo/issues")
                    issues.TrySetResult(true);
                if (path.EndsWith("/actions/runs", StringComparison.Ordinal))
                    builds.TrySetResult(true);
                return Json(path == "/search/issues" ? "{\"items\":[]}"
                    : path.EndsWith("/actions/runs", StringComparison.Ordinal) ? "{\"workflow_runs\":[]}" : "[]");
            }));
            var operation = FetchAsync(client, timeout.Token, "me");
            try
            {
                await Task.WhenAll(issues.Task, builds.Task).WithCancellation(timeout.Token);
                Assert.IsFalse(operation.IsCompleted, "Issue and build reads must not wait for the pull request lane.");
            }
            finally
            {
                release.TrySetResult(true);
                await operation;
            }
        }

        [TestMethod]
        public async Task RepositoryCategories_PreserveAllFailureOutcomesAndWarningsAsync()
        {
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                await Task.Delay(20, token);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            }));

            var detail = await FetchAsync(client, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Unavailable, detail.Status);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, detail.PullRequestsStatus);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, detail.IssuesStatus);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, detail.CiRunsStatus);
            Assert.IsTrue(detail.HasCategoryOutcomes);
            Assert.Contains("/pulls returned HTTP 503", detail.ActivityWarning);
            Assert.Contains("/issues returned HTTP 503", detail.ActivityWarning);
            Assert.Contains("/actions/runs returned HTTP 503", detail.ActivityWarning);
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The wait observes test-owned HTTP requests without a UI synchronization context.")]
        public async Task RepositoryCategories_CancellationDrainsAllRequestsAsync()
        {
            var started = Signal();
            var requests = 0;
            var active = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (_, token) =>
            {
                Interlocked.Increment(ref active);
                if (Interlocked.Increment(ref requests) == 3)
                    started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return Json("[]");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }));
            var operation = FetchAsync(client, cancellation.Token);
            try
            {
                await started.Task.WithCancellation(cancellation.Token);
            }
            finally
            {
                cancellation.Cancel();
            }
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            Assert.AreEqual(3, requests);
            Assert.AreEqual(0, active);
        }

        private static Task<DevHubRepoDetail> FetchAsync(HttpClient client, CancellationToken token, string? login = null)
            => GitHubDevHubProvider.GetRepoDetailAsync(client, new DevHubCredential("account", "test-token"), login!,
                new RemoteRepoIdentifier("github.com", "owner", "repo"), new DevHubActivityOptions(), token);
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => send(request, cancellationToken);
        }
    }
}
