using System.Net;
using System.Net.Http;
using System.Text;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class AzureDevOpsAuthenticationTests
    {
        private const string Identity = "{\"authenticatedUser\":{\"id\":\"user-id\",\"customDisplayName\":\"User\",\"properties\":{\"Account\":{\"$value\":\"user@example.com\"}}}}";

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Summary_UsesBearerForEntraAndBasicForPatOnEveryPageAsync(bool entra)
        {
            var credential = Credential(entra);
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual(entra ? "Bearer" : "Basic", request.Headers.Authorization!.Scheme);
                Assert.AreEqual(entra ? "token" : Convert.ToBase64String(Encoding.ASCII.GetBytes(":token")), request.Headers.Authorization.Parameter);
                return Json(request.RequestUri!.AbsolutePath.Contains("connectionData") ? Identity : "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = Provider(client, credential);

            var result = await provider.GetRepoSummaryAsync(Repo(), CancellationToken.None);

            Assert.AreEqual(DevHubRepoSummaryStatus.Ready, result.Status);
            Assert.AreEqual(0, result.OpenPullRequests);
            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Profile_SendsExpectedAuthenticationAndParsesConnectionIdentityAsync(bool entra)
        {
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual(entra ? "Bearer" : "Basic", request.Headers.Authorization!.Scheme);
                return Json(Identity);
            });
            using var client = new HttpClient(handler);
            var provider = Provider(client, Credential(entra));

            var user = await provider.GetUserForHostAsync("dev.azure.com", "https://devdiv.visualstudio.com/_apis/connectionData", CancellationToken.None);

            Assert.IsNotNull(user);
            Assert.AreEqual("user@example.com", user.Username);
            Assert.AreEqual("User", user.DisplayName);
            Assert.AreEqual("dev.azure.com", user.Host);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task RepoDetail_UsesSelectedCredentialForPrIdentityAndBuildRequestsAsync(bool entra)
        {
            var handler = new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual(entra ? "Bearer" : "Basic", request.Headers.Authorization!.Scheme);
                return Json(request.RequestUri!.AbsolutePath.Contains("connectionData") ? Identity : "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = Provider(client, Credential(entra));

            var result = await provider.GetRepoDetailAsync(Repo(), 10, CancellationToken.None);

            Assert.IsNotNull(result);
            Assert.AreEqual(3, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Profile_UnauthorizedInvalidatesAndRetriesOnceWithFreshCredentialAsync(bool entra)
        {
            var invalidations = 0;
            var handler = new TestHttpMessageHandler((request, count) =>
            {
                Assert.AreEqual(count == 1 ? "old-token" : "fresh-token", entra
                    ? request.Headers.Authorization!.Parameter
                    : Encoding.ASCII.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)).TrimStart(':'));
                return count == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(Identity);
            });
            using var client = new HttpClient(handler);
            var provider = new AzureDevOpsDevHubProvider(client,
                (_, _, _) => Task.FromResult(entra ? new DevHubCredential(invalidations == 0 ? "old-token" : "fresh-token", DateTimeOffset.UtcNow.AddHours(1))
                    : new DevHubCredential("user", invalidations == 0 ? "old-token" : "fresh-token")),
                (_, _) => invalidations++);

            var user = await provider.GetUserForHostAsync("dev.azure.com", "https://dev.azure.com/org/_apis/connectionData", CancellationToken.None);

            Assert.IsNotNull(user);
            Assert.AreEqual(1, invalidations);
            Assert.AreEqual(2, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(401, 2)]
        [DataRow(403, 1)]
        [DataRow(429, 1)]
        public async Task Profile_OnlyUnauthorizedIsRetriedAndFailuresNeverBecomeConnectedAsync(int status, int expectedRequests)
        {
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage((HttpStatusCode)status));
            using var client = new HttpClient(handler);
            var invalidations = 0;
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(Credential(true)), (_, _) => invalidations++);

            var user = await provider.GetUserForHostAsync("dev.azure.com", "https://dev.azure.com/org/_apis/connectionData", CancellationToken.None);

            Assert.IsNull(user);
            Assert.AreEqual(expectedRequests, handler.RequestCount);
            Assert.AreEqual(status == 401 ? 2 : 0, invalidations);
        }

        [TestMethod]
        public async Task Summary_NoVsCredentialShowsActionableSignInWithoutRequestsAsync()
        {
            var handler = new TestHttpMessageHandler((_, _) => Json("{}"));
            using var client = new HttpClient(handler);
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult<DevHubCredential>(null!), (_, _) => { });

            var summary = await provider.GetRepoSummaryAsync(Repo(), CancellationToken.None);

            Assert.AreEqual(DevHubRepoSummaryStatus.Unauthorized, summary.Status);
            Assert.IsNull(summary.OpenPullRequests);
            StringAssert.Contains(summary.StatusMessage, "Visual Studio account");
            Assert.AreEqual(0, handler.RequestCount);
        }

        [TestMethod]
        public async Task OnPremises_PatDoesNotUseCloudCredentialHostAsync()
        {
            var repo = RemoteRepoIdentifier.TryParse("https://tfs.contoso.com/tfs/Collection/Project/_git/Repo")!;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                Assert.AreEqual("Basic", request.Headers.Authorization!.Scheme);
                return Json(request.RequestUri!.AbsolutePath.Contains("connectionData") ? Identity : "{\"value\":[]}");
            }));
            var provider = new AzureDevOpsDevHubProvider(client, (host, _, _) =>
            {
                Assert.AreEqual("tfs.contoso.com", host);
                return Task.FromResult(Credential(false));
            }, (_, _) => { });

            Assert.AreEqual(DevHubRepoSummaryStatus.Ready, (await provider.GetRepoSummaryAsync(repo, CancellationToken.None)).Status);
        }

        [TestMethod]
        public async Task OnPremises_EntraCredentialCannotBeSentToServerAsync()
        {
            var handler = new TestHttpMessageHandler((_, _) => Json(Identity));
            using var client = new HttpClient(handler);
            var provider = Provider(client, Credential(true));

            var user = await provider.GetUserForHostAsync("tfs.contoso.com", "https://tfs.contoso.com/_apis/connectionData", CancellationToken.None);

            Assert.IsNull(user);
            Assert.AreEqual(0, handler.RequestCount);
        }

        [TestMethod]
        public async Task Profile_CancellationDuringReacquisitionStopsRetryAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            using var client = new HttpClient(handler);
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Credential(true));
            }, (_, _) => cancellation.Cancel());

            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetUserForHostAsync(
                "dev.azure.com", "https://dev.azure.com/org/_apis/connectionData", cancellation.Token));
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        [DataRow("{}")]
        [DataRow("{\"authenticatedUser\":{}}")]
        [DataRow("{\"authenticatedUser\":{\"id\":\"00000000-0000-0000-0000-000000000000\"}}")]
        public async Task Profile_MissingOrAnonymousIdentityDoesNotBecomeConnectedAsync(string payload)
        {
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => Json(payload)));
            var provider = Provider(client, Credential(true));

            Assert.IsNull(await provider.GetUserForHostAsync("dev.azure.com", "https://dev.azure.com/org/_apis/connectionData", CancellationToken.None));
        }

        [TestMethod]
        [DataRow("{}", false)]
        [DataRow("{\"id\":\"profile-id\",\"emailAddress\":\"user@example.com\",\"displayName\":\"User\"}", true)]
        public async Task Profile_SpsRequiresAuthenticatedProfileIdentityAsync(string payload, bool authenticated)
        {
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => Json(payload)));
            var provider = Provider(client, Credential(true));

            var user = await provider.GetUserForHostAsync("dev.azure.com", "https://app.vssps.visualstudio.com/_apis/profile/profiles/me", CancellationToken.None);

            if (authenticated)
            {
                Assert.IsNotNull(user);
                Assert.AreEqual("user@example.com", user.Username);
                Assert.AreEqual("User", user.DisplayName);
            }
            else
            {
                Assert.IsNull(user);
            }
        }

        private static DevHubCredential Credential(bool entra) => entra
            ? new DevHubCredential("token", DateTimeOffset.UtcNow.AddHours(1)) : new DevHubCredential("user", "token");

        private static AzureDevOpsDevHubProvider Provider(HttpClient client, DevHubCredential credential) =>
            new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(credential), (_, _) => { });

        private static RemoteRepoIdentifier Repo() => new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");

        private static HttpResponseMessage Json(string content) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}