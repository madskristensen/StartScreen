using System.IO;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubRefreshTests
    {
        private string _cacheFolder = null!;

        [TestInitialize]
        public void Setup()
        {
            _cacheFolder = Path.Combine(Path.GetTempPath(), "StartScreenTest_" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_cacheFolder))
                Directory.Delete(_cacheFolder, recursive: true);
        }

        [TestMethod]
        public async Task ForcedRefresh_WaitsAndChecksAuthenticationAgainAsync()
        {
            var release = Signal();
            var calls = 0;
            var service = Service(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    await release.Task;
                return Array.Empty<DevHubUser>();
            });

            var first = service.RefreshAsync(CancellationToken.None);
            var forced = service.RefreshAsync(CancellationToken.None, forceRefresh: true);
            var waited = !forced.IsCompleted;
            var callsBeforeRelease = calls;
            release.SetResult(true);
            var results = await Task.WhenAll(first, forced);

            Assert.IsTrue(waited);
            Assert.AreEqual(1, callsBeforeRelease);
            Assert.AreEqual(2, calls);
            Assert.AreNotSame(results[0], results[1]);
            Assert.AreSame(results[1], service.CurrentDashboard);
            Assert.IsFalse(service.IsRefreshing);
        }

        [TestMethod]
        public async Task BackgroundRefresh_DoesNotQueueDuplicateWorkAsync()
        {
            var release = Signal();
            var calls = 0;
            var service = Service(async _ =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return Array.Empty<DevHubUser>();
            });

            var first = service.RefreshAsync(CancellationToken.None);
            var duplicate = await service.RefreshAsync(CancellationToken.None);
            var wasRefreshing = service.IsRefreshing;
            release.SetResult(true);
            await first;

            Assert.IsNull(duplicate);
            Assert.IsTrue(wasRefreshing);
            Assert.AreEqual(1, calls);
            Assert.IsFalse(service.IsRefreshing);
        }

        [TestMethod]
        public async Task ForcedRefresh_CancellationDoesNotInterruptActiveRefreshOrBlockRetryAsync()
        {
            var release = Signal();
            var calls = 0;
            var service = Service(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    await release.Task;
                return Array.Empty<DevHubUser>();
            });
            using var cancellation = new CancellationTokenSource();
            var first = service.RefreshAsync(CancellationToken.None);

            try
            {
                var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    var forced = service.RefreshAsync(cancellation.Token, forceRefresh: true);
                    cancellation.Cancel();
                    await forced;
                });
                Assert.AreEqual(cancellation.Token, exception.CancellationToken);
                Assert.IsTrue(service.IsRefreshing);
                Assert.AreEqual(1, calls);
            }
            finally
            {
                release.SetResult(true);
                await first;
            }

            await service.RefreshAsync(CancellationToken.None, forceRefresh: true);
            Assert.AreEqual(2, calls);
            Assert.IsFalse(service.IsRefreshing);
        }

        [TestMethod]
        public async Task Refresh_AuthenticationFailureReleasesGateForRetryAsync()
        {
            var calls = 0;
            var service = Service(_ => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<IReadOnlyList<DevHubUser>>(new InvalidOperationException("Authentication failed."))
                : Task.FromResult<IReadOnlyList<DevHubUser>>(Array.Empty<DevHubUser>()));

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync(CancellationToken.None));
            Assert.IsFalse(service.IsRefreshing);
            await service.RefreshAsync(CancellationToken.None, forceRefresh: true);

            Assert.AreEqual(2, calls);
            Assert.IsFalse(service.IsRefreshing);
        }

        private DevHubService Service(Func<CancellationToken, Task<IReadOnlyList<DevHubUser>>> authenticate)
        {
            return new DevHubService(new DevHubCacheService(_cacheFolder), [new AuthenticationProvider(authenticate)]);
        }

        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class AuthenticationProvider(Func<CancellationToken, Task<IReadOnlyList<DevHubUser>>> authenticate) : IDevHubProvider
        {
            public string DisplayName => "Authentication test";
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken) => authenticate(cancellationToken);
            public bool CanHandle(string remoteUrl) => throw new NotSupportedException();
            public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl) => throw new NotSupportedException();
            public Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> candidateRemoteUrls, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
        }
    }
}