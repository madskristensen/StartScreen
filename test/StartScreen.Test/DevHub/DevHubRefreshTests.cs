using System.IO;
using Microsoft.VisualStudio.Threading;
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

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Refresh_PersonalPullRequestsSurviveRepositoryLimitsInEitherCompletionOrderAsync(bool repositoryFirst)
        {
            var repo = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");
            var personal = new TaskCompletionSource<IReadOnlyList<DevHubPullRequest>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var repository = new TaskCompletionSource<DevHubRepoDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = Signal();
            var older = new DevHubPullRequest { Title = "Older personal", WebUrl = "https://example.com/pull/1", Status = "open", IsAuthoredByCurrentUser = true, RepoIdentifier = repo };
            var own = new DevHubPullRequest { Title = "Personal", WebUrl = "https://example.com/pull/2", Status = "open", IsAuthoredByCurrentUser = true, RepoIdentifier = repo };
            var enriched = new DevHubPullRequest { Title = own.Title, WebUrl = own.WebUrl, Status = "open", IsAuthoredByCurrentUser = true, RepoIdentifier = repo, CiStatus = "failure" };
            var incoming = new DevHubPullRequest { Title = "Incoming", WebUrl = "https://example.com/pull/3", Status = "open", RepoIdentifier = repo };
            var provider = new AuthenticationProvider(_ => Task.FromResult<IReadOnlyList<DevHubUser>>([new DevHubUser { Username = "me" }]))
            {
                ReadPullRequests = _ => personal.Task,
                ActivityRepo = repo,
                Detail = repository.Task,
            };
            var cache = new DevHubCacheService(_cacheFolder);
            var service = new DevHubService(cache, [provider], _ => Task.FromResult<IReadOnlyList<string>>(["https://example.com/repo"]),
                () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
            var detail = new DevHubRepoDetail { RepoIdentifier = repo, PullRequests = [enriched, incoming] };
            var progress = new InlineProgress(dashboard =>
            {
                if (dashboard.PullRequests.Any(pr => pr.Title == (repositoryFirst ? "Incoming" : "Older personal")))
                    published.TrySetResult(true);
            });
            var refresh = service.RefreshAsync(CancellationToken.None, progress);
            try
            {
                if (repositoryFirst)
                    repository.SetResult(detail);
                else
                    personal.SetResult([older, own]);
                Assert.AreSame(published.Task, await Task.WhenAny(published.Task, Task.Delay(TimeSpan.FromSeconds(10))));
                Assert.IsFalse(refresh.IsCompleted);
            }
            finally
            {
                repository.TrySetResult(detail);
                personal.TrySetResult([older, own]);
            }
            var result = await refresh;

            Assert.HasCount(3, result.PullRequests);
            Assert.AreSame(older, result.PullRequests.Single(pr => pr.WebUrl == older.WebUrl));
            Assert.AreSame(enriched, result.PullRequests.Single(pr => pr.WebUrl == own.WebUrl));
            Assert.HasCount(2, DevHubWorkItem.GetMyWork(result.PullRequests, [], []));
            Assert.HasCount(1, DevHubWorkItem.GetIncoming(result.PullRequests, [], []));
            var cached = await cache.ReadDashboardAsync();
            Assert.IsNotNull(cached);
            Assert.HasCount(2, DevHubWorkItem.GetMyWork(cached.PullRequests, [], []));
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
            var started = Signal();
            var calls = 0;
            var service = Service(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult(true);
                    await release.Task;
                }
                return Array.Empty<DevHubUser>();
            });

            var first = service.RefreshAsync(CancellationToken.None);
            var forced = service.RefreshAsync(CancellationToken.None, forceRefresh: true);
            var waited = !forced.IsCompleted;
            var callsBeforeRelease = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await started.Task.WithCancellation(timeout.Token);
                callsBeforeRelease = calls;
            }
            finally
            {
                release.SetResult(true);
            }
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
            var started = Signal();
            var calls = 0;
            var service = Service(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult(true);
                    await release.Task;
                }
                return Array.Empty<DevHubUser>();
            });
            using var cancellation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = service.RefreshAsync(CancellationToken.None);

            try
            {
                await started.Task.WithCancellation(timeout.Token);
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

            var failed = await service.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, failed.Status);
            Assert.IsFalse(failed.HasAuthentication);
            Assert.IsFalse(failed.HasData);
            Assert.IsFalse(service.IsRefreshing);
            await service.RefreshAsync(CancellationToken.None, forceRefresh: true);

            Assert.AreEqual(2, calls);
            Assert.IsFalse(service.IsRefreshing);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Refresh_PublishesSourcesIndependentlyAndMergesWithoutDuplicatesAsync(bool repositoryFirst)
        {
            var github = new RemoteRepoIdentifier("github.com", "owner", "repo");
            var ado = new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");
            var issues = new TaskCompletionSource<IReadOnlyList<DevHubIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var detail = new TaskCompletionSource<DevHubRepoDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = Signal();
            var provider = new AuthenticationProvider(_ => Task.FromResult<IReadOnlyList<DevHubUser>>([new DevHubUser { Username = "me" }]))
            {
                ReadIssues = _ => issues.Task,
                ActivityRepo = ado,
                Detail = detail.Task,
            };
            var cache = new DevHubCacheService(_cacheFolder);
            cache.WriteDashboard(new DevHubDashboard
            {
                Issues = [new DevHubIssue { Title = "Old GitHub", RepoIdentifier = github }, new DevHubIssue { Title = "Old ADO", RepoIdentifier = ado }],
            });
            var service = new DevHubService(cache, [provider], _ => Task.FromResult<IReadOnlyList<string>>(["https://example.com/repo"]),
                () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
            await service.LoadFromCacheAsync();
            var freshIssue = new DevHubIssue { Title = "Fresh GitHub", RepoIdentifier = github };
            var aggregateIssue = new DevHubIssue { Title = "Project ADO", WebUrl = "https://dev.azure.com/org/project/_workitems/edit/1" };
            var freshDetail = new DevHubRepoDetail
            {
                RepoIdentifier = ado,
                Issues = [new DevHubIssue { Title = "Fresh ADO", RepoIdentifier = ado, WebUrl = aggregateIssue.WebUrl }],
                ActivityWarning = "Some issue activity could not be loaded.",
            };
            var progress = new InlineProgress(dashboard =>
            {
                if (dashboard.Issues.Any(issue => issue.Title == (repositoryFirst ? "Fresh ADO" : "Fresh GitHub")))
                    published.TrySetResult(true);
            });
            var refresh = service.RefreshAsync(CancellationToken.None, progress);
            try
            {
                if (repositoryFirst)
                    detail.SetResult(freshDetail);
                else
                    issues.SetResult([freshIssue, aggregateIssue]);

                Assert.AreSame(published.Task, await Task.WhenAny(published.Task, Task.Delay(TimeSpan.FromSeconds(10))));
                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsTrue(service.IsRefreshing);
                Assert.IsFalse(service.CurrentDashboard.Issues.Any(issue => issue.Title == "Old ADO") && repositoryFirst);
            }
            finally
            {
                issues.TrySetResult([freshIssue, aggregateIssue]);
                detail.TrySetResult(freshDetail);
            }
            var result = await refresh;

            CollectionAssert.AreEquivalent(new[] { "Fresh GitHub", "Fresh ADO" }, result.Issues.Select(issue => issue.Title).ToArray());
            var cached = await cache.ReadDashboardAsync();
            Assert.IsNotNull(cached);
            CollectionAssert.AreEquivalent(new[] { "Fresh GitHub", "Fresh ADO" }, cached.Issues.Select(issue => issue.Title).ToArray());
            Assert.AreEqual(ado, result.Issues.Single(issue => issue.Title == "Fresh ADO").RepoIdentifier);
            Assert.AreEqual(freshDetail.ActivityWarning, result.FilterByRepo(ado).ActivityWarning);
            Assert.AreEqual(freshDetail.ActivityWarning, cached.FilterByRepo(ado).ActivityWarning);
            Assert.IsNull(cached.FilterByRepo(github).ActivityWarning);
            Assert.IsFalse(service.IsRefreshing);
        }

        private sealed class InlineProgress(Action<DevHubDashboard> report) : IProgress<DevHubDashboard>
        {
            public void Report(DevHubDashboard value) => report(value);
        }

        private DevHubService Service(Func<CancellationToken, Task<IReadOnlyList<DevHubUser>>> authenticate)
        {
            return new DevHubService(new DevHubCacheService(_cacheFolder), [new AuthenticationProvider(authenticate)],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
        }

        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class AuthenticationProvider(Func<CancellationToken, Task<IReadOnlyList<DevHubUser>>> authenticate) : IDevHubProvider
        {
            public Func<CancellationToken, Task<IReadOnlyList<DevHubIssue>>> ReadIssues { get; set; } =
                _ => Task.FromResult<IReadOnlyList<DevHubIssue>>(Array.Empty<DevHubIssue>());
            public Func<CancellationToken, Task<IReadOnlyList<DevHubPullRequest>>> ReadPullRequests { get; set; } =
                _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([]);
            public RemoteRepoIdentifier? ActivityRepo { get; set; }
            public Task<DevHubRepoDetail>? Detail { get; set; }
            public string DisplayName => "Authentication test";
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken) => authenticate(cancellationToken);
            public bool CanHandle(string remoteUrl) => throw new NotSupportedException();
            public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl) => throw new NotSupportedException();
            public Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken) => ReadPullRequests(cancellationToken);
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken) => ReadIssues(cancellationToken);
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DevHubCiRun>>([]);
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> candidateRemoteUrls, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>(ActivityRepo == null ? [] : [ActivityRepo]);
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken) => Detail ?? throw new NotSupportedException();
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
        }
    }
}