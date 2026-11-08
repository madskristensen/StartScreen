using System.IO;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubScopedCacheTests
    {
        [TestMethod]
        public async Task ReSelectingRepositoryReusesItsSuccessfulViewWithoutExtendingLifetimeAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var originalTime = now;
            var calls = 0;
            var provider = new Provider { Fetch = (repo, _) =>
            {
                calls++;
                return Task.FromResult(Ready(repo));
            } };
            var service = Service(provider, () => now);
            await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);
            await service.ScopeToRepoAsync(Repo(1), CancellationToken.None);
            now = now.AddMinutes(4);

            var cached = await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);

            Assert.AreEqual(2, calls, "Returning to a recent repository must not fetch its details again.");
            Assert.AreEqual("repo0", cached.PullRequests.Single().Title);
            Assert.AreEqual(originalTime.UtcDateTime, cached.FetchedAt);
            now = now.AddMinutes(1);
            await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);
            Assert.AreEqual(3, calls, "A cache hit must not move the five-minute expiration.");
        }

        [TestMethod]
        public async Task ForcedSelectionReloadsSuccessfulViewAsync()
        {
            var calls = 0;
            var provider = new Provider { Fetch = (repo, _) =>
            {
                calls++;
                var snapshot = Ready(repo);
                snapshot.PullRequests[0].Title = "revision " + calls;
                return Task.FromResult(snapshot);
            } };
            var service = Service(provider);
            await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);

            var refreshed = await service.ScopeToRepoAsync(Repo(0), CancellationToken.None, forceRefresh: true);

            Assert.AreEqual(2, calls);
            Assert.AreEqual("revision 2", refreshed.PullRequests.Single().Title);
        }

        [TestMethod]
        public async Task FailedSelectionIsRetriedInsteadOfBecomingSuccessfulCacheHitAsync()
        {
            var calls = 0;
            var provider = new Provider { Fetch = (repo, _) => Task.FromResult(++calls == 1 ? Failed(repo) : Ready(repo)) };
            var service = Service(provider);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, (await service.ScopeToRepoAsync(Repo(0), CancellationToken.None)).Status);

            var retried = await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);

            Assert.AreEqual(2, calls);
            Assert.AreEqual(DevHubActivityStatus.Success, retried.Status);
            Assert.AreEqual("repo0", retried.PullRequests.Single().Title);
        }

        [TestMethod]
        public async Task CapacityEvictsOldestSnapshotButRetainsRecentFailureFallbackAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var provider = new Provider();
            var service = Service(provider, () => now);
            for (var index = 0; index <= DevHubService.ScopedSourceCapacity; index++)
            {
                now = now.AddSeconds(1);
                await service.ScopeToRepoAsync(Repo(index), CancellationToken.None);
                Assert.IsLessThanOrEqualTo(DevHubService.ScopedSourceCapacity, service.ScopedSourceCount);
            }
            provider.Fetch = (repo, _) => Task.FromResult(Failed(repo));

            var evicted = await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);
            var recent = await service.ScopeToRepoAsync(Repo(DevHubService.ScopedSourceCapacity), CancellationToken.None, forceRefresh: true);

            Assert.IsEmpty(evicted.PullRequests);
            Assert.HasCount(1, recent.PullRequests);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, recent.Sources.Values.Single().Status);
            Assert.AreEqual(DevHubService.ScopedSourceCapacity, service.ScopedSourceCount);
        }

        [TestMethod]
        public async Task ExpiredSnapshotCannotBeUsedAsFailureFallbackAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var provider = new Provider();
            var service = Service(provider, () => now);
            await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);
            now = now.AddMinutes(5);

            Assert.AreEqual(0, service.ScopedSourceCount);
            provider.Fetch = (repo, _) => Task.FromResult(Failed(repo));
            var result = await service.ScopeToRepoAsync(Repo(0), CancellationToken.None);

            Assert.IsEmpty(result.PullRequests);
        }

        [TestMethod]
        [DataRow("identity")]
        [DataRow("query")]
        [DataRow("credential")]
        [DataRow("token")]
        [DataRow("limit")]
        [DataRow("completed")]
        public async Task ContextChangeRemovesOldEntriesInsteadOfResurrectingThemAsync(string change)
        {
            var provider = new Provider();
            var query = "before";
            var generation = 0;
            var service = Service(provider, getQuery: () => query, getGeneration: () => generation);
            var originalOptions = new DevHubActivityOptions(10);
            await service.ScopeToRepoAsync(Repo(0), CancellationToken.None, originalOptions);
            var options = originalOptions;
            switch (change)
            {
                case "identity": provider.Identity = "after"; break;
                case "query": query = "after"; break;
                case "credential": generation++; break;
                case "token": provider.CredentialContext = "new-token-scope"; break;
                case "limit": options = new DevHubActivityOptions(20); break;
                case "completed": options = new DevHubActivityOptions(10, includeCompleted: true); break;
            }
            await service.ScopeToRepoAsync(Repo(1), CancellationToken.None, options);
            Assert.AreEqual(1, service.ScopedSourceCount);

            provider.Identity = "before";
            query = "before";
            generation = 0;
            provider.CredentialContext = null;
            provider.Fetch = (repo, _) => Task.FromResult(Failed(repo));
            var result = await service.ScopeToRepoAsync(Repo(0), CancellationToken.None, originalOptions);

            Assert.IsEmpty(result.PullRequests);
            Assert.AreEqual(1, service.ScopedSourceCount);
        }

        [TestMethod]
        public async Task ConcurrentRepositoriesInSameContextKeepIndependentFallbacksAsync()
        {
            var first = Signal();
            var second = Signal();
            var provider = new Provider { Fetch = (repo, _) => repo.Repo == "repo0" ? first.Task : second.Task };
            var service = Service(provider);
            var firstRequest = service.ScopeToRepoAsync(Repo(0), CancellationToken.None);
            var secondRequest = service.ScopeToRepoAsync(Repo(1), CancellationToken.None);
            second.SetResult(Ready(Repo(1)));
            await secondRequest;
            first.SetResult(Ready(Repo(0)));
            await firstRequest;
            Assert.AreEqual(2, service.ScopedSourceCount);
            provider.Fetch = (repo, _) => Task.FromResult(Failed(repo));

            var results = await Task.WhenAll(service.ScopeToRepoAsync(Repo(0), CancellationToken.None, forceRefresh: true),
                service.ScopeToRepoAsync(Repo(1), CancellationToken.None, forceRefresh: true));

            Assert.AreEqual("repo0", results[0].PullRequests.Single().Title);
            Assert.AreEqual("repo1", results[1].PullRequests.Single().Title);
            Assert.AreEqual(2, service.ScopedSourceCount);
        }

        [TestMethod]
        public async Task OlderInFlightOptionsCannotRepopulateNewContextAsync()
        {
            var old = Signal();
            var provider = new Provider { Fetch = (repo, _) => repo.Repo == "repo0" ? old.Task : Task.FromResult(Ready(repo)) };
            var service = Service(provider);
            var oldRequest = service.ScopeToRepoAsync(Repo(0), CancellationToken.None, new DevHubActivityOptions(10));
            await service.ScopeToRepoAsync(Repo(1), CancellationToken.None, new DevHubActivityOptions(20));

            old.SetResult(Ready(Repo(0)));
            await oldRequest;

            Assert.AreEqual(1, service.ScopedSourceCount);
            provider.Fetch = (repo, _) => Task.FromResult(Failed(repo));
            var result = await service.ScopeToRepoAsync(Repo(0), CancellationToken.None, new DevHubActivityOptions(20));
            Assert.IsEmpty(result.PullRequests);
        }

        [TestMethod]
        public async Task OlderAuthenticationCannotReplaceNewIdentityContextAsync()
        {
            var oldAuthentication = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var provider = new Provider { Authenticate = () => ++calls == 1 ? oldAuthentication.Task : Task.FromResult(Users("after")) };
            var service = Service(provider);
            var old = service.ScopeToRepoAsync(Repo(0), CancellationToken.None);
            await service.ScopeToRepoAsync(Repo(1), CancellationToken.None);

            oldAuthentication.SetResult(Users("before"));
            await old;

            Assert.AreEqual(1, service.ScopedSourceCount);
            provider.Fetch = (repo, _) => Task.FromResult(Failed(repo));
            Assert.IsEmpty((await service.ScopeToRepoAsync(Repo(0), CancellationToken.None)).PullRequests);
        }

        [TestMethod]
        public async Task ClearCacheFencesInFlightRepositoryResultsAsync()
        {
            var release = Signal();
            var provider = new Provider { Fetch = (_, _) => release.Task };
            var service = Service(provider);
            var request = service.ScopeToRepoAsync(Repo(0), CancellationToken.None);
            service.ClearCache();

            release.SetResult(Ready(Repo(0)));
            await request;

            Assert.AreEqual(0, service.ScopedSourceCount);
        }

        [TestMethod]
        public async Task DifferentServerPortsNeverShareScopedFailureFallbackAsync()
        {
            var provider = new Provider { IdentityHost = "tfs.contoso.com" };
            var service = Service(provider);
            var first = RemoteRepoIdentifier.TryParse("https://tfs.contoso.com:8443/tfs/Collection/Project/_git/Repo")!;
            var second = RemoteRepoIdentifier.TryParse("https://tfs.contoso.com:9443/tfs/Collection/Project/_git/Repo")!;
            await service.ScopeToRepoAsync(first, CancellationToken.None);
            provider.Fetch = (repo, _) => Task.FromResult(Failed(repo));

            var result = await service.ScopeToRepoAsync(second, CancellationToken.None);

            Assert.IsEmpty(result.PullRequests);
            Assert.AreEqual(2, service.ScopedSourceCount);
        }

        private static DevHubService Service(Provider provider, Func<DateTimeOffset>? now = null, Func<string>? getQuery = null, Func<int>? getGeneration = null)
        {
            return new DevHubService(new DevHubCacheService(Path.Combine(Environment.CurrentDirectory, "scoped-cache-" + Guid.NewGuid().ToString("N"))),
                [provider], _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent,
                getQuery ?? (() => string.Empty), getGeneration ?? (() => 0), now!);
        }

        private static RemoteRepoIdentifier Repo(int number) => new("github.com", "owner", "repo" + number);
        private static TaskCompletionSource<DevHubActivitySnapshot> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static IReadOnlyList<DevHubUser> Users(string identity) => [new DevHubUser { ProviderName = "Test", Host = "github.com", Username = identity, IdentityId = identity }];
        private static DevHubActivitySnapshot Ready(RemoteRepoIdentifier repo) => new()
        {
            Status = DevHubActivityStatus.Success,
            RepoIdentifier = repo,
            PullRequests = [new DevHubPullRequest { Title = repo.Repo, RepoIdentifier = repo, WebUrl = "https://github.com/owner/" + repo.Repo + "/pull/1" }],
        };
        private static DevHubActivitySnapshot Failed(RemoteRepoIdentifier repo) => new() { Status = DevHubActivityStatus.Unavailable, RepoIdentifier = repo };

        private sealed class Provider : IDevHubProvider, IDevHubActivitySource
        {
            internal string Identity { get; set; } = "before";
            internal string IdentityHost { get; set; } = "github.com";
            internal string? CredentialContext { get; set; }
            internal Func<Task<IReadOnlyList<DevHubUser>>>? Authenticate { get; set; }
            internal Func<RemoteRepoIdentifier, DevHubActivityOptions, Task<DevHubActivitySnapshot>> Fetch { get; set; } = (repo, _) => Task.FromResult(Ready(repo));
            public string DisplayName => "Test";
            public bool CanHandle(string remoteUrl) => true;
            public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl) => RemoteRepoIdentifier.TryParse(remoteUrl);
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
            {
                if (Authenticate != null)
                    return Authenticate();
                return Task.FromResult<IReadOnlyList<DevHubUser>>([new DevHubUser
                {
                    ProviderName = "Test", Host = IdentityHost, Username = Identity, IdentityId = Identity, CredentialContext = CredentialContext,
                }]);
            }
            public async Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken) => (await GetAuthenticatedUsersAsync(cancellationToken)).Single();
            public Task<DevHubActivitySnapshot> FetchActivityAsync(DevHubActivityKind kind, DevHubActivityOptions options, RemoteRepoIdentifier repo, CancellationToken cancellationToken) => Fetch(repo, options);
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> candidateRemoteUrls, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => "https://github.com/owner/" + repo.Repo;
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => GetRepoWebUrl(repo) + "/pulls";
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => GetRepoWebUrl(repo) + "/issues";
        }
    }
}