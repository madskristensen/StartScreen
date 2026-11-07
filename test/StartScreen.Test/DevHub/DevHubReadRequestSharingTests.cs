using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.Threading;
using StartScreen.Helpers;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubReadRequestSharingTests
    {
        [TestMethod]
        public async Task SeparateAzureDevOpsProvidersShareInventoryAndReadOnlyWiqlQueriesAsync()
        {
            var requests = new ConcurrentDictionary<string, int>();
            using var handler = new Handler(async (request, token) =>
            {
                var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
                requests.AddOrUpdate(request.Method + " " + request.RequestUri + " " + body, 1, (_, count) => count + 1);
                await Task.Delay(100, token);
                var path = request.RequestUri!.AbsolutePath;
                return Json(path.Contains("connectionData") ? "{\"authenticatedUser\":{\"id\":\"me\"}}"
                    : path.EndsWith("/repositories/repo") ? "{\"id\":\"e2d789d1-67ab-405a-9704-c72759720867\"}"
                    : path.EndsWith("/workitemtypes") ? "{\"value\":[{\"name\":\"Bug\"}]}"
                    : path.EndsWith("/states") ? "{\"value\":[{\"name\":\"Active\",\"category\":\"InProgress\"}]}"
                    : path.EndsWith("/wiql") ? "{\"workItems\":[]}" : "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var credential = Credential();
            var first = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(credential), (_, _) => { });
            var second = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(credential), (_, _) => { });
            var repo = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");
            var results = await Task.WhenAll(first.GetRepoDetailAsync(repo, new DevHubActivityOptions(), default),
                second.GetRepoDetailAsync(repo, new DevHubActivityOptions(), default));
            Assert.IsTrue(requests.Keys.Any(key => key.StartsWith("POST ") && key.Contains("/wiql")));
            Assert.IsTrue(requests.All(pair => pair.Value == 1), "Two providers must share both inventory GETs and exact WIQL bodies.");
            Assert.AreEqual(DevHubActivityStatus.Success, results[0].Status);
            Assert.AreEqual(DevHubActivityStatus.Success, results[1].Status);
            Assert.AreNotSame(results[0], results[1]);
        }

        [TestMethod]
        public async Task NetworkExceptionsAreSharedOnlyInFlightAndLastWaiterCancellationDisposesBodyAsync()
        {
            var sharing = new DevHubReadRequestSharing();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fail = true;
            using var handler = new Handler(async (_, token) =>
            {
                await pending.Task.WithCancellation(token);
                if (fail)
                    throw new IOException("Network unavailable");
                return Json("{}");
            });
            using var client = new HttpClient(handler);
            var credential = Credential();
            using var firstRequest = Request();
            using var secondRequest = Request();
            var first = sharing.SendAsync(client, firstRequest, credential, default);
            var second = sharing.SendAsync(client, secondRequest, credential, default);
            pending.SetResult(true);
#pragma warning disable VSTHRD003 // Inspect independently owned waiter failures, not a joinable UI operation.
            await Assert.ThrowsAsync<IOException>(async () => await first);
            await Assert.ThrowsAsync<IOException>(async () => await second);
#pragma warning restore VSTHRD003
            Assert.AreEqual(1, handler.Count);
            fail = false;
            using var retryRequest = Request();
            using var response = await sharing.SendAsync(client, retryRequest, credential, default);
            Assert.AreEqual(2, handler.Count);

            using var stream = new HttpResponseReaderTests.BlockingStream();
            using var bodyHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(stream) }));
            using var bodyClient = new HttpClient(bodyHandler);
            using var bodyRequest = Request();
            using var cancellation = new CancellationTokenSource();
            var read = sharing.SendAsync(bodyClient, bodyRequest, credential, cancellation.Token);
            await stream.ReadStarted.Task;
            cancellation.Cancel();
#pragma warning disable VSTHRD003 // Verify cancellation of this separately buffered read.
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
#pragma warning restore VSTHRD003
            await HttpResponseReaderTests.MustCompleteAsync(stream.Disposed.Task);
            Assert.IsTrue(stream.IsDisposed);
        }

        [TestMethod]
        public async Task SeparateInventoryWorkflowsShareSearchAndGraphQlButOwnTheirModelsAndRefreshReadinessAsync()
        {
            var requests = new ConcurrentDictionary<string, int>();
            var ready = true;
            using var handler = new Handler(async (request, token) =>
            {
                var key = request.Method + " " + request.RequestUri;
                requests.AddOrUpdate(key, 1, (_, count) => count + 1);
                await Task.Delay(100, token);
                if (request.Method == HttpMethod.Post)
                    return Json(Metadata(ready));
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                return Json(query.Contains("author:me") ? "{\"items\":[" + PullRequest() + "]}" : "{\"items\":[]}");
            });
            using var client = new HttpClient(handler);
            var credential = Credential();
            Task<List<DevHubPullRequest>> Load() => GitHubDevHubProvider.GetUserPullRequestsAsync(client, credential, "me",
                new DevHubActivityOptions(), default);
            var results = await Task.WhenAll(Load(), Load());
            Assert.IsGreaterThan(1, requests.Count);
            Assert.IsTrue(requests.All(pair => pair.Value == 1), "Both inventory and enrichment HTTP reads must be shared.");
            Assert.IsTrue(requests.Keys.Any(key => key.Contains("graphql")));
            Assert.IsTrue(results[0].Single().CanMerge);
            Assert.IsTrue(results[1].Single().CanMerge);
            Assert.AreNotSame(results[0].Single(), results[1].Single());
            results[0].Single().Title = "changed in one screen";
            Assert.AreEqual("Activity", results[1].Single().Title);
            ready = false;
            var refreshed = await Load();
            Assert.IsFalse(refreshed.Single().CanMerge, "A later manual refresh must not reuse ready flags.");
            Assert.IsTrue(requests.All(pair => pair.Value == 2), "Completed responses must never satisfy later requests.");
        }

        [TestMethod]
        public async Task SharedResponsesHaveIndependentOwnershipAndPreserveHeadersAndBodyAsync()
        {
            var sharing = new DevHubReadRequestSharing();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TrackingContent? content = null;
            using var handler = new Handler(async (_, token) =>
            {
                await pending.Task.WithCancellation(token);
                content = new TrackingContent("payload");
                var response = new HttpResponseMessage((HttpStatusCode)429) { Content = content };
                response.Headers.TryAddWithoutValidation("Retry-After", "30");
                response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", "next");
                return response;
            });
            using var client = new HttpClient(handler);
            using var firstRequest = Request();
            using var secondRequest = Request();
            var credential = Credential();
            var first = sharing.SendAsync(client, firstRequest, credential, default);
            var second = sharing.SendAsync(client, secondRequest, credential, default);
            pending.SetResult(true);
            using var firstResponse = await first;
            using var secondResponse = await second;
            Assert.AreEqual(1, handler.Count);
            Assert.IsTrue(content!.Disposed);
            Assert.AreEqual((HttpStatusCode)429, secondResponse.StatusCode);
            Assert.AreEqual(TimeSpan.FromSeconds(30), secondResponse.Headers.RetryAfter!.Delta);
            Assert.AreEqual("next", secondResponse.Headers.GetValues("x-ms-continuationtoken").Single());
            firstResponse.Dispose();
            Assert.AreEqual("payload", await HttpResponseReader.ReadStringAsync(secondResponse, default));
            using var retryRequest = Request();
            using var retryResponse = await sharing.SendAsync(client, retryRequest, credential, default);
            Assert.AreEqual(2, handler.Count, "Failures are shared only while in flight, never cached.");
        }

        [TestMethod]
        [DataRow("account")]
        [DataRow("username")]
        [DataRow("url")]
        [DataRow("headers")]
        [DataRow("body")]
        [DataRow("transport")]
        [DataRow("write")]
        public async Task DistinctRequestContextsAndWritesAreNotSharedAsync(string difference)
        {
            var sharing = new DevHubReadRequestSharing();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler(async (_, token) => { await pending.Task.WithCancellation(token); return Json("{}"); });
            using var firstClient = new HttpClient(handler, disposeHandler: false);
            using var secondClient = new HttpClient(handler, disposeHandler: false);
            using var firstRequest = Request(post: difference is "body" or "write");
            using var secondRequest = Request(post: difference is "body" or "write");
            if (difference == "url")
                secondRequest.RequestUri = new Uri("https://example.com/inventory?query=other");
            if (difference == "headers")
                secondRequest.Headers.TryAddWithoutValidation("Accept", "application/xml");
            if (difference == "body")
                secondRequest.Content = new StringContent("{\"query\":\"different\"}");
            var credential = Credential();
            var first = sharing.SendAsync(firstClient, firstRequest, credential, default, readOnlyPost: difference == "body");
            var second = sharing.SendAsync(difference == "transport" ? secondClient : firstClient, secondRequest,
                difference == "account" ? Credential() : difference == "username"
                    ? new DevHubCredential("other account", credential.Token) : credential, default, readOnlyPost: difference == "body");
            pending.SetResult(true);
            using var firstResponse = await first;
            using var secondResponse = await second;
            Assert.AreEqual(2, handler.Count);
        }

        [TestMethod]
        public async Task CancelingOneWaiterDoesNotCancelAnotherAndShutdownStopsSharedTransportAsync()
        {
            using var shutdown = new CancellationTokenSource();
            var sharing = new DevHubReadRequestSharing(() => shutdown.Token);
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler(async (_, token) =>
            {
                started.TrySetResult(true);
                await pending.Task.WithCancellation(token);
                return Json("{}");
            });
            using var client = new HttpClient(handler);
            using var firstRequest = Request();
            using var secondRequest = Request();
            using var canceled = new CancellationTokenSource();
            var credential = Credential();
            var first = sharing.SendAsync(client, firstRequest, credential, canceled.Token);
            var second = sharing.SendAsync(client, secondRequest, credential, default);
            await started.Task;
            canceled.Cancel();
#pragma warning disable VSTHRD003 // Independent waiter tasks are intentionally inspected to verify sharing and cancellation.
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
            Assert.IsFalse(second.IsCompleted);
            pending.SetResult(true);
            using var response = await second;
            Assert.AreEqual(1, handler.Count);
            pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var thirdRequest = Request();
            using var fourthRequest = Request();
            var third = sharing.SendAsync(client, thirdRequest, credential, default);
            var fourth = sharing.SendAsync(client, fourthRequest, credential, default);
            await started.Task;
            shutdown.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await third);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await fourth);
#pragma warning restore VSTHRD003
            Assert.AreEqual(2, handler.Count);
        }

        private static DevHubCredential Credential() => new DevHubCredential("account", Guid.NewGuid().ToString());
        private static HttpRequestMessage Request(bool post = false)
        {
            var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, "https://example.com/inventory?query=one");
            if (post)
                request.Content = new StringContent("{\"query\":\"same\"}");
            return request;
        }

        private static HttpResponseMessage Json(string json) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static string PullRequest() => "{\"number\":1,\"title\":\"Activity\",\"user\":{\"login\":\"me\"},\"state\":\"open\","
            + "\"assignees\":[],\"created_at\":\"2026-11-01T00:00:00Z\",\"updated_at\":\"2026-11-01T00:00:00Z\","
            + "\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"html_url\":\"https://github.com/owner/repo/pull/1\"}";

        private static string Metadata(bool ready) => JsonSerializer.Serialize(new
        {
            data = new { r0 = new { pullRequest = new
            {
                state = "OPEN", isDraft = false, mergeable = "MERGEABLE", mergeStateStatus = ready ? "CLEAN" : "BLOCKED",
                reviewDecision = "APPROVED", headRefOid = "commit",
                reviewThreads = new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false } },
                commits = new { nodes = new[] { new { commit = new
                {
                    oid = "commit", statusCheckRollup = new { state = "SUCCESS",
                        contexts = new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false } } },
                } } } },
            } } },
        });

        private sealed class TrackingContent(string text) : StringContent(text)
        {
            internal bool Disposed { get; private set; }
            protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
        }

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            private int _count;
            internal int Count => _count;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _count);
                return send(request, cancellationToken);
            }
        }
    }
}
