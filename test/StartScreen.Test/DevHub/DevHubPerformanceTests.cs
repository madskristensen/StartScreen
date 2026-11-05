using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubPerformanceTests
    {
        [TestMethod]
        public async Task CiRuns_UsesThreeConcurrentRequestsAndPreservesSortedResultsAsync()
        {
            var started = Signal();
            var release = Signal();
            var active = 0;
            var requests = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new AsyncHandler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/user/repos")
                    return Repositories();
                Interlocked.Increment(ref requests);
                var concurrent = Interlocked.Increment(ref active);
                Assert.IsLessThanOrEqualTo(3, concurrent);
                if (concurrent == 3)
                    started.TrySetResult(true);
                try
                {
                    await Task.WhenAny(release.Task, Task.Delay(Timeout.Infinite, token));
                    token.ThrowIfCancellationRequested();
                    var index = int.Parse(request.RequestUri.Segments[3].Trim('/').Substring(4));
                    return Json("{\"workflow_runs\":[{\"name\":\"Build\",\"status\":\"completed\",\"conclusion\":\"success\"," +
                        $"\"updated_at\":\"2026-09-0{index + 1}T00:00:00Z\",\"html_url\":\"https://example.com/run\"}}]}}");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }));
            var operation = new GitHubDevHubProvider().GetUserCiRunsAsync(client, Credential(), timeout.Token);
            try
            {
                await AwaitSignalAsync(started.Task);
                Assert.AreEqual(3, requests);
                Assert.IsFalse(operation.IsCompleted);
            }
            finally
            {
                release.TrySetResult(true);
            }
            var runs = await operation;

            Assert.AreEqual(5, requests);
            Assert.AreEqual(0, active);
            CollectionAssert.AreEqual(new[] { "repo4", "repo3", "repo2", "repo1", "repo0" },
                runs.Select(run => run.RepoIdentifier.Repo).ToArray());
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The test awaits its own HTTP operation without a UI synchronization context.")]
        public async Task CiRuns_CancellationStopsQueuedRequestsAsync()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var started = Signal();
            var requests = 0;
            using var client = new HttpClient(new AsyncHandler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/user/repos")
                    return Repositories();
                if (Interlocked.Increment(ref requests) == 3)
                    started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Json("{}");
            }));
            var operation = new GitHubDevHubProvider().GetUserCiRunsAsync(client, Credential(), cancellation.Token);

            await AwaitSignalAsync(started.Task);
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            Assert.AreEqual(3, requests);
        }

        [TestMethod]
        public async Task CiRuns_OneUnavailableRepositoryDoesNotDropOtherResultsAsync()
        {
            using var client = new HttpClient(new AsyncHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/user/repos" ? Repositories()
                : request.RequestUri.AbsolutePath.Contains("repo2") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("{\"workflow_runs\":[{\"name\":\"Build\",\"status\":\"completed\",\"updated_at\":\"2026-09-01T00:00:00Z\",\"html_url\":\"https://example.com\"}]}"))));

            var runs = await new GitHubDevHubProvider().GetUserCiRunsAsync(client, Credential(), CancellationToken.None);

            Assert.HasCount(4, runs);
            Assert.IsFalse(runs.Any(run => run.RepoIdentifier.Repo == "repo2"));
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The test awaits its own cache operations without a UI synchronization context.")]
        public async Task IdentityCache_CoalescesConcurrentLookupsAndIsolatesCanceledWaitersAsync()
        {
            var cache = new DevHubIdentityCache(credentialGeneration: () => 0);
            var release = Signal();
            var requests = 0;
            async Task<HttpResponseMessage> SendAsync(DevHubCredential _, string url, CancellationToken token)
            {
                Interlocked.Increment(ref requests);
                await release.Task;
                return Json("{\"login\":\"me\"}");
            }
            var first = cache.GetAsync(Credential(), "https://api.github.com/user", SendAsync, true, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var canceled = cache.GetAsync(Credential(), "https://api.github.com/user", SendAsync, true, cancellation.Token);
            var other = cache.GetAsync(Credential(), "https://api.github.com/user", SendAsync, true, CancellationToken.None);
            cancellation.Cancel();
            try
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
                Assert.AreEqual(1, requests);
            }
            finally
            {
                release.TrySetResult(true);
            }

            CollectionAssert.AreEqual(new[] { "me", "me" }, await Task.WhenAll(first, other));
            Assert.AreEqual(1, requests);
        }

        [TestMethod]
        public async Task IdentityCache_SeparatesCredentialsHostsAndOrganizationsAsync()
        {
            var cache = new DevHubIdentityCache(credentialGeneration: () => 0);
            var calls = 0;
            Task<HttpResponseMessage> SendAsync(DevHubCredential _, string url, CancellationToken token)
                => Task.FromResult(Json($"{{\"authenticatedUser\":{{\"id\":\"user{++calls}\"}}}}"));
            const string firstUrl = "https://dev.azure.com/one/_apis/connectionData";
            var first = await cache.GetAsync(Credential(), firstUrl, SendAsync, false, CancellationToken.None);
            var cached = await cache.GetAsync(Credential(), firstUrl, SendAsync, false, CancellationToken.None);
            var organization = await cache.GetAsync(Credential(), "https://dev.azure.com/two/_apis/connectionData", SendAsync, false, CancellationToken.None);
            var tokenChanged = await cache.GetAsync(new DevHubCredential("account", "changed-test-token"), firstUrl, SendAsync, false, CancellationToken.None);
            var accountChanged = await cache.GetAsync(new DevHubCredential("other", "test-token"), firstUrl, SendAsync, false, CancellationToken.None);

            Assert.AreEqual("user1", first);
            Assert.AreEqual(first, cached);
            Assert.AreEqual("user2", organization);
            Assert.AreEqual("user3", tokenChanged);
            Assert.AreEqual("user4", accountChanged);
            Assert.AreEqual(4, calls);
        }

        [TestMethod]
        public async Task IdentityCache_ExpiresAndInvalidatesAfterAuthenticationChangesAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var generation = 0;
            var cache = new DevHubIdentityCache(() => now, () => generation);
            var calls = 0;
            Task<HttpResponseMessage> SendAsync(DevHubCredential _, string url, CancellationToken token)
                => Task.FromResult(Json($"{{\"login\":\"user{++calls}\"}}"));
            async Task<string> GetAsync() => await cache.GetAsync(Credential(), "https://api.github.com/user", SendAsync, true, CancellationToken.None);

            Assert.AreEqual("user1", await GetAsync());
            now = now.AddMinutes(4);
            Assert.AreEqual("user1", await GetAsync());
            now = now.AddMinutes(1);
            Assert.AreEqual("user2", await GetAsync());
            generation++;
            Assert.AreEqual("user3", await GetAsync());
            Assert.AreEqual(3, calls);
        }

        [TestMethod]
        public async Task IdentityCache_DoesNotCacheFailuresAsync()
        {
            var cache = new DevHubIdentityCache(credentialGeneration: () => 0);
            var calls = 0;
            Task<HttpResponseMessage> SendAsync(DevHubCredential _, string url, CancellationToken token)
                => Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json("{\"login\":\"me\"}"));

            Assert.IsNull(await cache.GetAsync(Credential(), "https://api.github.com/user", SendAsync, true, CancellationToken.None));
            Assert.AreEqual("me", await cache.GetAsync(Credential(), "https://api.github.com/user", SendAsync, true, CancellationToken.None));
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task RepositorySummaries_ReuseIdentityAcrossRepositoriesAsync(bool gitHub)
        {
            var identityRequests = 0;
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/user") || request.RequestUri.AbsolutePath.EndsWith("/connectionData"))
                {
                    identityRequests++;
                    return Json(gitHub ? "{\"login\":\"me\"}" : "{\"authenticatedUser\":{\"id\":\"me\"}}");
                }
                return Json(gitHub ? "[]" : "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var credential = Credential();
            var github = new GitHubDevHubProvider();
            var ado = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(credential), (_, _) => { });
            foreach (var name in new[] { "one", "two" })
            {
                var repo = new RemoteRepoIdentifier(gitHub ? "github.com" : "dev.azure.com", "owner", name, gitHub ? null : "project");
                var summary = gitHub
                    ? await github.GetRepoSummaryAsync(client, credential, repo, CancellationToken.None)
                    : await ado.GetRepoSummaryAsync(repo, CancellationToken.None);
                Assert.AreEqual(DevHubRepoSummaryStatus.Ready, summary.Status);
                Assert.AreEqual(0, summary.RequestedReviews);
            }
            Assert.AreEqual(1, identityRequests);
            Assert.AreEqual(3, handler.RequestCount);
        }

        private static DevHubCredential Credential() => new DevHubCredential("account", "test-token");
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The signal is a test-owned completion source and has no UI-thread dependency.")]
        private static async Task AwaitSignalAsync(Task signal)
        {
            Assert.AreSame(signal, await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(10))));
            await signal;
        }

        private static HttpResponseMessage Repositories() => Json("[" + string.Join(",", Enumerable.Range(0, 5)
            .Select(index => $"{{\"full_name\":\"owner/repo{index}\"}}")) + "]");
        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

        private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => send(request, cancellationToken);
        }
    }
}