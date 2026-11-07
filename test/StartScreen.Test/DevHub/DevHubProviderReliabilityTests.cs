using System.Net;
using System.Net.Http;
using System.Text.Json;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubProviderReliabilityTests
    {
        [TestMethod]
        [DataRow(null, false, false)]
        [DataRow("https://github.com/owner/repo", false, false)]
        [DataRow("https://github.com/bitbucket.org/repo", false, false)]
        [DataRow("https://bitbucket.org/owner/repo", false, true)]
        [DataRow("git@bitbucket.org:owner/repo.git", false, true)]
        [DataRow(null, true, true)]
        public async Task BitbucketRefresh_ProbesOnlyForMatchingRepositoryOrStoredCredentialAsync(string? remote, bool stored, bool expected)
        {
            using var directory = new TemporaryDirectory();
            var requests = 0;
            var acquisitions = 0;
            using var handler = new Handler(_ =>
            {
                requests++;
                return Json("{\"uuid\":\"{me}\",\"username\":\"me\",\"values\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = new BitbucketDevHubProvider(client, _ =>
            {
                acquisitions++;
                return Task.FromResult(Credential());
            }, hasStoredCredential: () => stored);
            var service = new DevHubService(new DevHubCacheService(directory.Path), [provider],
                _ => Task.FromResult<IReadOnlyList<string>>(remote == null ? [] : [remote]),
                () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);

            var dashboard = await service.RefreshAsync(CancellationToken.None);

            Assert.AreEqual(expected, dashboard.HasAuthentication);
            Assert.IsEmpty(dashboard.ActivityWarnings);
            if (expected)
            {
                Assert.IsGreaterThan(0, acquisitions);
                Assert.IsGreaterThan(0, requests);
            }
            else
            {
                Assert.AreEqual(0, acquisitions, "Unused Bitbucket must not ask credential helpers for a token.");
                Assert.AreEqual(0, requests, "Unused Bitbucket must not make network requests.");
            }
        }

        [TestMethod]
        public async Task GitHubRepositoryScope_DoesNotProbeUnusedBitbucketAsync()
        {
            using var directory = new TemporaryDirectory();
            using var client = new HttpClient(new Handler(_ => throw new AssertFailedException("Bitbucket must not send HTTP requests.")));
            var provider = new BitbucketDevHubProvider(client,
                _ => throw new AssertFailedException("Bitbucket must not ask credential helpers for a token."), hasStoredCredential: () => false);
            var service = new DevHubService(new DevHubCacheService(directory.Path), [provider],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);

            var dashboard = await service.ScopeToRepoAsync(new RemoteRepoIdentifier("github.com", "owner", "repo"), CancellationToken.None);

            Assert.IsFalse(dashboard.HasAuthentication);
            Assert.IsEmpty(dashboard.Users);
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Bitbucket")]
        [DataRow("Azure DevOps")]
        public async Task AuthenticationBodyCancellationDisposesStalledTransportAsync(string providerName)
        {
            using var stream = new HttpResponseReaderTests.BlockingStream();
            using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            using var client = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var assertion = Assert.ThrowsAsync<OperationCanceledException>(() => providerName == "GitHub"
                ? new GitHubDevHubProvider().GetAuthenticatedUserAsync(client, Credential(), cancellation.Token)
                : providerName == "Bitbucket"
                    ? new BitbucketDevHubProvider(client, _ => Task.FromResult(Credential())).GetAuthenticatedUserAsync(cancellation.Token)
                    : new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { })
                        .GetUserForHostAsync("dev.azure.com", "https://dev.azure.com/org/_apis/connectionData", cancellation.Token));
            await HttpResponseReaderTests.MustCompleteAsync(stream.ReadStarted.Task);

            cancellation.Cancel();

            await HttpResponseReaderTests.MustCompleteAsync(assertion);
            await assertion;
            Assert.IsTrue(stream.IsDisposed);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task SummaryIdentityAndInventoryBodyCancellationNeverReturnsSuccessfulTotalsAsync(bool identityBody)
        {
            using var stream = new HttpResponseReaderTests.BlockingStream();
            using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/user") && !identityBody
                ? Json("{\"login\":\"me\"}")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            using var client = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var assertion = Assert.ThrowsAsync<OperationCanceledException>(() => new GitHubDevHubProvider()
                .GetRepoSummaryAsync(client, Credential(), new RemoteRepoIdentifier("github.com", "owner", "repo"), cancellation.Token));
            await HttpResponseReaderTests.MustCompleteAsync(stream.ReadStarted.Task);

            cancellation.Cancel();

            await HttpResponseReaderTests.MustCompleteAsync(assertion);
            await assertion;
            if (!identityBody)
            {
                Assert.IsFalse(stream.IsDisposed, "Canceling a waiter must not dispose shared inventory work.");
                stream.PendingRead.TrySetResult(0);
                await HttpResponseReaderTests.MustCompleteAsync(stream.Disposed.Task);
            }
            Assert.IsTrue(stream.IsDisposed);
        }

        [TestMethod]
        public async Task CiInventoryBodyCancellationDisposesStalledTransportAsync()
        {
            using var stream = new HttpResponseReaderTests.BlockingStream();
            using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/user/repos"
                ? Json("[{\"full_name\":\"owner/repo\",\"name\":\"repo\",\"owner\":{\"login\":\"owner\"}}]")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            using var client = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var assertion = Assert.ThrowsAsync<OperationCanceledException>(() => GitHubDevHubProvider
                .GetUserCiRunsAsync(client, Credential(), "me", new DevHubActivityOptions(), cancellation.Token));
            await HttpResponseReaderTests.MustCompleteAsync(stream.ReadStarted.Task);

            cancellation.Cancel();

            await HttpResponseReaderTests.MustCompleteAsync(assertion);
            await assertion;
            Assert.IsTrue(stream.IsDisposed);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task IncompleteGitHubSearchPreservesItemsWithExplicitPartialWarningAsync(bool pullRequests)
        {
            var item = "{\"number\":1,\"title\":\"Mine\",\"state\":\"open\",\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"html_url\":\"https://github.com/owner/repo/issues/1\",\"created_at\":\"2026-11-04T20:00:00Z\",\"updated_at\":\"2026-11-04T20:00:00Z\",\"user\":{\"login\":\"me\"}" + (pullRequests ? ",\"pull_request\":{}" : "") + "}";
            using var handler = new Handler(request => Json(request.Method == HttpMethod.Post ? "{\"data\":{}}"
                : "{\"incomplete_results\":true,\"items\":[" + item + "]}"));
            using var client = new HttpClient(handler);
            var result = await DevHubActivityRequest.RunAsync(async () => pullRequests
                ? (await GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None)).Count
                : (await GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me", new DevHubActivityOptions(), CancellationToken.None)).Count,
                count => count > 0, CancellationToken.None);

            Assert.AreEqual(1, result.Value);
            Assert.AreEqual(DevHubActivityStatus.Partial, result.Status);
            StringAssert.Contains(result.Message, "incomplete search results");
        }

        [TestMethod]
        public async Task EmptyIncompleteSearchIsNotSuccessfulEmptyInventoryAsync()
        {
            using var handler = new Handler(_ => Json("{\"incomplete_results\":true,\"items\":[]}"));
            using var client = new HttpClient(handler);
            var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me",
                new DevHubActivityOptions(), CancellationToken.None), items => items.Count > 0, CancellationToken.None);

            Assert.IsEmpty(result.Value);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, result.Status);
            StringAssert.Contains(result.Message, "incomplete search results");
        }

        [TestMethod]
        [DataRow("uuid", "{my-uuid}")]
        [DataRow("account_id", "my-account")]
        [DataRow("username", "legacy-user")]
        public async Task BitbucketStableIdentityWorksWithoutUsernameAndIsReusedForRepoAuthorshipAsync(string identityProperty, string identity)
        {
            var user = JsonSerializer.Serialize(new Dictionary<string, string> { [identityProperty] = identity, ["nickname"] = "shared-name" });
            var pr = "{\"id\":1,\"title\":\"Mine\",\"state\":\"OPEN\",\"updated_on\":\"2026-11-04T20:00:00Z\",\"created_on\":\"2026-11-04T20:00:00Z\",\"author\":" + user + "}";
            var identityRequests = 0;
            var personalPaths = new List<string>();
            using var handler = new Handler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/user"))
                {
                    identityRequests++;
                    return Json(user);
                }
                if (path.StartsWith("/2.0/pullrequests/", StringComparison.Ordinal))
                    personalPaths.Add(request.RequestUri.OriginalString);
                return Json(path.Contains("pullrequests") ? "{\"values\":[" + pr + "]}" : "{\"values\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = new BitbucketDevHubProvider(client, _ => Task.FromResult(Credential()));

            var authenticated = await provider.GetAuthenticatedUserAsync(CancellationToken.None);
            var personal = await provider.GetUserPullRequestsAsync(CancellationToken.None);
            var detail = await provider.GetRepoDetailAsync(new RemoteRepoIdentifier("bitbucket.org", "owner", "repo"), 10, CancellationToken.None);

            Assert.IsNotNull(authenticated);
            Assert.AreEqual(identity, authenticated.IdentityId);
            Assert.HasCount(1, personal);
            Assert.IsTrue(personal[0].IsAuthoredByCurrentUser);
            Assert.IsTrue(detail.PullRequests.Single().IsAuthoredByCurrentUser);
            Assert.AreEqual(1, identityRequests);
            StringAssert.Contains(personalPaths.Single(), Uri.EscapeDataString(identity));
        }

        [TestMethod]
        public void BitbucketNicknameAndMissingIdentityNeverImplyAuthorship()
        {
            const string item = "{\"id\":1,\"title\":\"Other\",\"state\":\"OPEN\",\"updated_on\":\"2026-11-04T20:00:00Z\",\"created_on\":\"2026-11-04T20:00:00Z\",\"author\":{\"nickname\":\"same-name\"}}";

            Assert.IsFalse(BitbucketDevHubProvider.ParsePullRequests("{\"values\":[" + item + "]}", "same-name").Single().IsAuthoredByCurrentUser);
            Assert.IsFalse(BitbucketDevHubProvider.ParsePullRequests("{\"values\":[" + item + "]}", null!).Single().IsAuthoredByCurrentUser);
        }

        [TestMethod]
        public async Task OnPremConfiguredPortAndPathUseSameCredentialKeyForProfileAndRepositoryAsync()
        {
            const string configured = "tfs.contoso.com:8443/tfs/Collection";
            var credentialKeys = new List<string>();
            var urls = new List<Uri>();
            using var handler = new Handler(request =>
            {
                urls.Add(request.RequestUri!);
                return Json(request.RequestUri!.AbsolutePath.Contains("connectionData")
                    ? "{\"authenticatedUser\":{\"id\":\"me\"}}" : "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = new AzureDevOpsDevHubProvider(client, (host, _, _) =>
            {
                credentialKeys.Add(host);
                return Task.FromResult(Credential());
            }, (_, _) => { }, _ => throw new AssertFailedException("A supplied refresh context must prevent another remote scan."), () => [configured]);
            var context = new DevHubRefreshContext(["https://tfs.contoso.com:8443/tfs/Collection/Project/_git/Repo"]);

            var users = await provider.GetAuthenticatedUsersAsync(context, CancellationToken.None);
            var repo = RemoteRepoIdentifier.TryParse(context.RemoteUrls.Single())!;
            var summary = await provider.GetRepoSummaryAsync(repo, CancellationToken.None);

            Assert.AreEqual(DevHubRepoSummaryStatus.Ready, summary.Status);
            Assert.AreEqual("tfs.contoso.com", users.Single().Host, "Repository authentication matches a bare host, not a persisted endpoint path.");
            Assert.IsGreaterThanOrEqualTo(2, credentialKeys.Count);
            Assert.IsTrue(credentialKeys.All(host => host == configured));
            Assert.IsTrue(urls.All(url => url.Port == 8443 && url.AbsolutePath.StartsWith("/tfs/Collection/", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void OnPremCredentialSelectionChecksAuthorityAndPathBoundaries()
        {
            using var client = new HttpClient();
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential()), (_, _) => { },
                getCredentialHosts: () => ["tfs.contoso.com:8443/tfs/Collection", "tfs.contoso.com:9443/tfs/Other", "tfs.contoso.com.evil:8443/tfs"]);

            Assert.AreEqual("tfs.contoso.com:8443/tfs/Collection", provider.GetCredentialHost(RemoteRepoIdentifier.TryParse("https://tfs.contoso.com:8443/tfs/Collection/Project/_git/Repo")!));
            Assert.AreEqual("tfs.contoso.com:8443", provider.GetCredentialHost(RemoteRepoIdentifier.TryParse("https://tfs.contoso.com:8443/tfs/CollectionOther/Project/_git/Repo")!));
            Assert.AreEqual("tfs.contoso.com:9443", provider.GetCredentialHost(RemoteRepoIdentifier.TryParse("https://tfs.contoso.com:9443/tfs/Collection/Project/_git/Repo")!));
        }

        private static DevHubCredential Credential() => new("me", "test-token");
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
        }
    }
}