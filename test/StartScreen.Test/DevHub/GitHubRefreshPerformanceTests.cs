using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class GitHubRefreshPerformanceTests
    {
        private static readonly RemoteRepoIdentifier _repo = new("github.com", "owner", "repo");

        [TestMethod]
        public async Task Searches_ShareThreeSlotsAcrossIssuesAndPullRequestsAndReuseFreedSlotsAsync()
        {
            var firstThree = Signal();
            var fourth = Signal();
            var releaseFirst = Signal();
            var releaseAll = Signal();
            var requests = 0;
            var active = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (_, token) =>
            {
                var index = Interlocked.Increment(ref requests);
                Assert.IsLessThanOrEqualTo(3, Interlocked.Increment(ref active));
                try
                {
                    if (index == 3)
                        firstThree.TrySetResult(true);
                    if (index == 4)
                        fourth.TrySetResult(true);
                    await (index == 1 ? releaseFirst.Task : releaseAll.Task).WithCancellation(token);
                    return Json("{\"items\":[]}");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }));
            var operation = Task.WhenAll(
                GitHubDevHubProvider.GetUserIssuesAsync(client, Credential(), "me", new DevHubActivityOptions(), timeout.Token),
                GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me", new DevHubActivityOptions(), timeout.Token, populateMetadata: false));
            try
            {
                Assert.AreSame(firstThree.Task, await Task.WhenAny(firstThree.Task, Task.Delay(TimeSpan.FromSeconds(3))));
                Assert.AreEqual(3, requests);
                releaseFirst.TrySetResult(true);
                await fourth.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(operation.IsCompleted, "A freed slot must start the next search while two earlier searches are blocked.");
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseAll.TrySetResult(true);
                await operation;
            }
            Assert.AreEqual(9, requests);
            Assert.AreEqual(0, active);
        }

        [TestMethod]
        public async Task Searches_CancellationStopsActiveAndQueuedRequestsAsync()
        {
            var started = Signal();
            var requests = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (_, token) =>
            {
                if (Interlocked.Increment(ref requests) == 3)
                    started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Json("{\"items\":[]}");
            }));

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var operation = GitHubDevHubProvider.GetUserPullRequestsAsync(client, Credential(), "me",
                    new DevHubActivityOptions(includeCompleted: true), cancellation.Token, populateMetadata: false);
                try
                {
                    await started.Task.WithCancellation(cancellation.Token);
                }
                finally
                {
                    cancellation.Cancel();
                }
                await operation;
            });
            Assert.AreEqual(3, requests);
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task PullRequests_PublishInventoryBeforeMetadataAndKeepFinalOutcomeAsync(bool scoped, bool failMetadata)
        {
            using var directory = new TemporaryDirectory();
            var metadataStarted = Signal();
            var releaseMetadata = Signal();
            var published = Signal();
            DevHubDashboard? preview = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    metadataStarted.TrySetResult(true);
                    await releaseMetadata.Task.WithCancellation(token);
                    return failMetadata ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        : Json("{\"data\":{\"r0\":{\"pullRequest\":{\"state\":\"OPEN\",\"isDraft\":false,\"mergeable\":\"MERGEABLE\",\"mergeStateStatus\":\"CLEAN\",\"reviewDecision\":\"APPROVED\",\"reviewThreads\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":false}},\"commits\":{\"nodes\":[{\"commit\":{\"statusCheckRollup\":{\"state\":\"SUCCESS\",\"contexts\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":false}}}}}]}}}}}");
                }
                if (request.RequestUri!.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal))
                    return Json(request.RequestUri.AbsolutePath.EndsWith("/runs", StringComparison.Ordinal) ? "{\"workflow_runs\":[]}" : "[]");
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                return Json(query.Contains("is:pr") && query.Contains("author:me") ? "{\"items\":[{\"number\":1,\"title\":\"My PR\",\"state\":\"open\",\"user\":{\"login\":\"me\"},\"updated_at\":\"2026-11-07T19:00:00Z\",\"created_at\":\"2026-11-01T19:00:00Z\",\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"html_url\":\"https://github.com/owner/repo/pull/1\"}]}" : "{\"items\":[]}");
            }));
            var provider = new Provider(async (kind, options, repo, token) =>
            {
                if (kind != (scoped ? DevHubActivityKind.Repository : DevHubActivityKind.PullRequests))
                    return new DevHubActivitySnapshot();
                if (scoped)
                {
                    var detail = await GitHubDevHubProvider.GetRepoDetailAsync(client, Credential(), "me", repo!, options, token);
                    return new DevHubActivitySnapshot { RepoIdentifier = repo, PullRequests = detail.PullRequests,
                        Issues = detail.Issues, CiRuns = detail.CiRuns, Status = detail.Status, Message = detail.ActivityWarning,
                        HasCategoryOutcomes = detail.HasCategoryOutcomes, PullRequestsStatus = detail.PullRequestsStatus,
                        IssuesStatus = detail.IssuesStatus, CiRunsStatus = detail.CiRunsStatus };
                }
                var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserPullRequestsAsync(
                    client, Credential(), "me", options, token, repo), items => items.Count > 0, token);
                return new DevHubActivitySnapshot { RepoIdentifier = repo, PullRequests = result.Value,
                    Status = result.Status, Message = result.Message };
            });
            var cache = new DevHubCacheService(directory.Path);
            var service = Service(cache, provider);
            var progress = new InlineProgress(dashboard =>
            {
                if (preview == null && dashboard.IsRefreshing && dashboard.PullRequests.Count > 0)
                {
                    preview = dashboard;
                    published.TrySetResult(true);
                }
            });
            var operation = scoped
                ? service.ScopeToRepoAsync(_repo, timeout.Token, progress: progress)
                : service.RefreshAsync(timeout.Token, progress);
            try
            {
                await metadataStarted.Task.WithCancellation(timeout.Token);
                await published.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsNotNull(preview);
                Assert.AreEqual("My PR", preview.PullRequests.Single().Title);
                Assert.IsNull(preview.PullRequests.Single().CanMerge);
                StringAssert.Contains(preview.PullRequests.Single().MergeBlockReason, "Checking review and check details");
                Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "dashboard.json")), "An inventory preview must not be persisted as a final refresh.");
            }
            finally
            {
                releaseMetadata.TrySetResult(true);
            }
            var result = await operation;
            Assert.IsNotNull(result);
            Assert.IsFalse(result.IsRefreshing);
            Assert.HasCount(1, result.PullRequests);
            Assert.AreNotSame(preview!.PullRequests.Single(), result.PullRequests.Single());
            Assert.IsNull(preview.PullRequests.Single().CanMerge, "Enrichment must not mutate published inventory objects.");
            Assert.AreEqual(failMetadata ? null : (bool?)true, result.PullRequests.Single().CanMerge);
            var source = result.Sources.Values.Single(snapshot => snapshot.PullRequests.Count > 0);
            Assert.AreEqual(DevHubActivityStatus.Success, source.Status, "Optional metadata failures must not stale a successful inventory.");
            if (failMetadata)
                StringAssert.Contains(source.Message, "review and check details are unavailable");
            if (!scoped)
            {
                var persisted = await cache.ReadDashboardAsync(timeout.Token);
                Assert.IsNotNull(persisted);
                Assert.AreEqual(result.PullRequests.Single().CanMerge, persisted.PullRequests.Single().CanMerge);
            }
        }

        [TestMethod]
        public async Task Inventory_AccountChangePreventsPublishingAndCancelsRemainingWorkAsync()
        {
            var started = new TaskCompletionSource<DevHubActivityOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
            var generation = 0;
            var provider = new Provider(async (kind, options, _, token) =>
            {
                if (kind == DevHubActivityKind.PullRequests)
                {
                    started.TrySetResult(options);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return new DevHubActivitySnapshot();
            });
            using var directory = new TemporaryDirectory();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var service = Service(new DevHubCacheService(directory.Path), provider, () => generation);
            var leaked = false;
            var operation = service.RefreshAsync(timeout.Token, new InlineProgress(dashboard => leaked |= dashboard.PullRequests.Count > 0));
            var options = await started.Task.WithCancellation(timeout.Token);
            Interlocked.Increment(ref generation);
            options.ReportProgress!(new DevHubActivitySnapshot { PullRequests = [new DevHubPullRequest { Title = "Old account", Status = "open" }] });
            var result = await operation;
            options.ReportProgress(new DevHubActivitySnapshot { PullRequests = [new DevHubPullRequest { Title = "Late old account", Status = "open" }] });
            Assert.IsFalse(leaked);
            Assert.IsEmpty(result.PullRequests);
            Assert.IsEmpty(service.CurrentDashboard.PullRequests);
            Assert.IsFalse(service.IsRefreshing);
        }

        [TestMethod]
        public async Task Dashboard_PopulatesPersonalAndTeamViewItemsWhileAnotherSearchIsPendingAsync()
        {
            using var directory = new TemporaryDirectory();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var started = Signal();
            var published = Signal();
            var release = Signal();
            var view = new StartScreen.ToolWindows.Controls.DevHubActivityViewModel();
            view.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            view.SetIncomingFilter(DevHubWorkItemKind.PullRequest);
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                var query = Uri.UnescapeDataString(request.RequestUri!.Query);
                if (query.Contains("author:me"))
                {
                    started.TrySetResult(true);
                    await release.Task.WithCancellation(token);
                }
                if (!query.Contains("involves:me"))
                    return Json("{\"items\":[]}");
                return Json("{\"items\":[" + string.Join(",", new[] { "me", "teammate" }.Select((author, index) =>
                    "{\"number\":" + (index + 1) + ",\"title\":\"" + author + "\",\"state\":\"open\",\"user\":{\"login\":\"" + author
                    + "\"},\"created_at\":\"2026-11-12T10:00:00Z\",\"updated_at\":\"2026-11-12T10:00:00Z\",\"repository_url\":\"https://api.github.com/repos/owner/repo\",\"html_url\":\"https://github.com/owner/repo/pull/" + (index + 1) + "\"}")) + "]}");
            }));
            var provider = new Provider(async (kind, options, _, token) =>
            {
                if (kind != DevHubActivityKind.PullRequests)
                    return new DevHubActivitySnapshot();
                var result = await DevHubActivityRequest.RunAsync(() => GitHubDevHubProvider.GetUserPullRequestsAsync(
                    client, Credential(), "me", options, token), items => items.Count > 0, token);
                return new DevHubActivitySnapshot { PullRequests = result.Value, Status = result.Status, Message = result.Message };
            });
            var service = Service(new DevHubCacheService(directory.Path), provider);
            var operation = service.RefreshAsync(timeout.Token, new InlineProgress(dashboard =>
            {
                view.UpdateRepositoryActivity(dashboard.PullRequests, dashboard.Issues, dashboard.CiRuns, [], DevHubSortOrder.MostRecent, false);
                if (dashboard.IsRefreshing && view.MyWorkItems.Count > 0 && view.IncomingItems.Count > 0)
                    published.TrySetResult(true);
            }));
            try
            {
                await Task.WhenAll(started.Task, published.Task).WithCancellation(timeout.Token);
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsGreaterThan(0, view.MyWorkItems.Count);
                Assert.IsGreaterThan(0, view.IncomingItems.Count);
            }
            finally
            {
                release.TrySetResult(true);
                await operation;
            }
        }

        [TestMethod]
        public async Task Inventory_RetainsCachedItemsUntilCompletionWithoutRestoringCachedReadinessAsync()
        {
            using var directory = new TemporaryDirectory();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var seed = true;
            var release = Signal();
            var published = Signal();
            DevHubDashboard? preview = null;
            DevHubPullRequest Item(int id, string title) => new()
            {
                NumericId = id,
                Title = title,
                Status = "open",
                RepoIdentifier = new("github.com", "owner", "repo"),
                WebUrl = "https://github.com/owner/repo/pull/" + id,
                IsAuthoredByCurrentUser = true,
                CanMerge = seed ? true : null,
            };
            var provider = new Provider(async (kind, options, _, token) =>
            {
                if (kind != DevHubActivityKind.PullRequests)
                    return new DevHubActivitySnapshot();
                if (seed)
                    return new DevHubActivitySnapshot { PullRequests = [Item(1, "Cached"), Item(2, "Still cached")] };
                DevHubActivityRequest.ReportPullRequests(options, [Item(1, "Fresh inventory")]);
                await release.Task.WithCancellation(token);
                return new DevHubActivitySnapshot();
            });
            var service = Service(new DevHubCacheService(directory.Path), provider);
            await service.RefreshAsync(timeout.Token);
            seed = false;
            var operation = service.RefreshAsync(timeout.Token, new InlineProgress(dashboard =>
            {
                if (dashboard.IsRefreshing && dashboard.PullRequests.Any(pr => pr.Title == "Fresh inventory"))
                {
                    preview = dashboard;
                    published.TrySetResult(true);
                }
            }));
            try
            {
                await published.Task.WithCancellation(timeout.Token);
                Assert.IsNotNull(preview);
                Assert.HasCount(2, preview.PullRequests);
                Assert.IsNull(preview.PullRequests.Single(pr => pr.NumericId == 1).CanMerge);
                Assert.AreEqual("Still cached", preview.PullRequests.Single(pr => pr.NumericId == 2).Title);
                Assert.IsFalse(operation.IsCompleted);
            }
            finally
            {
                release.TrySetResult(true);
                await operation;
            }
            Assert.IsEmpty((await operation)!.PullRequests, "Successful final results must replace, not retain, cached inventory.");
            Assert.HasCount(2, preview!.PullRequests, "Final completion must not mutate the previously published preview.");
        }

        [TestMethod]
        public async Task Inventory_LegacyRepositoryCacheRemainsVisibleUntilAggregateFeedsFinishAsync()
        {
            using var directory = new TemporaryDirectory();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var seed = true;
            var release = Signal();
            var published = Signal();
            var repo = new RemoteRepoIdentifier("github.com", "owner", "repo");
            DevHubDashboard? preview = null;
            var provider = new Provider(async (kind, _, _, token) =>
            {
                if (!seed)
                    await release.Task.WithCancellation(token);
                return kind == DevHubActivityKind.Repository ? new DevHubActivitySnapshot
                {
                    RepoIdentifier = repo,
                    PullRequests = [new DevHubPullRequest { NumericId = 1, Title = "Cached team PR", RepoIdentifier = repo, Status = "open" }],
                    Issues = [new DevHubIssue { NumericId = 1, Title = "Cached team issue", RepoIdentifier = repo, State = "open" }],
                    CiRuns = [new DevHubCiRun { Name = "Cached build", RepoIdentifier = repo }],
                } : new DevHubActivitySnapshot();
            }) { ActivityRepositories = [repo] };
            var service = Service(new DevHubCacheService(directory.Path), provider);
            await service.RefreshAsync(timeout.Token);
            seed = false;
            provider.ActivityRepositories = [];
            var operation = service.RefreshAsync(timeout.Token, new InlineProgress(dashboard =>
            {
                if (dashboard.IsRefreshing && !dashboard.Sources.Keys.Any(key => key.Contains("/repo/"))
                    && dashboard.PullRequests.Count > 0 && dashboard.Issues.Count > 0 && dashboard.CiRuns.Count > 0)
                {
                    preview = dashboard;
                    published.TrySetResult(true);
                }
            }));
            try
            {
                await published.Task.WithCancellation(timeout.Token);
                Assert.IsNotNull(preview);
                Assert.HasCount(1, preview.PullRequests);
                Assert.HasCount(1, preview.Issues);
                Assert.HasCount(1, preview.CiRuns);
                Assert.IsFalse(operation.IsCompleted);
            }
            finally
            {
                release.TrySetResult(true);
                await operation;
            }
            var final = await operation;
            Assert.IsNotNull(final);
            Assert.IsEmpty(final.PullRequests);
            Assert.IsEmpty(final.Issues);
            Assert.IsEmpty(final.CiRuns);
        }

        private static DevHubService Service(DevHubCacheService cache, IDevHubProvider provider, Func<int>? generation = null) =>
            new(cache, [provider], _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent,
                () => string.Empty, generation ?? (() => 0));
        private static DevHubCredential Credential() => new("me", "synthetic-test-token");
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        }

        private sealed class InlineProgress(Action<DevHubDashboard> report) : IProgress<DevHubDashboard>
        {
            public void Report(DevHubDashboard value) => report(value);
        }

        private sealed class Provider(Func<DevHubActivityKind, DevHubActivityOptions, RemoteRepoIdentifier?, CancellationToken, Task<DevHubActivitySnapshot>> fetch)
            : IDevHubProvider, IDevHubActivitySource
        {
            public string DisplayName => "GitHub test";
            public IReadOnlyList<RemoteRepoIdentifier> ActivityRepositories { get; set; } = [];
            public Task<DevHubActivitySnapshot> FetchActivityAsync(DevHubActivityKind kind, DevHubActivityOptions options,
                RemoteRepoIdentifier repo, CancellationToken token) => fetch(kind, options, repo, token);
            public bool CanHandle(string url) => url.Contains("github.com");
            public RemoteRepoIdentifier ParseRemoteUrl(string url) => RemoteRepoIdentifier.TryParse(url);
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken token) =>
                Task.FromResult<IReadOnlyList<DevHubUser>>([new DevHubUser { Username = "me", IdentityId = "me", Host = "github.com", ProviderName = DisplayName }]);
            public Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken token) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken token) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken token) => throw new NotSupportedException();
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken token) => throw new NotSupportedException();
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> urls, CancellationToken token) => Task.FromResult(ActivityRepositories);
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maximum, CancellationToken token) => throw new NotSupportedException();
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken token) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => "https://github.com/" + repo.DisplayName;
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => GetRepoWebUrl(repo) + "/pulls";
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => GetRepoWebUrl(repo) + "/issues";
            public string GetCiRunsWebUrl(RemoteRepoIdentifier repo) => GetRepoWebUrl(repo) + "/actions";
        }
    }
}
