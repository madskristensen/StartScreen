using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    [DoNotParallelize]
    public class DevHubRefreshBudgetTests
    {
        private readonly string _cacheFolder = Path.Combine(Environment.CurrentDirectory, "RefreshBudget_" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_cacheFolder))
                Directory.Delete(_cacheFolder, recursive: true);
        }

        [TestMethod]
        public async Task Refresh_DiscoversOnceAndSharesImmutableSnapshotAcrossAuthenticationAndAllSourcesAsync()
        {
            var remotes = new List<string> { "https://example.com/original" };
            var discoveries = 0;
            var provider = new BudgetProvider("one", 1);
            var contexts = new List<DevHubRefreshContext>();
            provider.Authenticate = context =>
            {
                contexts.Add(context);
                remotes[0] = "https://example.com/changed";
            };
            var service = Service([provider], _ =>
            {
                discoveries++;
                return Task.FromResult<IReadOnlyList<string>>(remotes);
            });

            await service.RefreshAsync(CancellationToken.None);

            Assert.AreEqual(1, discoveries);
            Assert.HasCount(4, provider.Contexts);
            Assert.IsTrue(provider.Contexts.All(context => ReferenceEquals(context, contexts[0])));
            Assert.AreSame(contexts[0].RemoteUrls, provider.Candidates);
            Assert.AreEqual("https://example.com/original", contexts[0].RemoteUrls.Single());

            await service.RefreshAsync(CancellationToken.None, forceRefresh: true);

            Assert.AreEqual(2, discoveries);
            Assert.AreNotSame(contexts[0], contexts[1]);
            Assert.AreEqual("https://example.com/changed", contexts[1].RemoteUrls.Single());
        }

        [TestMethod]
        public async Task Refresh_AzureDevOpsReusesDiscoveryForAuthenticationAndThreePersonalCategoriesAsync()
        {
            var discoveries = 0;
            var fallbackDiscoveries = 0;
            var organizationAuthentication = false;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                organizationAuthentication |= path == "/org/_apis/connectionData";
                var json = path.Contains("connectionData") ? "{\"authenticatedUser\":{\"id\":\"me\"}}"
                    : path.Contains("profiles/me") ? "{\"id\":\"me\",\"emailAddress\":\"me\"}"
                    : path.EndsWith("/repositories/repo") ? "{\"id\":\"e2d789d1-67ab-405a-9704-c72759720867\"}"
                    : path.EndsWith("/wiql") ? "{\"workItems\":[]}" : "{\"value\":[]}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }));
            var provider = new AzureDevOpsDevHubProvider(client,
                (_, _, _) => Task.FromResult(new DevHubCredential("me", "test-token")), (_, _) => { }, _ =>
                {
                    fallbackDiscoveries++;
                    return Task.FromResult<IReadOnlyList<string>>([]);
                }, () => ["dev.azure.com"]);
            var service = Service([provider], _ =>
            {
                discoveries++;
                return Task.FromResult<IReadOnlyList<string>>(["https://dev.azure.com/org/project/_git/repo"]);
            });

            var dashboard = await service.RefreshAsync(CancellationToken.None);

            Assert.AreEqual(1, discoveries);
            Assert.AreEqual(0, fallbackDiscoveries);
            Assert.IsTrue(organizationAuthentication);
            Assert.AreEqual(DevHubActivityStatus.Success, dashboard.Status);
            Assert.Contains("Azure DevOps/issues", dashboard.Sources.Keys);
            Assert.Contains("Azure DevOps/pulls", dashboard.Sources.Keys);
            Assert.Contains("Azure DevOps/builds", dashboard.Sources.Keys);
        }

        [TestMethod]
        public async Task Refresh_RepositoryBudgetIsGlobalAndDoesNotBlockPersonalPublishingAsync()
        {
            var active = 0;
            var maximum = 0;
            var calls = 0;
            var started = Signal();
            var release = Signal();
            var personalPublished = Signal();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task<DevHubActivitySnapshot> DetailAsync(RemoteRepoIdentifier repo, CancellationToken token)
            {
                Interlocked.Increment(ref calls);
                var concurrent = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, concurrent);
                if (concurrent == DevHubService.DashboardRepositoryConcurrency)
                    started.TrySetResult(true);
                try
                {
                    await Task.WhenAny(release.Task, Task.Delay(Timeout.Infinite, token));
                    token.ThrowIfCancellationRequested();
                    return new DevHubActivitySnapshot { Status = DevHubActivityStatus.Success, RepoIdentifier = repo };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
            var providers = new[] { new BudgetProvider("one", 6), new BudgetProvider("two", 6) };
            foreach (var provider in providers)
                provider.Detail = DetailAsync;
            var service = Service(providers);
            var operation = service.RefreshAsync(timeout.Token, new InlineProgress(dashboard =>
            {
                if (dashboard.Sources.ContainsKey("two/issues"))
                    personalPublished.TrySetResult(true);
            }));
            try
            {
                await AwaitSignalAsync(started.Task);
                await AwaitSignalAsync(personalPublished.Task);
                Assert.AreEqual(DevHubService.DashboardRepositoryConcurrency, calls);
                Assert.IsFalse(operation.IsCompleted);
            }
            finally
            {
                release.TrySetResult(true);
            }
            var result = await operation;

            Assert.AreEqual(12, calls);
            Assert.AreEqual(DevHubService.DashboardRepositoryConcurrency, maximum);
            Assert.AreEqual(0, active);
            Assert.HasCount(12, result.Sources.Values.Where(source => source.RepoIdentifier != null));
        }

        [TestMethod]
        [SuppressMessage("Usage", "VSTHRD003", Justification = "The test awaits its own refresh without a UI synchronization context.")]
        public async Task Refresh_CancellationStopsQueuedRepositoriesAndDrainsActiveCallsBeforeReturningAsync()
        {
            var active = 0;
            var calls = 0;
            var started = Signal();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var provider = new BudgetProvider("one", 20)
            {
                Detail = async (repo, token) =>
                {
                    Interlocked.Increment(ref active);
                    if (Interlocked.Increment(ref calls) == DevHubService.DashboardRepositoryConcurrency)
                        started.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                        return new DevHubActivitySnapshot { RepoIdentifier = repo };
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                },
            };
            var service = Service([provider]);
            var operation = service.RefreshAsync(cancellation.Token);
            await AwaitSignalAsync(started.Task);
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
            Assert.AreEqual(DevHubService.DashboardRepositoryConcurrency, calls);
            Assert.AreEqual(0, active);
            Assert.IsFalse(service.IsRefreshing);

            provider.Detail = (repo, _) => Task.FromResult(new DevHubActivitySnapshot { RepoIdentifier = repo });
            var result = await service.RefreshAsync(CancellationToken.None, forceRefresh: true);
            Assert.HasCount(20, result.Sources.Values.Where(source => source.RepoIdentifier != null));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Refresh_UnconfiguredBitbucketDoesNotWarnButRejectedConfiguredCredentialDoesAsync(bool retryCredentialAvailable)
        {
            var calls = 0;
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }));
            DevHubCredential? credential = null;
            var acquisitions = 0;
            var provider = new BitbucketDevHubProvider(client, _ => Task.FromResult(credential == null ? null!
                : ++acquisitions == 1 || retryCredentialAvailable ? credential : null!));
            var service = Service([provider]);

            var unconfigured = await service.RefreshAsync(CancellationToken.None);

            Assert.AreEqual(0, calls);
            Assert.AreEqual(DevHubActivityStatus.Success, unconfigured.Status);
            Assert.IsEmpty(unconfigured.ActivityWarnings);
            Assert.IsFalse(unconfigured.HasAuthentication);

            credential = new DevHubCredential("me", "test-token");
            var rejected = await service.RefreshAsync(CancellationToken.None, forceRefresh: true);

            Assert.AreEqual(retryCredentialAvailable ? 2 : 1, calls);
            Assert.AreEqual(DevHubActivityStatus.Unauthorized, rejected.Status);
            Assert.IsNotEmpty(rejected.ActivityWarnings);
            Assert.IsFalse(rejected.HasData);
        }

        [TestMethod]
        public async Task Refresh_UnconfiguredAzureDevOpsDoesNotWarnAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => throw new AssertFailedException("No credential should send HTTP requests.")));
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult<DevHubCredential>(null!), (_, _) => { },
                getCredentialHosts: () => ["dev.azure.com"]);

            var dashboard = await Service([provider]).RefreshAsync(CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Success, dashboard.Status);
            Assert.IsEmpty(dashboard.ActivityWarnings);
            Assert.IsFalse(dashboard.HasAuthentication);
        }

        [TestMethod]
        public async Task AzureDevOps_DirectProviderCallsKeepInjectedDiscoveryWithoutARefreshContextAsync()
        {
            var discoveries = 0;
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("connectionData")
                        ? "{\"authenticatedUser\":{\"id\":\"me\"}}"
                        : request.RequestUri.AbsolutePath.Contains("profiles/me") ? "{\"id\":\"me\"}"
                        : request.RequestUri.AbsolutePath.EndsWith("/wiql") ? "{\"workItems\":[]}" : "{\"value\":[]}"),
                }));
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(new DevHubCredential("me", "test-token")),
                (_, _) => { }, _ =>
                {
                    discoveries++;
                    return Task.FromResult<IReadOnlyList<string>>(["https://dev.azure.com/org/project/_git/repo"]);
                }, () => ["dev.azure.com"]);

            await provider.GetAuthenticatedUsersAsync(CancellationToken.None);
            await provider.GetUserIssuesAsync(CancellationToken.None);
            await provider.GetUserPullRequestsAsync(CancellationToken.None);
            await provider.GetUserCiRunsAsync(CancellationToken.None);

            Assert.AreEqual(4, discoveries);
        }

        private DevHubService Service(IReadOnlyList<IDevHubProvider> providers,
            Func<CancellationToken, Task<IReadOnlyList<string>>>? discover = null)
        {
            return new DevHubService(new DevHubCacheService(_cacheFolder), providers,
                discover ?? (_ => Task.FromResult<IReadOnlyList<string>>([])), () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
        }

        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        [SuppressMessage("Usage", "VSTHRD003", Justification = "The test awaits its own synchronization signal without a UI synchronization context.")]
        private static async Task AwaitSignalAsync(Task signal)
        {
            Assert.AreSame(signal, await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(10))));
            await signal;
        }

        private sealed class InlineProgress(Action<DevHubDashboard> report) : IProgress<DevHubDashboard>
        {
            public void Report(DevHubDashboard value) => report(value);
        }

        private sealed class BudgetProvider(string name, int repositoryCount) : IDevHubProvider, IDevHubActivitySource, IDevHubRefreshProvider
        {
            public Action<DevHubRefreshContext>? Authenticate { get; set; }
            public Func<RemoteRepoIdentifier, CancellationToken, Task<DevHubActivitySnapshot>>? Detail { get; set; }
            public List<DevHubRefreshContext> Contexts { get; } = [];
            public IReadOnlyList<string>? Candidates { get; private set; }
            public string DisplayName => name;
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(DevHubRefreshContext context, CancellationToken cancellationToken)
            {
                Authenticate?.Invoke(context);
                return Task.FromResult<IReadOnlyList<DevHubUser>>([new DevHubUser { Username = "me", ProviderName = name }]);
            }
            public Task<DevHubActivitySnapshot> FetchActivityAsync(DevHubActivityKind kind, DevHubActivityOptions options,
                RemoteRepoIdentifier repo, CancellationToken cancellationToken)
            {
                Contexts.Add(options.RefreshContext);
                return kind == DevHubActivityKind.Repository && Detail != null ? Detail(repo, cancellationToken)
                    : Task.FromResult(new DevHubActivitySnapshot { Status = DevHubActivityStatus.Success, RepoIdentifier = repo });
            }
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> candidateRemoteUrls, CancellationToken cancellationToken)
            {
                Candidates = candidateRemoteUrls;
                return Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>(Enumerable.Range(0, repositoryCount)
                    .Select(index => new RemoteRepoIdentifier("example.com", name, "repo" + index)).ToArray());
            }
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken) => throw new AssertFailedException("Expected explicit refresh context.");
            public bool CanHandle(string remoteUrl) => true;
            public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl) => throw new NotSupportedException();
            public Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
        }
    }
}