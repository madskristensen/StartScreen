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
        private const string RepositoryId = "e2d789d1-67ab-405a-9704-c72759720867";

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
                var path = request.RequestUri!.AbsolutePath;
                return Json(path.EndsWith("/repositories/repo") ? $"{{\"id\":\"{RepositoryId}\"}}"
                    : path.Contains("connectionData") ? Identity : "{\"value\":[]}");
            });
            using var client = new HttpClient(handler);
            var provider = Provider(client, Credential(entra));

            var result = await provider.GetRepoDetailAsync(Repo(), 10, CancellationToken.None);

            Assert.IsNotNull(result);
            Assert.AreEqual(4, handler.RequestCount);
        }

        [TestMethod]
        [DataRow(true, false)]
        [DataRow(false, false)]
        [DataRow(false, true)]
        public async Task RepoDetail_ResolvesRepositoryGuidAndIncludesFailedPrBuildsAsync(bool onPremises, bool optimized)
        {
            var repo = onPremises
                ? RemoteRepoIdentifier.TryParse("https://tfs.contoso.com/tfs/Collection/Project/_git/Repo")!
                : optimized ? RemoteRepoIdentifier.TryParse("https://devdiv.visualstudio.com/DevDiv/_git/_optimized/VSEditor")! : Repo();
            var buildRequested = false;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                Assert.DoesNotContain("_optimized", uri.AbsoluteUri);
                if (uri.AbsolutePath.Contains("/build/builds"))
                {
                    buildRequested = true;
                    StringAssert.Contains(uri.Query, $"repositoryId={RepositoryId}&repositoryType=TfsGit");
                    StringAssert.Contains(uri.Query, onPremises ? "api-version=5.0" : "api-version=7.1");
                    Assert.DoesNotContain("branchName=", uri.Query);
                    Assert.DoesNotContain("resultFilter=", uri.Query);
                    return Json("{\"value\":[{\"id\":71,\"buildNumber\":\"PR validation\",\"status\":\"completed\",\"result\":\"failed\",\"sourceBranch\":\"refs/pull/71/merge\",\"finishTime\":\"2026-11-04T20:00:00Z\",\"_links\":{\"web\":{\"href\":\"https://dev.azure.com/org/project/_build/results?buildId=71\"}}}]}");
                }
                return Json(uri.AbsolutePath.Contains("connectionData") ? Identity
                    : uri.AbsolutePath.EndsWith("/pullrequests") ? "{\"value\":[]}"
                    : $"{{\"id\":\"{RepositoryId}\"}}");
            }));
            var provider = Provider(client, Credential(!onPremises));

            var result = await provider.GetRepoDetailAsync(repo, 10, CancellationToken.None);

            Assert.IsTrue(buildRequested);
            Assert.IsNotNull(result);
            Assert.HasCount(1, result.CiRuns);
            Assert.AreEqual("failure", result.CiRuns[0].Status);
            Assert.AreEqual("refs/pull/71/merge", result.CiRuns[0].Branch);
            Assert.AreEqual(repo, result.CiRuns[0].RepoIdentifier);
        }

        [TestMethod]
        [DataRow("{}")]
        [DataRow("{\"id\":\"repo\"}")]
        [DataRow("{\"id\":\"00000000-0000-0000-0000-000000000000\"}")]
        public async Task RepoDetail_InvalidRepositoryIdDoesNotQueryBuildsOrReportEmptySuccessAsync(string repositoryJson)
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                Assert.DoesNotContain("/build/builds", path);
                return Json(path.EndsWith("/pullrequests") ? "{\"value\":[]}"
                    : path.Contains("connectionData") ? Identity : repositoryJson);
            }));

            var result = await Provider(client, Credential(true)).GetRepoDetailAsync(Repo(), 10, CancellationToken.None);

            Assert.IsNull(result);
        }

        [TestMethod]
        public async Task RepoDetail_FailedBuildRequestDoesNotReportEmptySuccessAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                return path.Contains("/build/builds") ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                    : Json(path.EndsWith("/pullrequests") ? "{\"value\":[]}"
                    : path.Contains("connectionData") ? Identity : $"{{\"id\":\"{RepositoryId}\"}}");
            }));

            var result = await Provider(client, Credential(true)).GetRepoDetailAsync(Repo(), 10, CancellationToken.None);

            Assert.IsNull(result);
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