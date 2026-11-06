using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    [DoNotParallelize]
    public class DevHubHttpDisposalTests
    {
        [TestMethod]
        [DataRow(200, "{\"values\":[]}")]
        [DataRow(403, "{}")]
        [DataRow(200, "{")]
        public async Task Bitbucket_PersonalAndRepositoryResponsesAreDisposedOnSuccessHttpFailureAndParseFailureAsync(int status, string body)
        {
            using var handler = new TrackingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/user")
                ? (200, "{\"username\":\"me\"}") : (status, body));
            using var client = new HttpClient(handler);
            var provider = new BitbucketDevHubProvider(client, _ => Task.FromResult(Credential()));

            await provider.GetUserPullRequestsAsync(CancellationToken.None);
            await provider.GetRepoDetailAsync(new RemoteRepoIdentifier("bitbucket.org", "owner", "repo"), 10, CancellationToken.None);

            Assert.IsGreaterThanOrEqualTo(5, handler.Responses.Count);
            AssertDisposed(handler);
            using var stillUsable = await client.GetAsync("https://example.com/client-alive");
        }

        [TestMethod]
        [DataRow(200, "{\"items\":[]}")]
        [DataRow(503, "{}")]
        [DataRow(200, "{")]
        public async Task GitHub_PersonalFeedsDisposeEveryResponseOnAllOutcomesAsync(int status, string body)
        {
            using var handler = new TrackingHandler(_ => (status, body));
            using var client = new HttpClient(handler);

            await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None);
            await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None);

            Assert.IsGreaterThanOrEqualTo(9, handler.Responses.Count);
            AssertDisposed(handler);
            using var stillUsable = await client.GetAsync("https://example.com/client-alive");
        }

        [TestMethod]
        [DataRow(200, "{\"id\":\"me\"}")]
        [DataRow(403, "{}")]
        [DataRow(200, "{")]
        public async Task AzureDevOps_RepositoryAndPersonalFeedsDisposeIdentityPagingPostAndEnrichmentResponsesAsync(int status, string body)
        {
            using var handler = new TrackingHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.Contains("profiles/me"))
                    return (status, body);
                if (path.Contains("connectionData"))
                    return (200, "{\"authenticatedUser\":{\"id\":\"me\"}}");
                if (path.EndsWith("/repositories/repo"))
                    return (200, "{\"id\":\"e2d789d1-67ab-405a-9704-c72759720867\"}");
                if (path.EndsWith("/wiql"))
                    return (status, status == 200 && body != "{" ? "{\"workItems\":[]}" : body);
                if (path.EndsWith("/builds"))
                    return (200, "{\"value\":[{\"id\":1,\"status\":\"completed\",\"result\":\"failed\",\"finishTime\":\"2026-11-04T20:00:00Z\",\"repository\":{\"name\":\"repo\"},\"requestedFor\":{\"id\":\"me\"},\"_links\":{\"web\":{\"href\":\"https://example.com/build\"}}}]}");
                if (path.EndsWith("/timeline"))
                    return (status, status == 200 && body != "{" ? "{\"records\":[]}" : body);
                return (200, "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { },
                _ => Task.FromResult<IReadOnlyList<string>>(["https://dev.azure.com/org/project/_git/repo"]));

            await provider.GetUserForHostAsync("dev.azure.com", "https://app.vssps.visualstudio.com/_apis/profile/profiles/me", CancellationToken.None);
            await provider.GetUserIssuesAsync(CancellationToken.None);
            await provider.GetUserCiRunsAsync(CancellationToken.None);
            await provider.GetRepoDetailAsync(new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project"), 10, CancellationToken.None);

            Assert.IsTrue(handler.Requests.Any(request => request.Method == HttpMethod.Post));
            Assert.IsTrue(handler.Requests.Any(request => request.RequestUri!.AbsolutePath.EndsWith("/timeline")));
            AssertDisposed(handler);
            using var stillUsable = await client.GetAsync("https://example.com/client-alive");
        }

        [TestMethod]
        [DataRow("GitHub", 200)]
        [DataRow("GitHub", 401)]
        [DataRow("GitHub", 503)]
        [DataRow("Bitbucket", 200)]
        [DataRow("Bitbucket", 401)]
        [DataRow("Bitbucket", 503)]
        [DataRow("Azure DevOps", 200)]
        [DataRow("Azure DevOps", 401)]
        [DataRow("Azure DevOps", 503)]
        public async Task Authentication_DisposesRejectedAndRetriedResponsesAsync(string providerName, int retryStatus)
        {
            var requests = 0;
            using var handler = new TrackingHandler(_ => ++requests == 1 ? (401, "{}")
                : (retryStatus, "{\"login\":\"me\",\"username\":\"me\",\"id\":\"me\"}"));
            using var client = new HttpClient(handler);
            if (providerName == "GitHub")
                await new GitHubDevHubProvider().GetAuthenticatedUserAsync(client, Credential(), CancellationToken.None,
                    _ => Task.FromResult(Credential()));
            else if (providerName == "Bitbucket")
                await new BitbucketDevHubProvider(client, _ => Task.FromResult(Credential())).GetAuthenticatedUserAsync(CancellationToken.None);
            else
                await new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { })
                    .GetUserForHostAsync("dev.azure.com", "https://app.vssps.visualstudio.com/_apis/profile/profiles/me", CancellationToken.None);

            Assert.AreEqual(2, requests);
            AssertDisposed(handler);
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Bitbucket")]
        [DataRow("Azure DevOps")]
        public async Task Authentication_DisposesInFlightRequestWhenTransportIsCanceledAsync(string providerName)
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new TrackingHandler(_ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
            using var client = new HttpClient(handler);
            Task<DevHubUser> AuthenticateAsync()
            {
                return providerName == "GitHub"
                    ? new GitHubDevHubProvider().GetAuthenticatedUserAsync(client, Credential(), cancellation.Token)
                    : providerName == "Bitbucket"
                        ? new BitbucketDevHubProvider(client, _ => Task.FromResult(Credential())).GetAuthenticatedUserAsync(cancellation.Token)
                        : new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { })
                            .GetUserForHostAsync("dev.azure.com", "https://app.vssps.visualstudio.com/_apis/profile/profiles/me", cancellation.Token);
            }

            await Assert.ThrowsAsync<OperationCanceledException>(AuthenticateAsync);

            Assert.HasCount(1, handler.Requests);
            Assert.IsEmpty(handler.Responses);
            AssertRequestsDisposed(handler);
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Bitbucket")]
        [DataRow("Azure DevOps")]
        public async Task Authentication_DisposesRejectedResponseWhenCredentialRetryThrowsAsync(string providerName)
        {
            using var handler = new TrackingHandler(_ => (401, "{}"));
            using var client = new HttpClient(handler);
            Task<DevHubCredential> FailAsync(CancellationToken _) => Task.FromException<DevHubCredential>(new InvalidOperationException("Credential retry failed."));
            var credentials = 0;
            if (providerName == "GitHub")
                await new GitHubDevHubProvider().GetAuthenticatedUserAsync(client, Credential(), CancellationToken.None, FailAsync);
            else if (providerName == "Bitbucket")
                await new BitbucketDevHubProvider(client, token => ++credentials == 1 ? Task.FromResult(Credential()) : FailAsync(token))
                    .GetAuthenticatedUserAsync(CancellationToken.None);
            else
                await new AzureDevOpsDevHubProvider(client, (_, _, token) => ++credentials == 1 ? Task.FromResult(Credential()) : FailAsync(token), (_, _) => { })
                    .GetUserForHostAsync("dev.azure.com", "https://app.vssps.visualstudio.com/_apis/profile/profiles/me", CancellationToken.None);

            Assert.HasCount(1, handler.Responses);
            AssertDisposed(handler);
        }

        [TestMethod]
        public async Task AzureDevOps_DisposesSecondUnauthorizedResponseWhenInvalidationThrowsAsync()
        {
            using var handler = new TrackingHandler(_ => (401, "{}"));
            using var client = new HttpClient(handler);
            var invalidations = 0;
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) =>
            {
                if (++invalidations == 2)
                    throw new InvalidOperationException("Invalidation failed.");
            });

            await provider.GetUserForHostAsync("dev.azure.com", "https://app.vssps.visualstudio.com/_apis/profile/profiles/me", CancellationToken.None);

            Assert.HasCount(2, handler.Responses);
            AssertDisposed(handler);
        }

        [TestMethod]
        [DataRow(503, "{}")]
        [DataRow(200, "{")]
        [DataRow(200, "{\"errors\":[{\"message\":\"Unavailable\"}],\"data\":{}}")]
        public async Task GitHub_OptionalMetadataDisposesPostAndDoesNotMarkInventoryStaleAsync(int status, string body)
        {
            using var handler = new TrackingHandler(request => request.Method == HttpMethod.Post ? (status, body)
                : (200, "{\"items\":[{\"number\":1,\"title\":\"Mine\",\"state\":\"open\",\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"created_at\":\"2026-11-04T20:00:00Z\",\"updated_at\":\"2026-11-04T20:00:00Z\",\"html_url\":\"https://github.com/owner/repo/pull/1\",\"user\":{\"login\":\"me\"},\"pull_request\":{}}]}"));
            using var client = new HttpClient(handler);

            var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                new DevHubActivityOptions(), CancellationToken.None), prs => prs.Count > 0, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Success, result.Status);
            Assert.HasCount(1, result.Value);
            Assert.IsNull(result.Value[0].CanMerge);
            Assert.IsTrue(handler.Requests.Any(request => request.Method == HttpMethod.Post));
            AssertDisposed(handler);
        }

        private static DevHubCredential Credential() => new("me", "test-token");

        private static void AssertDisposed(TrackingHandler handler)
        {
            Assert.IsNotEmpty(handler.Responses);
            Assert.IsTrue(handler.Responses.All(response => response.IsDisposed));
            AssertRequestsDisposed(handler);
        }

        private static void AssertRequestsDisposed(TrackingHandler handler)
        {
            foreach (var request in handler.Requests)
            {
                using var content = new StringContent(string.Empty);
                Assert.ThrowsExactly<ObjectDisposedException>(() => request.Content = content);
            }
        }

        private sealed class TrackingResponse(int status, string body) : HttpResponseMessage((HttpStatusCode)status)
        {
            public bool IsDisposed { get; private set; }
            public void Initialize() => Content = new StringContent(body);
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    IsDisposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingHandler(Func<HttpRequestMessage, (int Status, string Body)> respond) : HttpMessageHandler
        {
            public ConcurrentBag<TrackingResponse> Responses { get; } = [];
            public ConcurrentBag<HttpRequestMessage> Requests { get; } = [];
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                var (status, body) = respond(request);
                var response = new TrackingResponse(status, body);
                response.Initialize();
                Responses.Add(response);
                return Task.FromResult<HttpResponseMessage>(response);
            }
        }
    }
}