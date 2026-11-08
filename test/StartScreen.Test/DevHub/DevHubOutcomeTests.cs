using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.Threading;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;
using StartScreen.ToolWindows;

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
        public async Task Cache_ReopenedServiceDisplaysActivityWhileOnlineRequestsArePendingAsync()
        {
            var provider = new Provider("GitHub")
            {
                Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("cached issue")]),
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("cached pull")]),
                Builds = _ => Task.FromResult<IReadOnlyList<DevHubCiRun>>([new DevHubCiRun { Name = "cached build", WebUrl = "https://example.com/build/1" }]),
            };
            await Service(provider).RefreshAsync(CancellationToken.None);
            var reopened = Service(provider);
            var cached = await reopened.LoadFromCacheAsync();
            Assert.IsNotNull(cached);
            Assert.AreEqual("cached pull", cached.PullRequests.Single().Title);
            Assert.AreEqual("cached issue", cached.Issues.Single().Title);
            Assert.AreEqual("cached build", cached.CiRuns.Single().Name);

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = new TaskCompletionSource<DevHubDashboard>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubPullRequest>>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Pulls = _ => { started.TrySetResult(true); return pending.Task; };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = reopened.RefreshAsync(timeout.Token, new InlineProgress(dashboard =>
            {
                if (dashboard.IsRefreshing)
                    published.TrySetResult(dashboard);
            }));
            try
            {
                await Task.WhenAll(started.Task, published.Task).WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsNotNull(reopened.CurrentDashboard);
                Assert.AreEqual("cached pull", reopened.CurrentDashboard.PullRequests.Single().Title);
                Assert.AreEqual("cached issue", reopened.CurrentDashboard.Issues.Single().Title);
                Assert.AreEqual("cached build", reopened.CurrentDashboard.CiRuns.Single().Name);
                Assert.IsTrue(reopened.CurrentDashboard.IsRefreshing);
            }
            finally
            {
                pending.TrySetResult([PullRequest("latest pull")]);
                await refresh;
            }
            var latest = await refresh;
            Assert.AreEqual("latest pull", latest.PullRequests.Single().Title);
            Assert.IsFalse(latest.IsRefreshing);
            var restoredAgain = await reopened.LoadFromCacheAsync();
            Assert.IsNotNull(restoredAgain);
            Assert.AreEqual("latest pull", restoredAgain.PullRequests.Single().Title);
        }

        [TestMethod]
        [DataRow("matching repositories")]
        [DataRow("changed repositories")]
        [DataRow("discovery failure")]
        public async Task Cache_AccountVerificationOverlapsDiscoveryWithoutPublishingUnvalidatedTeamPrsAsync(string discoveryResult)
        {
            var teamPull = PullRequest("cached team pull");
            teamPull.IsAuthoredByCurrentUser = false;
            teamPull.Status = "open";
            var provider = new Provider("GitHub")
            {
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([teamPull]),
            };
            await Service(provider).RefreshAsync(CancellationToken.None);
            var users = await provider.GetAuthenticatedUsersAsync(CancellationToken.None);
            var authenticationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var verified = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var authenticationCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var discoveryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var discovery = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var authentications = 0;
            var discoveries = 0;
            provider.Authenticate = async token =>
            {
                Interlocked.Increment(ref authentications);
                authenticationStarted.TrySetResult(true);
                var result = await verified.Task.WithCancellation(token);
                authenticationCompleted.TrySetResult(true);
                return result;
            };
            var activityCalls = provider.ActivityCalls;
            var service = new DevHubService(new DevHubCacheService(_folder), [provider], async token =>
            {
                discoveries++;
                discoveryStarted.TrySetResult(true);
                var urls = await discovery.Task.WithCancellation(token);
                if (discoveryResult == "discovery failure")
                    DevHubActivityRequest.Failed();
                return urls;
            }, () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
            var snapshots = new ConcurrentQueue<DevHubDashboard>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var load = service.LoadFromCacheAsync(timeout.Token, progress: new InlineProgress(snapshots.Enqueue));
            try
            {
                await Task.WhenAll(authenticationStarted.Task, discoveryStarted.Task).WithCancellation(timeout.Token);
                Assert.IsFalse(discovery.Task.IsCompleted, "Account verification must not wait for repository discovery.");
                verified.SetResult(users);
                await authenticationCompleted.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(load.IsCompleted);
                Assert.IsNull(service.CurrentDashboard);
                Assert.IsEmpty(snapshots, "A verified account alone must not expose activity from a different repository context.");
            }
            finally
            {
                verified.TrySetResult(users);
                discovery.TrySetResult(discoveryResult == "changed repositories" ? ["https://github.com/other/repo"] : []);
            }
            var dashboard = await load;
            if (discoveryResult == "matching repositories")
            {
                Assert.IsNotNull(dashboard);
                Assert.AreEqual("cached team pull", dashboard.PullRequests.Single().Title);
                Assert.HasCount(1, DevHubWorkItem.GetIncoming(dashboard.PullRequests, [], []));
                Assert.HasCount(1, snapshots);
            }
            else
            {
                Assert.IsNull(dashboard);
                Assert.IsNull(service.CurrentDashboard);
                Assert.IsEmpty(snapshots);
            }
            Assert.AreEqual(1, authentications);
            Assert.AreEqual(1, discoveries);
            Assert.AreEqual(activityCalls, provider.ActivityCalls, "Painting cached PRs must not fetch fresh categories.");
        }

        [TestMethod]
        public async Task Cache_ContextMismatchCancelsEarlyAccountVerificationAsync()
        {
            var provider = new Provider("GitHub");
            await Service(provider).RefreshAsync(CancellationToken.None);
            var authenticationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var discovery = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var authenticationToken = CancellationToken.None;
            provider.Authenticate = token =>
            {
                authenticationToken = token;
                authenticationStarted.TrySetResult(true);
                return pending.Task.WithCancellation(token);
            };
            var service = new DevHubService(new DevHubCacheService(_folder), [provider], token => discovery.Task.WithCancellation(token),
                () => DevHubSortOrder.MostRecent, () => string.Empty, () => 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var load = service.LoadFromCacheAsync(timeout.Token);
            await authenticationStarted.Task.WithCancellation(timeout.Token);

            discovery.SetResult(["https://github.com/other/repo"]);
            Assert.IsNull(await load.WithCancellation(timeout.Token));
            Assert.IsTrue(authenticationToken.IsCancellationRequested);
            Assert.IsNull(service.CurrentDashboard);
            Assert.IsFalse(pending.Task.IsCompleted, "Rejecting the cache must cancel rather than wait for the account endpoint.");
        }

        [TestMethod]
        public async Task Cache_SynchronousCredentialAcquisitionCannotBlockAnotherProvidersTeamPrsAsync()
        {
            var teamPull = PullRequest("cached GitHub team pull");
            teamPull.IsAuthoredByCurrentUser = false;
            teamPull.Status = "open";
            var github = new Provider("GitHub")
            {
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([teamPull]),
            };
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com" };
            await Service(ado, github).RefreshAsync(CancellationToken.None);
            var users = await ado.GetAuthenticatedUsersAsync(CancellationToken.None);
            var credentialStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new TaskCompletionSource<DevHubDashboard>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCredential = new ManualResetEventSlim();
            ado.Authenticate = token =>
            {
                credentialStarted.TrySetResult(true);
                releaseCredential.Wait(token);
                return Task.FromResult(users);
            };
            var service = Service(ado, github);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var load = service.LoadFromCacheAsync(timeout.Token, progress: new InlineProgress(dashboard =>
            {
                if (dashboard.PullRequests.Any(pr => pr.Title == teamPull.Title))
                    displayed.TrySetResult(dashboard);
            }));
            try
            {
                await credentialStarted.Task.WithCancellation(timeout.Token);
                var dashboard = await displayed.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(load.IsCompleted, "The slow provider is still synchronously acquiring credentials.");
                Assert.AreEqual("cached GitHub team pull", dashboard.PullRequests.Single().Title);
                Assert.HasCount(1, dashboard.Users);
                Assert.IsTrue(dashboard.IsRefreshing);
            }
            finally
            {
                releaseCredential.Set();
                await load;
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Cache_TeamActivityIsPublishedAfterItsProviderIsVerifiedWithoutWaitingForOtherProvidersAsync(bool duringStartup)
        {
            var teamPull = PullRequest("cached team pull");
            teamPull.IsAuthoredByCurrentUser = false;
            teamPull.Status = "open";
            var github = new Provider("GitHub")
            {
                ActivityRepositories = [_repo],
                Detail = _ => Task.FromResult(new DevHubRepoDetail
                {
                    RepoIdentifier = _repo,
                    PullRequests = [teamPull],
                    Issues = [Issue("cached team issue")],
                    CiRuns = [new DevHubCiRun { Name = "cached team build", RepoIdentifier = _repo }],
                }),
            };
            var ado = new Provider("Azure DevOps")
            {
                Host = "dev.azure.com",
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("cached ADO pull")]),
            };
            await Service(github, ado).RefreshAsync(CancellationToken.None);
            var githubUsers = await github.GetAuthenticatedUsersAsync(CancellationToken.None);
            var adoUsers = await ado.GetAuthenticatedUsersAsync(CancellationToken.None);
            var githubVerified = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var adoVerified = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var authentications = 0;
            github.Authenticate = token =>
            {
                if (Interlocked.Increment(ref authentications) == 2)
                    started.TrySetResult(true);
                return githubVerified.Task.WithCancellation(token);
            };
            ado.Authenticate = token =>
            {
                if (Interlocked.Increment(ref authentications) == 2)
                    started.TrySetResult(true);
                return adoVerified.Task.WithCancellation(token);
            };
            var activityCalls = github.ActivityCalls + ado.ActivityCalls;
            var reopened = Service(github, ado);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var snapshots = new List<DevHubDashboard>();
            var progress = new InlineProgress(value =>
            {
                snapshots.Add(value);
                if (value.PullRequests.Any(pr => pr.Title == teamPull.Title))
                    displayed.TrySetResult(true);
            });
            using var context = new JoinableTaskContext();
            using var startup = duringStartup
                ? new DevHubStartupLoad(reopened, context.Factory, timeout.Token, new BackgroundProgress(), progress)
                : null;
            var load = startup != null ? startup.LoadAsync(timeout.Token,
                () => throw new AssertFailedException("The control must reuse the factory's cache load."))
                : reopened.LoadFromCacheAsync(timeout.Token, progress: progress);
            try
            {
                await started.Task.WithCancellation(timeout.Token);
                Assert.IsNull(reopened.CurrentDashboard, "No account's cached activity is visible before identity verification.");
                Assert.IsEmpty(snapshots);
                githubVerified.SetResult(githubUsers);
                await displayed.Task.WithCancellation(timeout.Token);

                Assert.IsFalse(load.IsCompleted, "Azure DevOps is still authenticating.");
                var visible = reopened.CurrentDashboard;
                Assert.IsNotNull(visible);
                Assert.AreEqual("cached team pull", visible.PullRequests.Single().Title);
                Assert.AreEqual("cached team issue", visible.Issues.Single().Title);
                Assert.AreEqual("cached team build", visible.CiRuns.Single().Name);
                Assert.HasCount(1, DevHubWorkItem.GetIncoming(visible.PullRequests, [], []));
                Assert.IsTrue(visible.IsRefreshing);
                Assert.IsTrue(visible.Sources.Keys.All(key => key.StartsWith("GitHub/", StringComparison.Ordinal)));
                Assert.AreEqual(activityCalls, github.ActivityCalls + ado.ActivityCalls, "Cache display must not fetch online activity.");
            }
            finally
            {
                githubVerified.TrySetResult(githubUsers);
                adoVerified.TrySetResult(adoUsers);
            }
            var final = await load;
            Assert.IsNotNull(final);
            Assert.IsFalse(final.IsRefreshing);
            Assert.HasCount(2, final.PullRequests);
            Assert.IsFalse(reopened.IsCacheStale());
        }

        [TestMethod]
        [DataRow(0, false)]
        [DataRow(2, false)]
        [DataRow(3, true)]
        [DataRow(5, true)]
        public async Task Startup_CacheVerificationStartsBeforeControlConsumesItAndDoesNotFetchFreshActivityAsync(int ageHours, bool stale)
        {
            var provider = new Provider("GitHub")
            {
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("cached pull")]),
            };
            await Service(provider).RefreshAsync(CancellationToken.None);
            File.SetLastWriteTimeUtc(Path.Combine(_folder, "dashboard.json"), DateTime.UtcNow.AddHours(-ageHours));
            var users = await provider.GetAuthenticatedUsersAsync(CancellationToken.None);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var verified = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var authentications = 0;
            provider.Authenticate = token =>
            {
                Interlocked.Increment(ref authentications);
                started.TrySetResult(true);
                return verified.Task.WithCancellation(token);
            };
            var activityCalls = provider.ActivityCalls;
            var service = Service(provider);
            var progress = new BackgroundProgress();
            using var context = new JoinableTaskContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var displayed = new TaskCompletionSource<DevHubDashboard>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var startup = new DevHubStartupLoad(service, context.Factory, timeout.Token, progress,
                new InlineProgress(value => displayed.TrySetResult(value)));

            await started.Task.WithCancellation(timeout.Token);
            Assert.IsFalse(startup.LoadTask.IsCompleted, "Constructing startup state must not wait for network verification.");
            Assert.IsNull(service.CurrentDashboard, "Unverified disk data must not be exposed.");
            Assert.IsTrue(progress.IsBusy);

            verified.SetResult(users);
            var cached = await displayed.Task.WithCancellation(timeout.Token);
            var result = await startup.LoadAsync(timeout.Token,
                () => throw new AssertFailedException("The initial load must not read or authenticate twice."));

            Assert.AreSame(service, startup.Service);
            Assert.AreSame(cached, result);
            Assert.AreSame(result, service.CurrentDashboard);
            Assert.AreEqual("cached pull", result.PullRequests.Single().Title);
            Assert.AreEqual(1, authentications);
            Assert.AreEqual(activityCalls, provider.ActivityCalls);
            Assert.AreEqual(stale, service.IsCacheStale());
            Assert.IsFalse(progress.IsBusy);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Startup_CompletedLoadCannotRestoreAnAccountOrQueryChangedBeforeControlCreationAsync(bool credentialChanged)
        {
            var generation = 0;
            var query = "first";
            var provider = new Provider("GitHub")
            {
                Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("old context")]),
            };
            var service = new DevHubService(new DevHubCacheService(_folder), [provider], _ => Task.FromResult<IReadOnlyList<string>>([]),
                () => DevHubSortOrder.MostRecent, () => query, () => generation);
            await service.RefreshAsync(CancellationToken.None);
            using var context = new JoinableTaskContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var startup = new DevHubStartupLoad(service, context.Factory, timeout.Token, new BackgroundProgress(),
                new InlineProgress(_ => { }));
            Assert.IsNotNull(await startup.LoadTask.JoinAsync(timeout.Token));

            if (credentialChanged)
                generation++;
            else
                query = "changed";

            var result = await startup.LoadAsync(timeout.Token,
                () => throw new AssertFailedException("Invalidated startup data must be left for the normal refresh path."));

            Assert.IsNull(result);
            Assert.IsNull(service.CurrentDashboard);
        }

        [TestMethod]
        public async Task Startup_UnloadCancelsVerificationAndReopeningRetriesCacheAsync()
        {
            var provider = new Provider("GitHub")
            {
                Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("cached issue")]),
            };
            await Service(provider).RefreshAsync(CancellationToken.None);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Authenticate = token =>
            {
                started.TrySetResult(true);
                return pending.Task.WithCancellation(token);
            };
            var service = Service(provider);
            var progress = new BackgroundProgress();
            using var context = new JoinableTaskContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var startup = new DevHubStartupLoad(service, context.Factory, timeout.Token, progress,
                new InlineProgress(_ => Assert.Fail("Canceled verification must not publish cached activity.")));
            await started.Task.WithCancellation(timeout.Token);

            startup.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => startup.LoadTask.JoinAsync(timeout.Token));
            Assert.IsNull(service.CurrentDashboard);
            Assert.IsFalse(progress.IsBusy);

            provider.Authenticate = null;
            var reloads = 0;
            Task<DevHubDashboard> ReloadAsync()
            {
                reloads++;
                return service.LoadFromCacheAsync(timeout.Token);
            }
            var restored = await startup.LoadAsync(timeout.Token, ReloadAsync);
            Assert.AreEqual("cached issue", restored.Issues.Single().Title);
            Assert.AreEqual(1, reloads);

            await startup.LoadAsync(timeout.Token, ReloadAsync);
            Assert.AreEqual(2, reloads, "Later activations must not reuse the original startup snapshot.");
        }

        [TestMethod]
        public async Task Startup_ShutdownCancelsVerificationWithoutRetryingAsync()
        {
            var provider = new Provider("GitHub");
            await Service(provider).RefreshAsync(CancellationToken.None);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Authenticate = token =>
            {
                started.TrySetResult(true);
                return pending.Task.WithCancellation(token);
            };
            using var context = new JoinableTaskContext();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var service = Service(provider);
            using var startup = new DevHubStartupLoad(service, context.Factory, shutdown.Token, new BackgroundProgress(),
                new InlineProgress(_ => Assert.Fail("Shutdown must not publish cached activity.")));
            await started.Task.WithCancellation(shutdown.Token);
            shutdown.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => startup.LoadAsync(shutdown.Token,
                () => throw new AssertFailedException("Shutdown must not retry the cache load.")));
            await Assert.ThrowsAsync<OperationCanceledException>(() => startup.LoadTask.JoinAsync());
            Assert.IsNull(service.CurrentDashboard);
        }

        [TestMethod]
        [DataRow("signed out")]
        [DataRow("changed account")]
        [DataRow("changed identity")]
        [DataRow("disabled")]
        public async Task Cache_AccountChangesOnlyRemoveTheAffectedProvidersDataAndKeepTeamActivityDuringRefreshAsync(string change)
        {
            var teamPull = PullRequest("cached team pull");
            teamPull.IsAuthoredByCurrentUser = false;
            teamPull.Status = "open";
            var github = new Provider("GitHub")
            {
                ActivityRepositories = [_repo],
                Detail = _ => Task.FromResult(new DevHubRepoDetail { RepoIdentifier = _repo, PullRequests = [teamPull] }),
            };
            var ado = new Provider("Azure DevOps")
            {
                Host = "dev.azure.com",
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("previous ADO account")]),
            };
            await Service(github, ado).RefreshAsync(CancellationToken.None);
            switch (change)
            {
                case "signed out": ado.Authenticated = false; break;
                case "changed account": ado.Username = "new account"; break;
                case "changed identity": ado.IdentityId = "new identity"; break;
            }
            ado.Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("current ADO account")]);
            var reopened = Service(change == "disabled" ? [github] : [github, ado]);
            var cached = await reopened.LoadFromCacheAsync();
            Assert.IsNotNull(cached);
            Assert.AreEqual("cached team pull", cached.PullRequests.Single().Title);
            Assert.IsTrue(cached.Sources.Keys.All(key => key.StartsWith("GitHub/", StringComparison.Ordinal)));
            Assert.IsTrue(reopened.IsCacheStale(), "A partial cache must still request current activity.");

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var repository = new TaskCompletionSource<DevHubRepoDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
            github.Detail = token => { started.TrySetResult(true); return repository.Task.WithCancellation(token); };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = reopened.RefreshAsync(timeout.Token);
            try
            {
                await started.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsTrue(reopened.CurrentDashboard.IsRefreshing);
                Assert.IsTrue(reopened.CurrentDashboard.PullRequests.Any(pr => pr.Title == "cached team pull"),
                    "Starting online refresh must not clear the verified team cache when another account changed.");
                Assert.IsFalse(reopened.CurrentDashboard.PullRequests.Any(pr => pr.Title == "previous ADO account"));
            }
            finally
            {
                repository.TrySetResult(new DevHubRepoDetail { RepoIdentifier = _repo, PullRequests = [teamPull] });
            }
            await refresh;
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Cache_ContextChangesDuringProviderVerificationClearAlreadyPublishedActivityAsync(bool credentialChanged)
        {
            var github = new Provider("GitHub") { Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("cached")]) };
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com" };
            var query = "first";
            var generation = 0;
            DevHubService CreateService() => new(new DevHubCacheService(_folder), [github, ado],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => query, () => generation);
            await CreateService().RefreshAsync(CancellationToken.None);
            var adoUsers = await ado.GetAuthenticatedUsersAsync(CancellationToken.None);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            ado.Authenticate = token => pending.Task.WithCancellation(token);
            var displayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleared = false;
            var reopened = CreateService();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var load = reopened.LoadFromCacheAsync(timeout.Token, progress: new InlineProgress(value =>
            {
                cleared |= value == null;
                if (value?.HasData == true)
                    displayed.TrySetResult(true);
            }));
            try
            {
                await displayed.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(load.IsCompleted);
                if (credentialChanged)
                    generation++;
                else
                    query = "changed";
            }
            finally
            {
                pending.TrySetResult(adoUsers);
            }
            Assert.IsNull(await load);
            Assert.IsNull(reopened.CurrentDashboard);
            Assert.IsTrue(cleared, "The UI must be told to clear a cache whose context changed.");
        }

        [TestMethod]
        [DoNotParallelize]
        public async Task Cache_BitbucketUnauthorizedDoesNotDiscardOtherVerifiedAccountsAsync()
        {
            var github = new Provider("GitHub")
            {
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("cached pull")]),
                Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("cached issue")]),
                Builds = _ => Task.FromResult<IReadOnlyList<DevHubCiRun>>([new DevHubCiRun { Name = "cached build", WebUrl = "https://example.com/build" }]),
            };
            var bitbucket = new Provider("Bitbucket") { Authenticated = false };
            DevHubService CreateService() => new(new DevHubCacheService(_folder), [github, bitbucket],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => string.Empty);
            await CreateService().RefreshAsync(CancellationToken.None);
            bitbucket.OnAuthenticate = () =>
            {
                DevHubCredentialHelper.InvalidateCachedCredential("bitbucket.org");
                DevHubActivityRequest.Failed(unauthorized: true);
            };

            var reopened = CreateService();
            var cached = await reopened.LoadFromCacheAsync();

            Assert.IsNotNull(cached);
            Assert.IsTrue(cached.HasAuthentication);
            Assert.AreEqual("cached pull", cached.PullRequests.Single().Title);
            Assert.AreEqual("cached issue", cached.Issues.Single().Title);
            Assert.AreEqual("cached build", cached.CiRuns.Single().Name);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var refreshed = await reopened.RefreshAsync(CancellationToken.None, forceRefresh: true);
                Assert.IsTrue(refreshed.HasAuthentication);
                Assert.AreEqual(DevHubActivityStatus.Partial, refreshed.Status);
                Assert.AreEqual("cached pull", refreshed.PullRequests.Single().Title);
                Assert.AreEqual("cached issue", refreshed.Issues.Single().Title);
                Assert.AreEqual("cached build", refreshed.CiRuns.Single().Name);
                Assert.AreEqual(DevHubActivityStatus.Unauthorized, refreshed.Sources["Bitbucket/authentication"].Status);
                Assert.IsFalse(refreshed.ActivityWarnings.ContainsKey("authentication"));
                Assert.IsEmpty(refreshed.GetVisibleActivityWarnings(), "Bitbucket errors belong in Output, not the activity banner.");
                Assert.AreSame(refreshed, reopened.CurrentDashboard);
            }
        }

        [TestMethod]
        [DataRow("GitHub")]
        [DataRow("Azure DevOps")]
        [DataRow("Bitbucket")]
        public async Task ProviderSettings_DisabledProviderIsSkippedByCacheRefreshAndScopeAndCanBeReenabledAsync(string name)
        {
            var enabled = true;
            var generation = 0;
            var authentications = 0;
            var activities = 0;
            var provider = new Provider(name)
            {
                OnAuthenticate = () => authentications++,
                Pulls = _ =>
                {
                    activities++;
                    return Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("enabled pull")]);
                },
                Detail = _ =>
                {
                    activities++;
                    return Task.FromResult(new DevHubRepoDetail { RepoIdentifier = _repo });
                },
            };
            var service = new DevHubService(new DevHubCacheService(_folder), [provider],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => string.Empty,
                () => generation, getProviders: () => enabled ? [provider] : []);
            Assert.IsTrue((await service.RefreshAsync(CancellationToken.None)).HasData);

            enabled = false;
            generation++;
            authentications = activities = 0;
            Assert.IsNull(service.CurrentDashboard);
            Assert.IsNull(await service.LoadFromCacheAsync());
            var disabled = await service.RefreshAsync(CancellationToken.None, forceRefresh: true);
            Assert.IsFalse(disabled.HasAuthentication);
            Assert.IsFalse(disabled.HasData);
            Assert.IsFalse((await service.ScopeToRepoAsync(_repo, CancellationToken.None)).HasData);
            Assert.AreEqual(0, authentications);
            Assert.AreEqual(0, activities);

            enabled = true;
            generation++;
            var reenabled = await service.RefreshAsync(CancellationToken.None, forceRefresh: true);
            Assert.IsTrue(reenabled.HasAuthentication);
            Assert.AreEqual("enabled pull", reenabled.PullRequests.Single().Title);
            Assert.AreEqual(1, authentications);
            Assert.AreEqual(1, activities);
        }

        [TestMethod]
        [DataRow("Bitbucket/authentication")]
        [DataRow("Bitbucket/global/Issues")]
        [DataRow("bitbucket.org/workspace/repo")]
        public async Task Cache_BitbucketWarningsRemainDiagnosticOnlyWhileOtherWarningsStayVisibleAsync(string key)
        {
            var dashboard = new DevHubDashboard
            {
                ActivityWarnings = new Dictionary<string, string>
                {
                    [key] = "api.bitbucket.org returned HTTP 401. Refresh to retry.",
                    ["GitHub/authentication"] = "GitHub sign-in is required.",
                },
                Sources = new Dictionary<string, DevHubActivitySnapshot>
                {
                    [key] = new DevHubActivitySnapshot { Status = DevHubActivityStatus.Unauthorized },
                },
            };
            var cache = new DevHubCacheService(_folder);
            await cache.WriteDashboardAsync(dashboard);

            var restored = await cache.ReadDashboardAsync(CancellationToken.None);

            Assert.IsNotNull(restored);
            Assert.AreEqual(DevHubActivityStatus.Unauthorized, restored.Status);
            Assert.HasCount(2, restored.ActivityWarnings, "Suppressing presentation must not discard diagnostic failure data.");
            Assert.AreEqual("GitHub sign-in is required.", restored.GetVisibleActivityWarnings().Single().Value);
            Assert.IsNull(restored.FilterByRepo(new RemoteRepoIdentifier("bitbucket.org", "workspace", "repo")).ActivityWarning);
        }

        [TestMethod]
        [DoNotParallelize]
        public async Task Refresh_ProviderTokenRetryDuringActivityDoesNotDiscardConnectedDataAsync()
        {
            var provider = new Provider("GitHub")
            {
                Pulls = _ =>
                {
                    DevHubCredentialHelper.InvalidateCachedCredential("github.com");
                    return Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("refreshed pull")]);
                },
                Detail = _ =>
                {
                    DevHubCredentialHelper.InvalidateCachedCredential("github.com");
                    return Task.FromResult(new DevHubRepoDetail { RepoIdentifier = _repo, PullRequests = [PullRequest("scoped pull")] });
                },
            };
            var service = new DevHubService(new DevHubCacheService(_folder), [provider],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => string.Empty);

            var dashboard = await service.RefreshAsync(CancellationToken.None);
            var scoped = await service.ScopeToRepoAsync(_repo, CancellationToken.None);

            Assert.IsTrue(dashboard.HasAuthentication);
            Assert.AreEqual("refreshed pull", dashboard.PullRequests.Single().Title);
            Assert.IsTrue(scoped.HasAuthentication);
            Assert.AreEqual("scoped pull", scoped.PullRequests.Single().Title);
            Assert.IsNotNull(await new DevHubCacheService(_folder).ReadDashboardAsync());
        }

        [TestMethod]
        [DoNotParallelize]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Refresh_ExplicitAccountChangeStillDiscardsConnectedDataAsync(bool duringAuthentication)
        {
            var provider = new Provider("GitHub")
            {
                OnAuthenticate = duringAuthentication ? DevHubCredentialHelper.ClearCachedCredentials : null,
                Pulls = _ =>
                {
                    if (!duringAuthentication)
                        DevHubCredentialHelper.ClearCachedCredentials();
                    return Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("old account")]);
                },
            };
            var cache = new DevHubCacheService(_folder);
            var service = new DevHubService(cache, [provider],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => string.Empty);

            var dashboard = await service.RefreshAsync(CancellationToken.None);

            Assert.IsFalse(dashboard.HasAuthentication);
            Assert.IsEmpty(dashboard.PullRequests);
            Assert.AreEqual(DevHubActivityStatus.Unauthorized, dashboard.Status);
            Assert.IsNull(await cache.ReadDashboardAsync());
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

            var failed = await service.ScopeToRepoAsync(_repo, CancellationToken.None, forceRefresh: true);
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
            Assert.HasCount(2, snapshots, "A burst keeps only the first and final publication.");
        }

        [TestMethod]
        public async Task Refresh_CoalescedDataPublishesWhileAnotherSourceIsPendingAsync()
        {
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubCiRun>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new TaskCompletionSource<DevHubDashboard>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new Provider("GitHub")
            {
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("fresh")]),
                Builds = token => pending.Task.WithCancellation(token),
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var refresh = Service(provider).RefreshAsync(timeout.Token, new InlineProgress(value =>
            {
                if (value.IsRefreshing && value.PullRequests.Count > 0)
                    displayed.TrySetResult(value);
            }));
            try
            {
                var interim = await displayed.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.AreEqual("fresh", interim.PullRequests.Single().Title);
            }
            finally
            {
                pending.TrySetResult([]);
                await refresh;
            }
        }

        [TestMethod]
        public async Task WorkProgress_ReportsEachAuthenticationWhileAnotherProviderIsPendingAsync()
        {
            var github = new Provider("GitHub");
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com" };
            var users = await ado.GetAuthenticatedUsersAsync(CancellationToken.None);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var advanced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = new ConcurrentQueue<(int Completed, int Total)>();
            ado.Authenticate = token => pending.Task.WithCancellation(token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = Service(github, ado).RefreshAsync(timeout.Token, workProgress: new InlineWorkProgress(value =>
            {
                reports.Enqueue(value);
                if (value == (2, 8))
                    advanced.TrySetResult(true);
            }));
            try
            {
                await advanced.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.AreEqual(4, github.ActivityCalls);
                Assert.AreEqual(0, ado.ActivityCalls);
            }
            finally
            {
                pending.TrySetResult(users);
            }
            await refresh;
            Assert.AreEqual((12, 12), reports.Last());
        }

        [TestMethod]
        public async Task WorkProgress_DiscoveryAddsRealRepositoryWorkAndDoesNotCompleteBeforeItReturnsAsync()
        {
            var pending = new TaskCompletionSource<DevHubRepoDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
            var advanced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new Provider("GitHub")
            {
                ActivityRepositories = [_repo],
                Detail = token => pending.Task.WithCancellation(token),
            };
            var reports = new ConcurrentQueue<(int Completed, int Total)>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = Service(provider).RefreshAsync(timeout.Token, workProgress: new InlineWorkProgress(value =>
            {
                reports.Enqueue(value);
                if (value == (6, 8))
                    advanced.TrySetResult(true);
            }));
            try
            {
                await advanced.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.AreEqual((6, 8), reports.Last());
            }
            finally
            {
                pending.TrySetResult(new DevHubRepoDetail { RepoIdentifier = _repo });
            }
            await refresh;
            Assert.AreEqual((8, 8), reports.Last());
            Assert.IsTrue(reports.Any(value => value.Total == 7));
            Assert.IsTrue(reports.Any(value => value.Total == 8 && value.Completed < 8));
        }

        [TestMethod]
        public async Task WorkProgress_CacheVerificationCountsEachAccountWithoutRefetchingActivityAsync()
        {
            var github = new Provider("GitHub");
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com" };
            await Service(github, ado).RefreshAsync(CancellationToken.None);
            var users = await ado.GetAuthenticatedUsersAsync(CancellationToken.None);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var advanced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = new ConcurrentQueue<(int Completed, int Total)>();
            var calls = github.ActivityCalls + ado.ActivityCalls;
            ado.Authenticate = token => pending.Task.WithCancellation(token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var load = Service(github, ado).LoadFromCacheAsync(timeout.Token, workProgress: new InlineWorkProgress(value =>
            {
                reports.Enqueue(value);
                if (value == (3, 4))
                    advanced.TrySetResult(true);
            }));
            try
            {
                await advanced.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(load.IsCompleted);
                Assert.AreEqual(calls, github.ActivityCalls + ado.ActivityCalls);
            }
            finally
            {
                pending.TrySetResult(users);
            }
            Assert.IsNotNull(await load);
            Assert.AreEqual((4, 4), reports.Last());
        }

        [TestMethod]
        public async Task StepProgress_DescribesPendingCategoriesWithoutSerializingThemAsync()
        {
            var issues = new TaskCompletionSource<IReadOnlyList<DevHubIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pulls = new TaskCompletionSource<IReadOnlyList<DevHubPullRequest>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var issuesShown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pullsShown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var issuesStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new Provider("GitHub")
            {
                Issues = token =>
                {
                    issuesStarted.TrySetResult(true);
                    return issues.Task.WithCancellation(token);
                },
                Pulls = token => pulls.Task.WithCancellation(token),
            };
            using var model = new StartScreenViewModel();
            using var work = model.BackgroundProgress.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = Service(provider).RefreshAsync(timeout.Token, workProgress: work, stepProgress: new InlineStepProgress(step =>
            {
                work.Report(step);
                if (step == "Loading GitHub issues...")
                    issuesShown.TrySetResult(true);
                if (step == "Loading GitHub pull requests...")
                    pullsShown.TrySetResult(true);
            }));
            try
            {
                await pullsShown.Task.WithCancellation(timeout.Token);
                Assert.IsTrue(issuesStarted.Task.IsCompleted);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.AreEqual("Loading GitHub pull requests...", model.UnifiedRefreshStatus);
                pulls.SetResult([]);
                await issuesShown.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsFalse(issues.Task.IsCompleted);
                Assert.AreEqual("Loading GitHub issues...", model.UnifiedRefreshStatus);
            }
            finally
            {
                issues.TrySetResult([]);
                pulls.TrySetResult([]);
                await refresh;
            }
            Assert.AreEqual("Saving activity cache...", model.UnifiedRefreshStatus);
            Assert.AreEqual(100d, model.RefreshProgressValue);
            work.Dispose();
            Assert.IsFalse(model.IsRefreshProgressActive);
            Assert.AreEqual(string.Empty, model.UnifiedRefreshStatus);
        }

        [TestMethod]
        [DataRow(true, false)]
        [DataRow(false, false)]
        [DataRow(true, true)]
        [DataRow(false, true)]
        public async Task Refresh_PublishesEachProviderBeforeAnotherAuthenticatesOrFailsAsync(bool githubFirst, bool authenticationFails)
        {
            var github = new Provider("GitHub");
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com" };
            var fast = githubFirst ? github : ado;
            var slow = githubFirst ? ado : github;
            fast.Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("fresh issue")]);
            fast.Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("fresh pull")]);
            var users = await slow.GetAuthenticatedUsersAsync(CancellationToken.None);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            slow.Authenticate = token => pending.Task.WithCancellation(token);
            var service = Service(github, ado);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = service.RefreshAsync(timeout.Token, new InlineProgress(value =>
            {
                if (value.Issues.Count > 0 && value.PullRequests.Count > 0)
                    displayed.TrySetResult(true);
            }));
            try
            {
                await displayed.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsTrue(service.CurrentDashboard.IsRefreshing);
                Assert.AreEqual(fast.DisplayName, service.CurrentDashboard.Users.Single().ProviderName);
                Assert.AreEqual(4, fast.ActivityCalls);
                Assert.AreEqual(0, slow.ActivityCalls);
                Assert.IsTrue(service.CurrentDashboard.Sources.Keys.All(key => key.StartsWith(fast.DisplayName + "/", StringComparison.Ordinal)));
                Assert.IsNull(await new DevHubCacheService(_folder).ReadDashboardAsync(), "Do not persist an incomplete authentication context.");
                if (authenticationFails)
                    pending.TrySetException(new HttpRequestException("Authentication unavailable"));
            }
            finally
            {
                pending.TrySetResult(users);
                await refresh;
            }
            var final = await refresh;
            Assert.IsFalse(final.IsRefreshing);
            Assert.AreEqual("fresh issue", final.Issues.Single().Title);
            Assert.AreEqual("fresh pull", final.PullRequests.Single().Title);
            Assert.AreEqual(authenticationFails ? DevHubActivityStatus.Partial : DevHubActivityStatus.Success, final.Status);
            Assert.HasCount(authenticationFails ? 1 : 2, final.Users);
            var cached = await new DevHubCacheService(_folder).ReadDashboardAsync();
            Assert.IsNotNull(cached);
            Assert.AreEqual("fresh issue", cached.Issues.Single().Title);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Scope_AuthenticatesOnlyTheOwningProviderAsync(bool githubRepository)
        {
            var fast = new Provider(githubRepository ? "GitHub" : "Azure DevOps")
            {
                Host = githubRepository ? "github.com" : "dev.azure.com",
                Detail = _ => Task.FromResult(new DevHubRepoDetail { Issues = [Issue("scoped")] }),
            };
            var calls = 0;
            var unrelated = new Provider("unrelated")
            {
                Handles = _ => false,
                OnAuthenticate = () => calls++,
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var repo = githubRepository ? _repo : new RemoteRepoIdentifier("dev.azure.com", "org", "repo", "project");
            var scoped = await Service(unrelated, fast).ScopeToRepoAsync(repo, timeout.Token);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, unrelated.ActivityCalls);
            Assert.AreEqual(1, fast.ActivityCalls);
            Assert.AreEqual(fast.DisplayName, scoped.Users.Single().ProviderName);
            Assert.AreEqual("scoped", scoped.Issues.Single().Title);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Refresh_PendingAuthenticationCannotRestoreAnotherProvidersOrOldAccountsCacheAsync(bool accountChanged)
        {
            var github = new Provider("GitHub") { Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("cached GitHub")]) };
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com",
                Pulls = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([PullRequest("unverified Azure DevOps")]) };
            var service = Service(github, ado);
            await service.RefreshAsync(CancellationToken.None);
            var adoUsers = await ado.GetAuthenticatedUsersAsync(CancellationToken.None);
            var authentication = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pulls = new TaskCompletionSource<IReadOnlyList<DevHubPullRequest>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = new TaskCompletionSource<DevHubDashboard>(TaskCreationOptions.RunContinuationsAsynchronously);
            ado.Authenticate = token => authentication.Task.WithCancellation(token);
            github.Pulls = token => pulls.Task.WithCancellation(token);
            if (accountChanged)
                github.Username = "changed GitHub account";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = service.RefreshAsync(timeout.Token, new InlineProgress(value =>
            {
                if (value.Users.Count == 1 && value.Users[0].ProviderName == "GitHub")
                    published.TrySetResult(value);
            }));
            try
            {
                var interim = await published.Task.WithCancellation(timeout.Token);
                Assert.IsFalse(refresh.IsCompleted);
                Assert.IsTrue(interim.IsRefreshing);
                Assert.IsFalse(interim.Sources.Keys.Any(key => key.StartsWith("Azure DevOps/", StringComparison.Ordinal)));
                Assert.HasCount(accountChanged ? 0 : 1, interim.PullRequests);
                if (!accountChanged)
                    Assert.AreEqual("cached GitHub", interim.PullRequests.Single().Title);
            }
            finally
            {
                authentication.TrySetResult(adoUsers);
                pulls.TrySetResult([]);
                await refresh;
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Refresh_ContextChangeWhileAnotherProviderAuthenticatesInvalidatesPublishedResultsAsync(bool credentialChanged)
        {
            var github = new Provider("GitHub") { Issues = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([Issue("fresh")]) };
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com" };
            var users = await ado.GetAuthenticatedUsersAsync(CancellationToken.None);
            var pending = new TaskCompletionSource<IReadOnlyList<DevHubUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ado.Authenticate = token => pending.Task.WithCancellation(token);
            var query = "first";
            var generation = 0;
            var service = new DevHubService(new DevHubCacheService(_folder), [github, ado],
                _ => Task.FromResult<IReadOnlyList<string>>([]), () => DevHubSortOrder.MostRecent, () => query, () => generation);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refresh = service.RefreshAsync(timeout.Token, new InlineProgress(value =>
            {
                if (value.Issues.Count > 0)
                    displayed.TrySetResult(true);
            }));
            try
            {
                await displayed.Task.WithCancellation(timeout.Token);
                if (credentialChanged)
                    generation++;
                else
                    query = "changed";
            }
            finally
            {
                pending.TrySetResult(users);
                await refresh;
            }
            Assert.AreEqual(DevHubActivityStatus.Unauthorized, service.CurrentDashboard.Status);
            Assert.IsEmpty(service.CurrentDashboard.Issues);
            Assert.IsNull(await new DevHubCacheService(_folder).ReadDashboardAsync());
        }

        [TestMethod]
        public async Task Scope_DashboardUpdatesKeepOnlyTheMatchingProvidersVerifiedFallbackAsync()
        {
            var github = new Provider("GitHub")
            {
                Detail = _ => Task.FromResult(new DevHubRepoDetail { Issues = [Issue("scoped")] }),
            };
            var ado = new Provider("Azure DevOps") { Host = "dev.azure.com", Handles = _ => false };
            var service = Service(github, ado);
            await service.ScopeToRepoAsync(_repo, CancellationToken.None);
            ado.Username = "changed unrelated account";
            await service.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(1, service.ScopedSourceCount);
            github.Detail = _ => Task.FromException<DevHubRepoDetail>(new HttpRequestException("Offline"));
            var retained = await service.ScopeToRepoAsync(_repo, CancellationToken.None, forceRefresh: true);
            Assert.AreEqual("scoped", retained.Issues.Single().Title);
            github.Username = "changed scoped account";
            await service.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(0, service.ScopedSourceCount);
            Assert.IsEmpty((await service.ScopeToRepoAsync(_repo, CancellationToken.None)).Issues);
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

        private sealed class InlineWorkProgress(Action<(int Completed, int Total)> report) : IProgress<(int Completed, int Total)>
        {
            public void Report((int Completed, int Total) value) => report(value);
        }

        private sealed class InlineStepProgress(Action<string> report) : IProgress<string>
        {
            public void Report(string value) => report(value);
        }

        private sealed class Provider(string name) : IDevHubProvider
        {
            public string DisplayName => name;
            public string Username { get; set; } = "account";
            public string IdentityId { get; set; } = "identity";
            public bool Authenticated { get; set; } = true;
            public string Host { get; set; } = "github.com";
            public Func<CancellationToken, Task<IReadOnlyList<DevHubUser>>>? Authenticate { get; set; }
            public IReadOnlyList<RemoteRepoIdentifier> ActivityRepositories { get; set; } = [];
            public int ActivityCalls { get; private set; }
            public Action? OnAuthenticate { get; set; }
            public Func<CancellationToken, Task<IReadOnlyList<DevHubIssue>>> Issues { get; set; } = _ => Task.FromResult<IReadOnlyList<DevHubIssue>>([]);
            public Func<CancellationToken, Task<IReadOnlyList<DevHubPullRequest>>> Pulls { get; set; } = _ => Task.FromResult<IReadOnlyList<DevHubPullRequest>>([]);
            public Func<CancellationToken, Task<IReadOnlyList<DevHubCiRun>>> Builds { get; set; } = _ => Task.FromResult<IReadOnlyList<DevHubCiRun>>([]);
            public Func<CancellationToken, Task<DevHubRepoDetail>> Detail { get; set; } = _ => Task.FromResult(new DevHubRepoDetail());
            public Func<string, bool> Handles { get; set; } = _ => true;
            public bool CanHandle(string remoteUrl) => Handles(remoteUrl);
            public RemoteRepoIdentifier ParseRemoteUrl(string remoteUrl) => RemoteRepoIdentifier.TryParse(remoteUrl);
            public Task<DevHubUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken) => Task.FromResult(User());
            public Task<IReadOnlyList<DevHubUser>> GetAuthenticatedUsersAsync(CancellationToken cancellationToken)
            {
                if (Authenticate != null)
                    return Authenticate(cancellationToken);
                OnAuthenticate?.Invoke();
                return Task.FromResult<IReadOnlyList<DevHubUser>>(Authenticated ? [User()] : []);
            }
            private DevHubUser User() => new DevHubUser { Username = Username, IdentityId = IdentityId, Host = Host, ProviderName = name };
            public Task<IReadOnlyList<DevHubIssue>> GetUserIssuesAsync(CancellationToken cancellationToken)
            {
                ActivityCalls++;
                return Issues(cancellationToken);
            }
            public Task<IReadOnlyList<DevHubPullRequest>> GetUserPullRequestsAsync(CancellationToken cancellationToken)
            {
                ActivityCalls++;
                return Pulls(cancellationToken);
            }
            public Task<IReadOnlyList<DevHubCiRun>> GetUserCiRunsAsync(CancellationToken cancellationToken)
            {
                ActivityCalls++;
                return Builds(cancellationToken);
            }
            public Task<IReadOnlyList<RemoteRepoIdentifier>> GetActivityReposAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
            {
                ActivityCalls++;
                return Task.FromResult(ActivityRepositories);
            }
            public Task<DevHubRepoDetail> GetRepoDetailAsync(RemoteRepoIdentifier repo, int maxItems, CancellationToken cancellationToken)
            {
                ActivityCalls++;
                return Detail(cancellationToken);
            }
            public Task<DevHubRepoSummary> GetRepoSummaryAsync(RemoteRepoIdentifier repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public string GetRepoWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetPullRequestsWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
            public string GetIssuesWebUrl(RemoteRepoIdentifier repo) => throw new NotSupportedException();
        }
    }
}