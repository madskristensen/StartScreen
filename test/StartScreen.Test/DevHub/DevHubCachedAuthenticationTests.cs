using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    [DoNotParallelize]
    public class DevHubCachedAuthenticationTests
    {
        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Azure DevOps")]
        [DataRow("Bitbucket")]
        public async Task ReopenedProvider_RestoresBoundIdentityWithoutHttpOrExtendingVerificationAsync(string name)
        {
            var requests = 0;
            using var client = new HttpClient(new Handler((_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(Profile());
            }));
            var key = Key();
            var binding = new DevHubAccountBinding(() => key, value => key = value);
            var credential = new DevHubCredential("me", "test-token");
            IDevHubProvider Provider() => Create(name, client, () => credential, binding);
            var users = await AuthenticateAsync(Provider());
            Assert.HasCount(1, users);
            var saved = JsonSerializer.Deserialize<List<DevHubUser>>(JsonSerializer.Serialize(users))!;
            var reopened = Provider();
            requests = 0;

            var restored = await ((IDevHubCachedAuthenticationProvider)reopened).GetCachedAuthenticatedUsersAsync(
                saved, new DevHubRefreshContext([]), CancellationToken.None);

            Assert.IsNotNull(restored);
            Assert.AreEqual(saved.Single().IdentityId, restored.Single().IdentityId);
            Assert.IsTrue(restored.Single().RestoredFromAccountBinding);
            Assert.AreEqual(0, requests, "Restoration must not wait for an identity endpoint.");
            await AuthenticateAsync(reopened);
            Assert.AreEqual(1, requests, "A local binding must not populate the five-minute online verification cache.");
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Azure DevOps")]
        [DataRow("Bitbucket")]
        public async Task ReopenedProvider_RejectsChangedOrInvalidatedCredentialsAsync(string name)
        {
            var requests = 0;
            using var client = new HttpClient(new Handler((_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(Profile());
            }));
            var key = Key();
            var binding = new DevHubAccountBinding(() => key, value => key = value);
            var credential = new DevHubCredential("me", "test-token");
            IDevHubProvider Provider() => Create(name, client, () => credential, binding);
            var users = await AuthenticateAsync(Provider());
            var reopened = (IDevHubCachedAuthenticationProvider)Provider();
            requests = 0;
            credential = new DevHubCredential("other", "other-token");
            Assert.IsNull(await reopened.GetCachedAuthenticatedUsersAsync(users, new DevHubRefreshContext([]), CancellationToken.None));
            credential = new DevHubCredential("me", "test-token");
            binding.Invalidate();
            Assert.IsNull(await reopened.GetCachedAuthenticatedUsersAsync(users, new DevHubRefreshContext([]), CancellationToken.None));
            Assert.AreEqual(0, requests);
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Azure DevOps")]
        [DataRow("Bitbucket")]
        public async Task ActivityAuthenticationRejection_InvalidatesRecentOnlineVerificationAsync(string name)
        {
            var requests = 0;
            using var client = new HttpClient(new Handler((_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(Profile());
            }));
            var key = Key();
            var provider = Create(name, client, () => new DevHubCredential("me", "test-token"),
                new DevHubAccountBinding(() => key, value => key = value));
            await AuthenticateAsync(provider);
            await AuthenticateAsync(provider);
            Assert.AreEqual(1, requests);

            using var rejected = DevHubActivityRequest.Observe(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            await AuthenticateAsync(provider);

            Assert.AreEqual(2, requests, "A confirmed authentication rejection must force verification before trusting the in-memory identity again.");
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Azure DevOps")]
        [DataRow("Bitbucket")]
        public async Task LegacyProfilesAndCanceledLoads_DoNotAcquireCredentialsBeforeVerificationIsNeededAsync(string name)
        {
            var acquisitions = 0;
            using var client = new HttpClient(new Handler((_, _) => throw new AssertFailedException("Local cache lookup must not send HTTP.")));
            var key = Key();
            var provider = (IDevHubCachedAuthenticationProvider)Create(name, client, () =>
            {
                acquisitions++;
                return new DevHubCredential("me", "test-token");
            }, new DevHubAccountBinding(() => key, value => key = value));
            var legacy = new DevHubUser { ProviderName = name, Username = "me", IdentityId = "1" };
            Assert.IsNull(await provider.GetCachedAuthenticatedUsersAsync([legacy], new DevHubRefreshContext([]), CancellationToken.None));
            Assert.AreEqual(0, acquisitions);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetCachedAuthenticatedUsersAsync(
                [legacy], new DevHubRefreshContext([]), cancellation.Token));
            Assert.AreEqual(0, acquisitions);
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Azure DevOps")]
        [DataRow("Bitbucket")]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The waits observe test-owned HTTP requests without a UI synchronization context.")]
        public async Task ConcurrentVerification_SharesOneIdentityRequestAsync(string name)
        {
            var started = Signal();
            var release = Signal();
            var requests = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (_, token) =>
            {
                Interlocked.Increment(ref requests);
                started.TrySetResult(true);
                await release.Task.WithCancellation(token);
                return Profile();
            }));
            var key = Key();
            var provider = Create(name, client, () => new DevHubCredential("me", "test-token"),
                new DevHubAccountBinding(() => key, value => key = value));
            var first = AuthenticateAsync(provider, timeout.Token);
            var second = AuthenticateAsync(provider, timeout.Token);
            try
            {
                await started.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(first.IsCompleted);
                Assert.IsFalse(second.IsCompleted);
            }
            finally
            {
                release.TrySetResult(true);
                await Task.WhenAll(first, second);
            }
            Assert.AreEqual(1, requests);
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The waits observe test-owned HTTP requests without a UI synchronization context.")]
        public async Task Startup_DisplaysStaleActivityBeforeDelayedOnlineVerificationAsync()
        {
            using var directory = new TemporaryDirectory();
            var key = Key();
            var binding = new DevHubAccountBinding(() => key, value => key = value);
            var block = false;
            var started = Signal();
            var release = Signal();
            var profileRequests = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref profileRequests);
                    if (block)
                    {
                        started.TrySetResult(true);
                        await release.Task.WithCancellation(token);
                    }
                    return Profile();
                }
                return Json("{\"values\":[]}");
            }));
            var cache = new DevHubCacheService(directory.Path);
            var seeded = await Service(cache, Create("Bitbucket", client, () => new DevHubCredential("me", "test-token"), binding))
                .RefreshAsync(timeout.Token);
            SeedActivity(seeded);
            seeded.FetchedAt = DateTime.UtcNow.AddHours(-12);
            await cache.WriteDashboardAsync(seeded, timeout.Token);
            var service = Service(cache, Create("Bitbucket", client, () => new DevHubCredential("me", "test-token"), binding));
            profileRequests = 0;
            block = true;

            var restored = await service.LoadFromCacheAsync(timeout.Token);
            Assert.IsNotNull(restored);
            Assert.AreEqual("Saved activity", restored.Issues.Single().Title);
            Assert.AreEqual(0, profileRequests);
            Assert.IsTrue(service.IsCacheStale(), "A local binding must schedule online validation even when the file is recently written.");
            var refresh = service.RefreshAsync(timeout.Token);
            try
            {
                await started.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.AreEqual("Saved activity", service.CurrentDashboard.Issues.Single().Title);
            }
            finally
            {
                release.TrySetResult(true);
                await refresh;
            }
            Assert.AreEqual(1, profileRequests);
            Assert.IsEmpty(service.CurrentDashboard.Issues);
        }

        [TestMethod]
        [DataRow(503, true)]
        [DataRow(401, false)]
        public async Task BackgroundVerification_RetainsActivityOnlyForTemporaryFailureAsync(int status, bool retain)
        {
            using var directory = new TemporaryDirectory();
            var key = Key();
            var binding = new DevHubAccountBinding(() => key, value => key = value);
            var credential = new DevHubCredential("me", "test-token");
            var fail = false;
            using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                    ? fail ? new HttpResponseMessage((HttpStatusCode)status) : Profile() : Json("{\"values\":[]}"))));
            var cache = new DevHubCacheService(directory.Path);
            var seeded = await Service(cache, Create("Bitbucket", client, () => credential, binding)).RefreshAsync(CancellationToken.None);
            SeedActivity(seeded);
            await cache.WriteDashboardAsync(seeded);
            var service = Service(cache, Create("Bitbucket", client, () => credential, binding));
            Assert.IsNotNull(await service.LoadFromCacheAsync());
            fail = true;

            var refreshed = await service.RefreshAsync(CancellationToken.None);

            Assert.AreEqual(retain, refreshed.HasData);
            Assert.AreEqual(retain, refreshed.HasAuthentication);
            Assert.AreNotEqual(DevHubActivityStatus.Success, refreshed.Status);
            Assert.IsTrue(refreshed.Sources.ContainsKey("Bitbucket/authentication"));
            if (retain)
            {
                Assert.AreEqual("Saved activity", refreshed.Issues.Single().Title);
                Assert.IsTrue(refreshed.Sources["Bitbucket/issues"].IsStale);
                Assert.IsNotNull(await service.LoadFromCacheAsync());
            }
        }

        private static DevHubService Service(DevHubCacheService cache, IDevHubProvider provider) => new(cache, [provider],
            _ => Task.FromResult<IReadOnlyList<string>>(["https://bitbucket.org/owner/repo"]),
            () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);

        private static void SeedActivity(DevHubDashboard dashboard)
        {
            var issue = new DevHubIssue { Title = "Saved activity", WebUrl = "https://bitbucket.org/owner/repo/issues/1" };
            dashboard.Sources["Bitbucket/issues"].Issues = [issue];
            dashboard.Issues = [issue];
        }

        private static IDevHubProvider Create(string name, HttpClient client, Func<DevHubCredential> credential, DevHubAccountBinding binding)
            => name switch
            {
                "GitHub" => new GitHubDevHubProvider(client, _ => Task.FromResult(credential()), binding),
                "Azure DevOps" => new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(credential()), (_, _) => binding.Invalidate(),
                    getCredentialHosts: () => ["dev.azure.com"], accountBinding: binding),
                "Bitbucket" => new BitbucketDevHubProvider(client, _ => Task.FromResult(credential()), hasStoredCredential: () => true, accountBinding: binding),
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };

        private static Task<IReadOnlyList<DevHubUser>> AuthenticateAsync(IDevHubProvider provider, CancellationToken token = default)
            => provider is IDevHubRefreshProvider refresh ? refresh.GetAuthenticatedUsersAsync(new DevHubRefreshContext([]), token)
                : provider.GetAuthenticatedUsersAsync(token);
        private static string Key() => Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static HttpResponseMessage Profile() => Json("{\"login\":\"me\",\"id\":\"1\",\"uuid\":\"{me}\",\"username\":\"me\",\"emailAddress\":\"me\"}");
        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        }
    }
}
