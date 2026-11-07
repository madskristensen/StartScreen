using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using StartScreen.Services;
using StartScreen.Services.DevHub;

namespace StartScreen.Test
{
    [TestClass]
    public class SharedRefreshTests
    {
        [TestMethod]
        public async Task CompletedRunCacheSeparatesAccountsAndRerunsAsync()
        {
            var resource = Guid.NewGuid().ToString();
            var credential = new DevHubCredential("account", Guid.NewGuid().ToString());
            var calls = 0;
            Task<string> Fetch(CancellationToken _) => Task.FromResult((++calls).ToString());
            Assert.AreEqual("1", await DevHubMetadataCache.GetCompletedRunAsync(credential, resource + "|1", Fetch, default));
            Assert.AreEqual("1", await DevHubMetadataCache.GetCompletedRunAsync(credential, resource + "|1", Fetch, default));
            Assert.AreEqual("2", await DevHubMetadataCache.GetCompletedRunAsync(credential, resource + "|2", Fetch, default));
            Assert.AreEqual("3", await DevHubMetadataCache.GetCompletedRunAsync(new DevHubCredential("other", Guid.NewGuid().ToString()),
                resource + "|1", Fetch, default));
            Assert.AreEqual(3, calls);
        }

        [TestMethod]
        public async Task SuccessfulCacheUsesExactTenMinuteBoundaryAndManualInvalidationAsync()
        {
            var now = DateTime.UtcNow;
            var refresh = new SharedRefresh<int>(() => now, TimeSpan.FromMinutes(10));
            var requests = 0;
            Task<int> Fetch(CancellationToken _) => Task.FromResult(++requests);
            Assert.AreEqual(1, await refresh.RunAsync("account", false, Fetch, value => value > 0, default));
            now = now.AddMinutes(10).AddTicks(-1);
            Assert.AreEqual(1, await refresh.RunAsync("account", false, Fetch, value => value > 0, default));
            now = now.AddTicks(1);
            Assert.AreEqual(2, await refresh.RunAsync("account", false, Fetch, value => value > 0, default));
            Assert.AreEqual(3, await refresh.RunAsync("account", true, Fetch, value => value > 0, default));
        }

        [TestMethod]
        public async Task SharedWaiterCancellationDoesNotCancelFetchAndManualRetryBypassesBackoffAsync()
        {
            var now = DateTime.UtcNow;
            var refresh = new SharedRefresh<int>(() => now);
            var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            Task<int> Fetch(CancellationToken token) { Interlocked.Increment(ref requests); return pending.Task; }
            using var canceled = new CancellationTokenSource();
            Task<int> first = refresh.RunAsync("account", false, Fetch, value => value > 0, canceled.Token);
            Task<int> second = refresh.RunAsync("account", false, Fetch, value => value > 0, default);
            canceled.Cancel();
#pragma warning disable VSTHRD003 // Deliberately await the independently shared request to verify waiter cancellation.
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await first);
#pragma warning restore VSTHRD003
            pending.SetResult(0);
            Assert.AreEqual(0, await second);
            Assert.AreEqual(1, requests);
            Assert.AreEqual(0, await refresh.RunAsync("account", false, Fetch, value => value > 0, default));
            Assert.AreEqual(1, requests);
            Assert.AreEqual(0, await refresh.RunAsync("account", true, Fetch, value => value > 0, default));
            Assert.AreEqual(2, requests);
            now = now.AddMinutes(1);
            await refresh.RunAsync("account", false, Fetch, value => value > 0, default);
            Assert.AreEqual(3, requests);
            await refresh.RunAsync("other account", false, Fetch, value => value > 0, default);
            Assert.AreEqual(4, requests);
        }

        [TestMethod]
        [DataRow(429, false)]
        [DataRow(503, true)]
        public void RetryAfterSupportsSecondsAndDates(int status, bool date)
        {
            var url = Guid.NewGuid().ToString();
            using var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = date ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(3))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
            HttpRetryBackoff.Observe(url, response);
            Assert.IsTrue(HttpRetryBackoff.IsDeferred(url));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-1));
            HttpRetryBackoff.Observe(url, response);
            Assert.IsFalse(HttpRetryBackoff.IsDeferred(url));
        }
    }
}
