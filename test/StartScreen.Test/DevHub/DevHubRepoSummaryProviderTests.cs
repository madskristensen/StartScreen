using System.Net;
using System.Net.Http;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubRepoSummaryProviderTests
    {
        [TestMethod]
        public async Task VerifiedCountsAreSharedAcrossIdentityCacheInstancesButNotCredentialsAsync()
        {
            DevHubRepoSummaryFetcher.InvalidateSummaries();
            var requests = 0;
            Task<HttpResponseMessage> Send(DevHubCredential credential, string url, CancellationToken token)
            {
                requests++;
                return Task.FromResult(Json(url.EndsWith("/user") ? Identity(true) : Page(true, 0, 0)));
            }
            var credential = new DevHubCredential("account", Guid.NewGuid().ToString());
            var scope = Guid.NewGuid().ToString();
            Task<DevHubRepoSummary> Fetch(DevHubCredential account) => DevHubRepoSummaryFetcher.FetchAsync(Repo(true),
                _ => Task.FromResult(account), Send, true, default, identityCache: new DevHubIdentityCache(), cacheScope: scope);
            var first = await Fetch(credential);
            var second = await Fetch(credential);
            Assert.AreSame(first, second);
            Assert.AreEqual(3, requests, "Each instance verifies identity; complete totals are reused.");
            var changed = await Fetch(new DevHubCredential("other", Guid.NewGuid().ToString()));
            Assert.AreNotSame(first, changed);
            Assert.AreEqual(5, requests);
            DevHubRepoSummaryFetcher.InvalidateSummaries();
            Assert.AreNotSame(first, await Fetch(credential));
            Assert.AreEqual(7, requests);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_CountsAllPagesAndDirectPendingReviewsAsync(bool gitHub)
        {
            var urls = new List<string>();
            var handler = new TestHttpMessageHandler((request, count) =>
            {
                urls.Add(request.RequestUri!.AbsoluteUri);
                return Json(count == 1 ? Identity(gitHub) : count == 2 ? Page(gitHub, 1, 100)
                    : count == 3 ? Page(gitHub, 101, 7) : Page(gitHub, 0, 0));
            });
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(DevHubRepoSummaryStatus.Ready, result.Status);
            Assert.AreEqual(107, result.OpenPullRequests);
            Assert.AreEqual(54, result.RequestedReviews);
            Assert.AreEqual(4, handler.RequestCount);
            StringAssert.Contains(urls[2], gitHub ? "page=2" : "$skip=100");
            StringAssert.Contains(urls[3], gitHub ? "page=3" : "$skip=107");
            Assert.AreEqual(Repo(gitHub), result.RepoIdentifier);
            Assert.AreEqual(DateTimeKind.Utc, result.FetchedAt.Kind);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_ContinuesAfterShortPageAsync(bool gitHub)
        {
            var handler = new TestHttpMessageHandler((_, count) => Json(count == 1 ? Identity(gitHub)
                : count == 2 ? Page(gitHub, 1, 2) : count == 3 ? Page(gitHub, 3, 1) : Page(gitHub, 0, 0)));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(3, result.OpenPullRequests);
            Assert.AreEqual(2, result.RequestedReviews);
            Assert.AreEqual(4, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(true, 401, DevHubRepoSummaryStatus.Unauthorized)]
        [DataRow(false, 401, DevHubRepoSummaryStatus.Unauthorized)]
        [DataRow(true, 403, DevHubRepoSummaryStatus.Unavailable)]
        [DataRow(false, 404, DevHubRepoSummaryStatus.Unavailable)]
        [DataRow(true, 429, DevHubRepoSummaryStatus.Unavailable)]
        [DataRow(false, 503, DevHubRepoSummaryStatus.Unavailable)]
        public async Task Summary_HttpFailureNeverBecomesZeroAsync(bool gitHub, int status, DevHubRepoSummaryStatus expected)
        {
            var handler = new TestHttpMessageHandler((_, count) => count == 1 ? Json(Identity(gitHub))
                : new HttpResponseMessage((HttpStatusCode)status));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(expected, result.Status);
            Assert.IsNull(result.OpenPullRequests);
            Assert.IsNull(result.RequestedReviews);
            Assert.IsFalse(string.IsNullOrEmpty(result.StatusMessage));
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_IdentityFailureKeepsTotalButReviewsUnknownAsync(bool gitHub)
        {
            var handler = new TestHttpMessageHandler((_, count) => count == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Json(count == 2 ? Page(gitHub, 1, 2) : Page(gitHub, 0, 0)));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(DevHubRepoSummaryStatus.Ready, result.Status);
            Assert.AreEqual(2, result.OpenPullRequests);
            Assert.IsNull(result.RequestedReviews);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_MissingReviewerPayloadKeepsReviewsUnknownAsync(bool gitHub)
        {
            var item = gitHub ? "[{\"number\":1}]" : "{\"value\":[{\"pullRequestId\":1}]}";
            var handler = new TestHttpMessageHandler((_, count) => Json(count == 1 ? Identity(gitHub)
                : count == 2 ? item : Page(gitHub, 0, 0)));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(1, result.OpenPullRequests);
            Assert.IsNull(result.RequestedReviews);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_RepeatedPageIsNotReportedAsCompleteAsync(bool gitHub)
        {
            var handler = new TestHttpMessageHandler((_, count) => Json(count == 1 ? Identity(gitHub) : Page(gitHub, 1, 1)));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(DevHubRepoSummaryStatus.Unavailable, result.Status);
            Assert.IsNull(result.OpenPullRequests);
            Assert.AreEqual(3, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_FailureOnLaterPageDoesNotExposePartialTotalsAsync(bool gitHub)
        {
            var handler = new TestHttpMessageHandler((_, count) => count == 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(count == 1 ? Identity(gitHub) : Page(gitHub, 1, 100)));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.IsNull(result.OpenPullRequests);
            Assert.IsNull(result.RequestedReviews);
            Assert.AreEqual(DevHubRepoSummaryStatus.Unavailable, result.Status);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_EmptyRepositoryHasKnownZeroAsync(bool gitHub)
        {
            var handler = new TestHttpMessageHandler((_, count) => Json(count == 1 ? Identity(gitHub) : Page(gitHub, 0, 0)));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(DevHubRepoSummaryStatus.Ready, result.Status);
            Assert.AreEqual(0, result.OpenPullRequests);
            Assert.AreEqual(0, result.RequestedReviews);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_CancellationPropagatesAsync(bool gitHub)
        {
            using var cancellation = new CancellationTokenSource();
            var handler = new TestHttpMessageHandler((_, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return Json("{}");
            });
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<OperationCanceledException>(() => FetchAsync(gitHub, client, cancellation.Token));
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task Bitbucket_ExplicitlyUnsupportedWithoutChangingActivityAsync()
        {
            var provider = new BitbucketDevHubProvider();
            var repo = new RemoteRepoIdentifier("bitbucket.org", "owner", "repo");
            var result = await provider.GetRepoSummaryAsync(repo, CancellationToken.None);

            Assert.AreEqual(DevHubRepoSummaryStatus.Unsupported, result.Status);
            Assert.IsNull(result.OpenPullRequests);
            Assert.IsNull(result.RequestedReviews);
            Assert.AreEqual("https://bitbucket.org/owner/repo/pull-requests", provider.GetPullRequestsWebUrl(repo));
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_MalformedPayloadNeverBecomesZeroAsync(bool gitHub)
        {
            var handler = new TestHttpMessageHandler((_, count) => Json(count == 1 ? Identity(gitHub) : "not-json"));
            using var client = new HttpClient(handler);

            var result = await FetchAsync(gitHub, client);

            Assert.AreEqual(DevHubRepoSummaryStatus.Unavailable, result.Status);
            Assert.IsNull(result.OpenPullRequests);
            Assert.IsNull(result.RequestedReviews);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_MissingCredentialsNeverSendsRequestAsync(bool gitHub)
        {
            var handler = new TestHttpMessageHandler((_, _) => Json("{}"));
            using var client = new HttpClient(handler);

            var result = gitHub
                ? await new GitHubDevHubProvider().GetRepoSummaryAsync(client, null!, Repo(true), CancellationToken.None)
                : await new AzureDevOpsDevHubProvider().GetRepoSummaryAsync(client, null!, Repo(false), CancellationToken.None);

            Assert.AreEqual(DevHubRepoSummaryStatus.Unauthorized, result.Status);
            Assert.IsNull(result.OpenPullRequests);
            Assert.IsNull(result.RequestedReviews);
            Assert.AreEqual(0, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void GitHubSearch_OnlyDedicatedReviewQueryMarksRequested(bool reviewQuery)
        {
            const string item = "{\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"number\":1,"
                + "\"title\":\"PR\",\"user\":{\"login\":\"other\"},\"updated_at\":\"2026-09-12T10:00:00Z\","
                + "\"created_at\":\"2026-09-12T10:00:00Z\",\"html_url\":\"https://github.com/owner/repo/pull/1\"}";

            var result = GitHubDevHubProvider.ParsePullRequests("{\"items\":[" + item + "]}", "me", reviewQuery);

            Assert.IsFalse(result[0].IsAuthoredByCurrentUser);
            Assert.AreEqual(reviewQuery, result[0].IsReviewRequestedForCurrentUser);
        }

        [TestMethod]
        [DataRow("me", true)]
        [DataRow("other", false)]
        [DataRow(null, false)]
        public void GitHubRepo_ExplicitReviewerRequired(string? identity, bool expected)
        {
            const string item = "{\"number\":1,\"title\":\"PR\",\"user\":{\"login\":\"author\"},"
                + "\"requested_reviewers\":[{\"login\":\"ME\"}],\"requested_teams\":[{\"slug\":\"my-team\"}],"
                + "\"base\":{\"ref\":\"main\"},\"head\":{\"ref\":\"feature\"},"
                + "\"updated_at\":\"2026-09-12T10:00:00Z\",\"created_at\":\"2026-09-12T10:00:00Z\","
                + "\"html_url\":\"https://github.com/owner/repo/pull/1\"}";

            var result = GitHubDevHubProvider.ParseRepoPullRequests("[" + item + "]", Repo(true), identity!);

            Assert.AreEqual(expected, result[0].IsReviewRequestedForCurrentUser);
        }

        [TestMethod]
        [DataRow(0, "active", "me", true)]
        [DataRow(10, "active", "me", false)]
        [DataRow(-5, "active", "me", false)]
        [DataRow(0, "completed", "me", false)]
        [DataRow(0, "active", "other", false)]
        [DataRow(0, "active", null, false)]
        public void AzureDevOps_RequestedReviewRequiresVerifiedIdentityAndPendingVote(int vote, string status, string? identity, bool expected)
        {
            var item = "{\"pullRequestId\":1,\"title\":\"PR\",\"createdBy\":{\"displayName\":\"Author\",\"id\":\"author\"},"
                + "\"reviewers\":[{\"id\":\"me\",\"vote\":" + vote + "}],\"status\":\"" + status + "\","
                + "\"targetRefName\":\"refs/heads/main\",\"sourceRefName\":\"refs/heads/feature\","
                + "\"creationDate\":\"2026-09-12T10:00:00Z\"}";

            var result = AzureDevOpsDevHubProvider.ParseAdoPullRequests("{\"value\":[" + item + "]}", Repo(false), identity!);

            Assert.AreEqual(expected, result[0].IsReviewRequestedForCurrentUser);
            Assert.IsFalse(result[0].IsAuthoredByCurrentUser);
        }

        private static RemoteRepoIdentifier Repo(bool gitHub) => gitHub
            ? new RemoteRepoIdentifier("github.com", "owner", "repo")
            : new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");

        private static Task<DevHubRepoSummary> FetchAsync(bool gitHub, HttpClient client, CancellationToken token = default)
        {
            var credential = new DevHubCredential("ignored-credential-username", "test-token");
            return gitHub ? new GitHubDevHubProvider().GetRepoSummaryAsync(client, credential, Repo(true), token)
                : new AzureDevOpsDevHubProvider().GetRepoSummaryAsync(client, credential, Repo(false), token);
        }

        private static string Identity(bool gitHub) => gitHub ? "{\"login\":\"me\"}" : "{\"authenticatedUser\":{\"id\":\"me\"}}";

        private static HttpResponseMessage Json(string content) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        };

        private static string Page(bool gitHub, int first, int count)
        {
            var items = Enumerable.Range(first, count).Select(id => gitHub
                ? $"{{\"number\":{id},\"requested_reviewers\":[{{\"login\":\"{(id % 2 == 1 ? "ME" : "other")}\"}}]}}"
                : $"{{\"pullRequestId\":{id},\"reviewers\":[{{\"id\":\"me\",\"vote\":{(id % 2 == 1 ? 0 : 10)}}},{{\"id\":\"team\",\"isContainer\":true,\"vote\":0}}]}}");
            var array = "[" + string.Join(",", items) + "]";
            return gitHub ? array : "{\"count\":1,\"value\":" + array + "}";
        }
    }
}
