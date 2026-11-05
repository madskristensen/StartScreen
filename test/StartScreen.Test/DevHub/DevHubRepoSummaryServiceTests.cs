using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubRepoSummaryServiceTests
    {
        private static readonly RemoteRepoIdentifier _repo = new RemoteRepoIdentifier("github.com", "owner", "repo");

        [TestMethod]
        public async Task Summary_DeduplicatesConcurrentRequestsAndCachesAsync()
        {
            var calls = 0;
            var started = Signal<bool>();
            var completion = Signal<DevHubRepoSummary>();
            var service = Service(async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult(true);
                return await completion.Task;
            });

            var first = service.GetSummaryAsync(_repo, CancellationToken.None);
            await started.Task;
            var second = service.GetSummaryAsync(_repo, CancellationToken.None);
            var expected = Ready(_repo);
            completion.SetResult(expected);
            var results = await Task.WhenAll(first, second);
            var cached = await service.GetSummaryAsync(_repo, CancellationToken.None);

            Assert.AreSame(expected, results[0]);
            Assert.AreSame(expected, results[1]);
            Assert.AreSame(expected, cached);
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public async Task Summary_CancelingOneWaiterDoesNotCancelSharedFetchAsync()
        {
            var started = Signal<bool>();
            var completion = Signal<DevHubRepoSummary>();
            var fetchToken = CancellationToken.None;
            var service = Service(async (_, token) =>
            {
                fetchToken = token;
                started.SetResult(true);
                return await completion.Task;
            });
            using var cancellation = new CancellationTokenSource();
            var shared = service.GetSummaryAsync(_repo, CancellationToken.None);
            await started.Task;

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var canceledWaiter = service.GetSummaryAsync(_repo, cancellation.Token);
                cancellation.Cancel();
                await canceledWaiter;
            });
            Assert.IsFalse(fetchToken.IsCancellationRequested);
            completion.SetResult(Ready(_repo));
            Assert.AreEqual(12, (await shared).OpenPullRequests);
        }

        [TestMethod]
        public async Task Summary_PreCanceledDoesNotFetchAsync()
        {
            var calls = 0;
            var service = Service((repo, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Ready(repo));
            });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetSummaryAsync(_repo, cancellation.Token));
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public async Task Summary_BoundsConcurrentRepositoryFetchesAsync()
        {
            var active = 0;
            var maximum = 0;
            var gate = new object();
            var bothStarted = Signal<bool>();
            var release = Signal<bool>();
            var service = Service(async (repo, _) =>
            {
                var current = Interlocked.Increment(ref active);
                lock (gate)
                {
                    maximum = Math.Max(maximum, current);
                }
                if (current == 2)
                    bothStarted.TrySetResult(true);
                await release.Task;
                Interlocked.Decrement(ref active);
                return Ready(repo);
            }, concurrency: 2);

            var pending = Enumerable.Range(0, 6).Select(index => service.GetSummaryAsync(
                new RemoteRepoIdentifier("github.com", "owner", "repo" + index), CancellationToken.None)).ToArray();
            await bothStarted.Task;
            Assert.AreEqual(2, active);
            release.SetResult(true);
            await Task.WhenAll(pending);
            Assert.AreEqual(2, maximum);
        }

        [TestMethod]
        public async Task Summary_AccountAndServerCollectionHaveSeparateKeysAsync()
        {
            var calls = 0;
            var account = "first";
            var service = Service((repo, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Ready(repo));
            }, () => account);
            await service.GetSummaryAsync(_repo, CancellationToken.None);
            account = "second";
            await service.GetSummaryAsync(_repo, CancellationToken.None);
            var first = new RemoteRepoIdentifier("server", "collection", "repo", "project", "https://server:8080/tfs/collection", true);
            var second = new RemoteRepoIdentifier("server", "collection", "repo", "project", "https://server:8081/other/collection", true);
            await service.GetSummaryAsync(first, CancellationToken.None);
            await service.GetSummaryAsync(second, CancellationToken.None);

            Assert.AreEqual(4, calls);
            service.Invalidate(_repo);
            await service.GetSummaryAsync(_repo, CancellationToken.None);
            Assert.AreEqual(5, calls);
            service.InvalidateAll();
            await service.GetSummaryAsync(first, CancellationToken.None);
            Assert.AreEqual(6, calls);
        }

        [TestMethod]
        public async Task Summary_InvalidationDuringFetchDoesNotRepopulateCacheAsync()
        {
            var calls = 0;
            var oldResult = Signal<DevHubRepoSummary>();
            var started = Signal<bool>();
            var service = Service((repo, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult(true);
                    return oldResult.Task;
                }
                return Task.FromResult(Ready(repo));
            });
            var original = service.GetSummaryAsync(_repo, CancellationToken.None);
            await started.Task;
            service.Invalidate(_repo);
            var fresh = await service.GetSummaryAsync(_repo, CancellationToken.None);
            oldResult.SetResult(new DevHubRepoSummary(_repo, DevHubRepoSummaryStatus.Ready, 99));
            await original;

            Assert.AreSame(fresh, await service.GetSummaryAsync(_repo, CancellationToken.None));
            Assert.AreEqual(12, fresh.OpenPullRequests);
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public async Task Summary_ExpiredAndFailedResultsAreRetriedAsync()
        {
            var calls = 0;
            var service = Service((repo, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Ready(repo));
            }, lifetime: TimeSpan.Zero);
            await service.GetSummaryAsync(_repo, CancellationToken.None);
            await service.GetSummaryAsync(_repo, CancellationToken.None);
            Assert.AreEqual(2, calls);

            calls = 0;
            service = Service((repo, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Unavailable) : Ready(repo)));
            Assert.IsNull((await service.GetSummaryAsync(_repo, CancellationToken.None)).OpenPullRequests);
            Assert.AreEqual(12, (await service.GetSummaryAsync(_repo, CancellationToken.None)).OpenPullRequests);
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public async Task Summary_ShutdownCancelsSharedFetchWithoutAnUnavailableResultAsync()
        {
            using var shutdown = new CancellationTokenSource();
            var started = Signal<bool>();
            var fetchToken = CancellationToken.None;
            var calls = 0;
            var service = Service(async (repo, token) =>
            {
                Interlocked.Increment(ref calls);
                fetchToken = token;
                started.SetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Ready(repo);
            }, shutdownToken: shutdown.Token);

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var request = service.GetSummaryAsync(_repo, CancellationToken.None);
                await started.Task;
                shutdown.Cancel();
                await request;
            });

            Assert.IsTrue(fetchToken.IsCancellationRequested);
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetSummaryAsync(_repo, CancellationToken.None));
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public async Task Summary_PreCanceledShutdownStopsBeforeOptionsOrFetchAsync()
        {
            using var shutdown = new CancellationTokenSource();
            shutdown.Cancel();
            var service = new DevHubRepoSummaryService(shutdown.Token);

            await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetSummaryAsync(_repo, CancellationToken.None));
        }

        [TestMethod]
        public async Task Summary_HideCancelsPendingPreservesCacheAndAllowsReopeningAsync()
        {
            var pendingRepo = new RemoteRepoIdentifier("github.com", "owner", "pending");
            var started = Signal<bool>();
            var pendingToken = CancellationToken.None;
            var pendingCalls = 0;
            var completedCalls = 0;
            var service = Service(async (repo, token) =>
            {
                if (repo.Equals(pendingRepo))
                {
                    if (Interlocked.Increment(ref pendingCalls) == 1)
                    {
                        pendingToken = token;
                        started.SetResult(true);
                        await Task.Delay(Timeout.Infinite, token);
                    }
                }
                else
                    Interlocked.Increment(ref completedCalls);
                return Ready(repo);
            });
            var cached = await service.GetSummaryAsync(_repo, CancellationToken.None);

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var request = service.GetSummaryAsync(pendingRepo, CancellationToken.None);
                await started.Task;
                service.CancelPendingRequests();
                await request;
            });

            Assert.IsTrue(pendingToken.IsCancellationRequested);
            Assert.AreSame(cached, await service.GetSummaryAsync(_repo, CancellationToken.None));
            var reopened = await service.GetSummaryAsync(pendingRepo, CancellationToken.None);
            Assert.AreEqual(DevHubRepoSummaryStatus.Ready, reopened.Status);
            Assert.AreEqual(12, reopened.OpenPullRequests);
            Assert.AreEqual(1, completedCalls);
            Assert.AreEqual(2, pendingCalls);
        }

        [TestMethod]
        public async Task Summary_HideAlsoCancelsInvalidatedAndQueuedRequestsAsync()
        {
            var started = Signal<bool>();
            var service = Service(async (repo, token) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Ready(repo);
            }, concurrency: 1);

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var invalidated = service.GetSummaryAsync(_repo, CancellationToken.None);
                await started.Task;
                service.InvalidateAll();
                var queued = service.GetSummaryAsync(_repo, CancellationToken.None);
                service.CancelPendingRequests();
                await Task.WhenAll(invalidated, queued);
                Assert.Fail("Both pending operations must be canceled.");
            });
        }

        private static TaskCompletionSource<T> Signal<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static DevHubRepoSummary Ready(RemoteRepoIdentifier repo) => new DevHubRepoSummary(repo, DevHubRepoSummaryStatus.Ready, 12, 3);

        private static DevHubRepoSummaryService Service(Func<RemoteRepoIdentifier, CancellationToken, Task<DevHubRepoSummary>> fetch,
            Func<string>? account = null, TimeSpan? lifetime = null, int concurrency = 4, CancellationToken shutdownToken = default) =>
            new DevHubRepoSummaryService(fetch, account ?? (() => "account"), lifetime ?? TimeSpan.FromMinutes(2), concurrency, shutdownToken);
    }
}
