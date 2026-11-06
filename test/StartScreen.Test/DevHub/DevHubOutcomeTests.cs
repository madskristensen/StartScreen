using System.IO;
using System.Net;
using System.Net.Http;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubOutcomeTests
    {
        private readonly RemoteRepoIdentifier _repo = new RemoteRepoIdentifier("github.com", "owner", "repo");
        private string _folder = null!;

        [TestInitialize]
        public void Setup()
        {
            _folder = Path.Combine(Environment.CurrentDirectory, "ActivityOutcomeTest_" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        [TestMethod]
        [DataRow(401, DevHubActivityStatus.Unauthorized)]
        [DataRow(403, DevHubActivityStatus.Unavailable)]
        [DataRow(429, DevHubActivityStatus.Unavailable)]
        [DataRow(503, DevHubActivityStatus.Unavailable)]
        public async Task Request_FailedEmptyIsNotSuccessfulEmptyAsync(int code, DevHubActivityStatus expected)
        {
            var failed = await DevHubActivityRequest.RunAsync(() =>
            {
                using var response = DevHubActivityRequest.Observe(new HttpResponseMessage((HttpStatusCode)code));
                return Task.FromResult<IReadOnlyList<DevHubIssue>>([]);
            }, items => items.Count > 0, CancellationToken.None);
            var empty = await DevHubActivityRequest.RunAsync(() => Task.FromResult<IReadOnlyList<DevHubIssue>>([]),
                items => items.Count > 0, CancellationToken.None);

            Assert.AreEqual(expected, failed.Status);
            Assert.IsEmpty(failed.Value);
            Assert.AreEqual(DevHubActivityStatus.Success, empty.Status);
        }

        [TestMethod]
        public async Task Refresh_FailureRetainsSourceButSuccessfulEmptyClearsOnlyItsSourceAsync()
        {
            var provider = new Provider("GitHub")
            {
                Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("old issue")]),
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("old pull")]),
            };
            var service = Service(provider);
            var first = await service.RefreshAsync(CancellationToken.None);
            var fetchedAt = first.Sources["GitHub/issues"].FetchedAt;
            provider.Issues = _ => Task.FromException<IReadOnlyList<DevHubIssue>>(new HttpRequestException("Offline"));
            provider.Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([]);

            var failed = await service.RefreshAsync(CancellationToken.None);
            Assert.AreEqual("old issue", failed.Issues.Single().Title);
            Assert.IsEmpty(failed.PullRequests);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, failed.Sources["GitHub/issues"].Status);
            Assert.AreEqual(DevHubActivityStatus.Success, failed.Sources["GitHub/pulls"].Status);
            Assert.AreEqual(fetchedAt, failed.Sources["GitHub/issues"].FetchedAt);
            StringAssert.Contains(failed.ActivityWarnings["GitHub/issues"], "stale");
            Assert.AreEqual(DevHubActivityStatus.Partial, failed.Status);
            Assert.IsTrue(service.IsCacheStale());

            var persisted = await new DevHubCacheService(_folder).ReadDashboardAsync();
            Assert.IsNotNull(persisted);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, persisted.Sources["GitHub/issues"].Status);
            Assert.AreEqual(fetchedAt, persisted.Sources["GitHub/issues"].FetchedAt);
            Assert.AreEqual("old issue", persisted.Issues.Single().Title);
            Assert.IsEmpty(persisted.PullRequests);

            provider.Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([]);
            var recovered = await service.RefreshAsync(CancellationToken.None);
            Assert.IsEmpty(recovered.Issues);
            Assert.IsEmpty(recovered.ActivityWarnings);
            Assert.AreEqual(DevHubActivityStatus.Success, recovered.Status);
        }

        [TestMethod]
        public async Task Refresh_ProviderFailureDoesNotDropAnotherProvidersSuccessfulEmptyAsync()
        {
            var first = new Provider("first") { Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("first")]) };
            var second = new Provider("second") { Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("second")]) };
            var service = Service(first, second);
            await service.RefreshAsync(CancellationToken.None);
            first.Issues = _ => Task.FromException<IReadOnlyList<DevHubIssue>>(new HttpRequestException("Unavailable"));
            second.Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([]);

            var dashboard = await service.RefreshAsync(CancellationToken.None);

            Assert.AreEqual("first", dashboard.Issues.Single().Title);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, dashboard.Sources["first/issues"].Status);
            Assert.AreEqual(DevHubActivityStatus.Success, dashboard.Sources["second/issues"].Status);
            Assert.AreEqual(DevHubActivityStatus.Partial, dashboard.Status);
        }

        [TestMethod]
        public async Task Refresh_AccountSwitchNeverPublishesOldItemsOrOwnershipAsync()
        {
            var provider = new Provider("GitHub") { Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("previous account")]) };
            var service = Service(provider);
            await service.RefreshAsync(CancellationToken.None);
            provider.Username = "new account";
            provider.Pulls = _ => Task.FromException<IReadOnlyList<DevHubPullRequest>>(new HttpRequestException("Offline"));
            var leaked = false;

            var dashboard = await service.RefreshAsync(CancellationToken.None, new InlineProgress(value => leaked |= value.PullRequests.Count > 0));

            Assert.IsFalse(leaked);
            Assert.IsEmpty(dashboard.PullRequests);
            Assert.AreEqual("new account", dashboard.Users.Single().Username);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, dashboard.Sources["GitHub/pulls"].Status);
            var cached = await new DevHubCacheService(_folder).ReadDashboardAsync();
            Assert.IsNotNull(cached);
            Assert.IsEmpty(cached.PullRequests);
        }

        [TestMethod]
        [DataRow("account")]
        [DataRow("identity")]
        [DataRow("query")]
        [DataRow("limit")]
        [DataRow("completed")]
        [DataRow("remotes")]
        public async Task Cache_RejectsIdentityAndQueryContextMismatchesAsync(string change)
        {
            var provider = new Provider("GitHub") { Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("cached")]) };
            var query = "first";
            IReadOnlyList<string> remotes = ["https://github.com/owner/repo"];
            DevHubService CreateService() => new DevHubService(new DevHubCacheService(_folder), [provider],
                _ => Task.FromResult(remotes), () => DevHubSortOrder.MostRecent, () => query, () => 0);
            await CreateService().RefreshAsync(CancellationToken.None);
            var matching = await CreateService().LoadFromCacheAsync();
            Assert.IsNotNull(matching);
            Assert.AreEqual("cached", matching.Issues.Single().Title);
            var options = new DevHubActivityOptions();
            switch (change)
            {
                case "account": provider.Username = "other"; break;
                case "identity": provider.IdentityId = "other immutable identity"; break;
                case "query": query = "second"; break;
                case "limit": options = new DevHubActivityOptions(120); break;
                case "completed": options = new DevHubActivityOptions(includeCompleted: true); break;
                case "remotes": remotes = ["https://github.com/other/repo"]; break;
            }
            var changed = CreateService();

            Assert.IsNull(await changed.LoadFromCacheAsync(options: options));
            Assert.IsNull(changed.CurrentDashboard);
        }

        [TestMethod]
        public async Task Cache_RejectsLegacyUnpartitionedSnapshotAsync()
        {
            new DevHubCacheService(_folder).WriteDashboard(new DevHubDashboard { Issues = [Issue("previous account")] });
            Assert.IsNull(await Service(new Provider("GitHub")).LoadFromCacheAsync());
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Refresh_ContextChangedDuringRequestDiscardsAndDoesNotPersistResultAsync(bool credentialChanged)
        {
            var provider = new Provider("GitHub");
            var query = "first";
            var generation = 0;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<DevHubIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Issues = _ => { started.TrySetResult(true); return release.Task; };
            var service = new DevHubService(new DevHubCacheService(_folder), [provider],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => query, () => generation);
            var operation = service.RefreshAsync(CancellationToken.None);
            await started.Task;
            if (credentialChanged)
                generation++;
            else
                query = "changed";
            release.TrySetResult([Issue("old context")]);

            var dashboard = await operation;

            Assert.IsEmpty(dashboard.Issues);
            Assert.AreEqual(DevHubActivityStatus.Unauthorized, dashboard.Status);
            Assert.IsNull(await new DevHubCacheService(_folder).ReadDashboardAsync());
        }

        [TestMethod]
        public async Task Scope_FailureRetainsOnlyVerifiedSameAccountAndOptionsAsync()
        {
            var provider = new Provider("GitHub")
            {
                Detail = _ => Task.FromResult(new DevHubRepoDetail { RepoIdentifier = _repo, Issues = [Issue("scoped")] }),
            };
            var service = Service(provider);
            var first = await service.ScopeToRepoAsync(_repo, CancellationToken.None);
            Assert.AreEqual("scoped", first.Issues.Single().Title);
            provider.Detail = _ => Task.FromException<DevHubRepoDetail>(new HttpRequestException("Offline"));

            var failed = await service.ScopeToRepoAsync(_repo, CancellationToken.None);
            Assert.AreEqual("scoped", failed.Issues.Single().Title);
            Assert.AreEqual(DevHubActivityStatus.Partial, failed.Status);
            StringAssert.Contains(failed.FilterByRepo(_repo).ActivityWarning, "stale");
            var changedOptions = await service.ScopeToRepoAsync(_repo, CancellationToken.None, new DevHubActivityOptions(120));
            Assert.IsEmpty(changedOptions.Issues);
            provider.Username = "other";
            var changedUser = await service.ScopeToRepoAsync(_repo, CancellationToken.None);
            Assert.IsEmpty(changedUser.Issues);
            Assert.IsNull(service.CurrentDashboard);
        }

        [TestMethod]
        public async Task Scope_AuthenticationFailureCannotReusePreviousIdentityAsync()
        {
            var provider = new Provider("GitHub")
            {
                Detail = _ => Task.FromResult(new DevHubRepoDetail { RepoIdentifier = _repo, Issues = [Issue("scoped")] }),
            };
            var service = Service(provider);
            await service.ScopeToRepoAsync(_repo, CancellationToken.None);
            provider.Authenticated = false;

            var dashboard = await service.ScopeToRepoAsync(_repo, CancellationToken.None);

            Assert.IsEmpty(dashboard.Issues);
            Assert.IsFalse(dashboard.HasAuthentication);
            Assert.AreEqual(DevHubActivityStatus.Unauthorized, dashboard.Status);
        }

        [TestMethod]
        [DataRow(401, DevHubActivityStatus.Unauthorized)]
        [DataRow(503, DevHubActivityStatus.Unavailable)]
        public async Task AzureDevOps_TypedRepositoryFailureNeverLooksEmptyAndSuccessfulAsync(int status, DevHubActivityStatus expected)
        {
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) => new HttpResponseMessage((HttpStatusCode)status)));
            var provider = new AzureDevOpsDevHubProvider(client, (_, _, _) => Task.FromResult(new DevHubCredential("account", "test-token")), (_, _) => { });

            var result = await provider.FetchActivityAsync(DevHubActivityKind.Repository, new DevHubActivityOptions(),
                new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project"), CancellationToken.None);

            Assert.AreEqual(expected, result.Status);
            Assert.IsEmpty(result.PullRequests);
            Assert.IsEmpty(result.Issues);
        }

        [TestMethod]
        public async Task Bitbucket_IdentityReuseTracksCredentialAndInvalidationAsync()
        {
            var generation = 0;
            var now = DateTimeOffset.UtcNow;
            var calls = 0;
            var credential = new DevHubCredential("first", "test-first");
            using var client = new HttpClient(new TestHttpMessageHandler((_, _) =>
            {
                calls++;
                return Json("{\"username\":\"" + credential.Username + "\"}");
            }));
            var cache = new DevHubIdentityCache(() => now, () => generation);
            var provider = new BitbucketDevHubProvider(client, _ => Task.FromResult(credential), cache);

            Assert.AreEqual("first", (await provider.GetCredentialAndUsernameAsync(CancellationToken.None)).username);
            Assert.AreEqual("first", (await provider.GetCredentialAndUsernameAsync(CancellationToken.None)).username);
            Assert.AreEqual(1, calls);
            credential = new DevHubCredential("second", "test-second");
            Assert.AreEqual("second", (await provider.GetCredentialAndUsernameAsync(CancellationToken.None)).username);
            Assert.AreEqual(2, calls);
            generation++;
            Assert.AreEqual("second", (await provider.GetCredentialAndUsernameAsync(CancellationToken.None)).username);
            Assert.AreEqual(3, calls);
            now = now.AddMinutes(5);
            Assert.AreEqual("second", (await provider.GetCredentialAndUsernameAsync(CancellationToken.None)).username);
            Assert.AreEqual(4, calls);
        }

        [TestMethod]
        public async Task IdentityCache_ExpiredCredentialDoesNotReuseOrSendIdentityAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var credential = new DevHubCredential("test-token", now.AddSeconds(1));
            var cache = new DevHubIdentityCache(() => now, () => 0);
            cache.Remember(credential, "https://api.github.com/user", "previous");
            var calls = 0;
            Task<HttpResponseMessage> SendAsync(DevHubCredential _, string url, CancellationToken token)
            {
                calls++;
                return Task.FromResult(Json("{\"login\":\"unexpected\"}"));
            }
            Assert.AreEqual("previous", await cache.GetAsync(credential, "https://api.github.com/user", SendAsync, true, CancellationToken.None));
            now = now.AddSeconds(1);

            Assert.IsNull(await cache.GetAsync(credential, "https://api.github.com/user", SendAsync, true, CancellationToken.None));
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public async Task Request_CallerCancellationIsNotAnUnavailableResultAsync()
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => DevHubActivityRequest.RunAsync(
                () => Task.FromCanceled<IReadOnlyList<DevHubIssue>>(canceled.Token), items => items.Count > 0, canceled.Token));
        }

        [TestMethod]
        public async Task Bitbucket_RepositoryFailureRetainsOnlyFailedCategoryAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
                request.RequestUri!.AbsolutePath.EndsWith("/issues") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json(request.RequestUri.AbsolutePath.EndsWith("/user") ? "{\"username\":\"account\"}" : "{\"values\":[]}")));
            var provider = new BitbucketDevHubProvider(client, _ => Task.FromResult(new DevHubCredential("account", "test-token")));
            var repo = new RemoteRepoIdentifier("bitbucket.org", "owner", "repo");
            var fresh = await provider.FetchActivityAsync(DevHubActivityKind.Repository, new DevHubActivityOptions(), repo, CancellationToken.None);
            Assert.AreEqual(DevHubActivityStatus.Partial, fresh.Status);
            Assert.AreEqual(DevHubActivityStatus.Success, fresh.PullRequestsStatus);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, fresh.IssuesStatus);
            Assert.AreEqual(DevHubActivityStatus.Success, fresh.CiRunsStatus);

            var retained = DevHubService.RetainFailedSource(fresh, new DevHubActivitySnapshot
            {
                RepoIdentifier = repo,
                PullRequests = [PullRequest("old PR")], Issues = [Issue("old issue")], CiRuns = [new DevHubCiRun { Name = "old build" }],
            });

            Assert.IsEmpty(retained.PullRequests);
            Assert.IsEmpty(retained.CiRuns);
            Assert.AreEqual("old issue", retained.Issues.Single().Title);
        }

        [TestMethod]
        public async Task GitHub_RepositoryCategoryOutcomesDistinguishFailureFromEmptyAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
                request.RequestUri!.AbsolutePath.EndsWith("/issues") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json(request.RequestUri.AbsolutePath.EndsWith("/runs") ? "{\"workflow_runs\":[]}" : "[]")));

            var detail = await GitHubDevHubProvider.GetRepoDetailAsync(client, new DevHubCredential("account", "test-token"),
                null, _repo, new DevHubActivityOptions(), CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Partial, detail.Status);
            Assert.AreEqual(DevHubActivityStatus.Success, detail.PullRequestsStatus);
            Assert.AreEqual(DevHubActivityStatus.Unavailable, detail.IssuesStatus);
            Assert.AreEqual(DevHubActivityStatus.Success, detail.CiRunsStatus);
        }

        [TestMethod]
        public async Task GitHub_ConcurrentCiRepositoryFailurePreservesExplicitPartialOutcomeAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
                request.RequestUri!.AbsolutePath == "/user/repos" ? Json("[{\"full_name\":\"owner/first\"},{\"full_name\":\"owner/second\"}]")
                    : request.RequestUri.AbsolutePath.Contains("/second/") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json("{\"workflow_runs\":[{\"name\":\"Build\",\"status\":\"completed\",\"updated_at\":\"2026-09-01T00:00:00Z\",\"html_url\":\"https://example.com/run\"}]}")));

            var result = await DevHubActivityRequest.RunAsync(() => new GitHubDevHubProvider().GetUserCiRunsAsync(client,
                new DevHubCredential("account", "test-token"), CancellationToken.None), items => items.Count > 0, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Partial, result.Status);
            Assert.AreEqual("first", result.Value.Single().RepoIdentifier.Repo);
        }

        [TestMethod]
        public async Task IdentityCache_InFlightInvalidationCannotReturnOldIdentityAsync()
        {
            var generation = 0;
            var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = new DevHubIdentityCache(credentialGeneration: () => generation);
            var lookup = cache.GetAsync(new DevHubCredential("account", "test-token"), "https://api.github.com/user",
                (_, _, _) => release.Task, true, CancellationToken.None);
            generation++;
            release.TrySetResult(Json("{\"login\":\"previous\"}"));

            Assert.IsNull(await lookup);
            Assert.AreEqual("current", await cache.GetAsync(new DevHubCredential("account", "test-token"), "https://api.github.com/user",
                (_, _, _) => Task.FromResult(Json("{\"login\":\"current\"}")), true, CancellationToken.None));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CurrentDashboard_ImmediatelyRejectsChangedCredentialsOrQueryAsync(bool credentialChanged)
        {
            var generation = 0;
            var query = "first";
            var provider = new Provider("GitHub") { Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("old context")]) };
            var service = new DevHubService(new DevHubCacheService(_folder), [provider], _ => Task.FromResult<IReadOnlyList<string>>([]),
                () => DevHubSortOrder.MostRecent, () => query, () => generation);
            await service.RefreshAsync(CancellationToken.None);
            Assert.IsNotNull(service.CurrentDashboard);

            if (credentialChanged)
                generation++;
            else
                query = "changed";

            Assert.IsNull(service.CurrentDashboard);
            Assert.IsTrue(service.IsCacheStale());
        }

        [TestMethod]
        public async Task Scope_DifferentAuthenticatedHostIsUnauthorizedNotSuccessfulEmptyAsync()
        {
            var dashboard = await Service(new Provider("provider")).ScopeToRepoAsync(
                new RemoteRepoIdentifier("unverified.example.com", "owner", "repo"), CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Unauthorized, dashboard.Status);
            Assert.IsFalse(dashboard.HasData);
            Assert.IsNotEmpty(dashboard.ActivityWarnings);
        }

        [TestMethod]
        public async Task Bitbucket_MalformedCollectionIsUnavailableNotSuccessfulEmptyAsync()
        {
            using var client = new HttpClient(new TestHttpMessageHandler((request, _) =>
                Json(request.RequestUri!.AbsolutePath.EndsWith("/user") ? "{\"username\":\"account\"}" : "{}")));
            var provider = new BitbucketDevHubProvider(client, _ => Task.FromResult(new DevHubCredential("account", "test-token")));

            var result = await provider.FetchActivityAsync(DevHubActivityKind.PullRequests, new DevHubActivityOptions(), null, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Unavailable, result.Status);
            Assert.IsEmpty(result.PullRequests);
        }

        [TestMethod]
        public async Task Refresh_PublishedSnapshotsDoNotMutateWhileLaterSourcesCompleteAsync()
        {
            var provider = new Provider("GitHub") { Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("fresh")]) };
            var snapshots = new List<DevHubDashboard>();
            var service = Service(provider);

            var final = await service.RefreshAsync(CancellationToken.None, new InlineProgress(snapshots.Add));

            Assert.IsEmpty(snapshots.First().PullRequests);
            Assert.IsEmpty(snapshots.First().Sources);
            Assert.IsTrue(snapshots.First().IsRefreshing);
            Assert.AreEqual("fresh", final.PullRequests.Single().Title);
            Assert.IsFalse(final.IsRefreshing);
            Assert.AreSame(final, service.CurrentDashboard);
            Assert.AreNotSame(snapshots.First(), final);
        }

        private DevHubIssue Issue(string title) => new DevHubIssue { Title = title, WebUrl = "https://github.com/owner/repo/issues/" + title, RepoIdentifier = _repo };
        private DevHubPullRequest PullRequest(string title) => new DevHubPullRequest
        {
            Title = title, WebUrl = "https://github.com/owner/repo/pull/" + title, RepoIdentifier = _repo, IsAuthoredByCurrentUser = true,
        };

        private DevHubService Service(params Provider[] providers) => new DevHubService(new DevHubCacheService(_folder), providers,
            _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
        private static HttpResponseMessage Json(string json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };

        private sealed class InlineProgress(Action<DevHubDashboard> report) : IProgress<DevHubDashboard>
        {
            public void Report(DevHubDashboard value) => report(value);
        }

        private sealed class Provider(string name) : IDevHubProvider
        {
            public string DisplayName => name;
            public string Username { get; set; } = "account";
            public string IdentityId { get; set; } = "identity";
            public bool Authenticated { get; set; } = true;
            public Func<CancellationToken, Task<IReadOnlyList<DevHubIssue>>> Issues { get; set; } = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([]);
            public Func<CancellationToken, Task<IReadOnlyList<DevHubPullRequest>>> Pulls { get; set; } = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([]);
            public Func<CancellationToken, Task<DevHubRepoDetail>> Detail { get; set; } = _ => Task.FromResult(new DevHubRepoDetail());
            public bool CanHandle(string remoteUrl) => true;
            public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl) => RemoteRepoIdentifier.TryParse(remoteUrl);
            public Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken) => Task.FromResult(User());
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<DevHubUser>>(Authenticated ? [User()] : []);
            private DevHubUser User() => new DevHubUser { Username = Username, IdentityId = IdentityId, Host = "github.com", ProviderName = name };
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken) => Issues(cancellationToken);
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken) => Pulls(cancellationToken);
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DevHubCiRun>>([]);
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<RemoteRepoIdentifier>>([]);
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken) => Detail(cancellationToken);
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
        }
    }
}