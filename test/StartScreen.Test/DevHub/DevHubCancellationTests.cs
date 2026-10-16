using System.Net;
using System.Net.Http;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubCancellationTests
    {
        [TestMethod]
        public async Task GitHubAuthentication_WhenCanceledBeforeRequest_DoesNotSendAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var provider = new GitHubDevHubProvider();

            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                provider.GetAuthenticatedUserAsync(client, new DevHubCredential("user", "test-token"), cancellation.Token));

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.AreEqual(0, handler.RequestCount);
        }

        [TestMethod]
        public async Task GitHubAuthentication_WhenCanceledDuringRequest_PropagatesCancellationAsync()
        {
            using var cancellation = new CancellationTokenSource();
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellation.Token.Register(() => response.TrySetCanceled());
            var handler = new PendingHttpMessageHandler(response.Task);
            using var client = new HttpClient(handler);
            var provider = new GitHubDevHubProvider();

            var task = provider.GetAuthenticatedUserAsync(client, new DevHubCredential("user", "test-token"), cancellation.Token);
            Assert.AreEqual(1, handler.RequestCount);
            Assert.IsFalse(task.IsCompleted);

            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => task);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task GitHubAuthentication_WhenHttpTimesOut_HandlesFailureAsync()
        {
            var handler = new TestHttpMessageHandler((_, _) => throw new TaskCanceledException("Simulated HTTP timeout"));
            using var client = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var provider = new GitHubDevHubProvider();

            var user = await provider.GetAuthenticatedUserAsync(client, new DevHubCredential("user", "test-token"), cancellation.Token);

            Assert.IsNull(user);
            Assert.IsFalse(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, handler.RequestCount);
        }

        [TestMethod]
        public async Task GitHubAuthentication_WithSuccessfulResponse_PreservesUserAsync()
        {
            var handler = new TestHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"login\":\"test-user\",\"name\":\"Test User\",\"avatar_url\":\"https://example.com/avatar\"}"),
            });
            using var client = new HttpClient(handler);
            var provider = new GitHubDevHubProvider();

            var user = await provider.GetAuthenticatedUserAsync(client, new DevHubCredential("user", "test-token"), CancellationToken.None);

            Assert.IsNotNull(user);
            Assert.AreEqual("test-user", user.Username);
            Assert.AreEqual("Test User", user.DisplayName);
            Assert.AreEqual("https://example.com/avatar", user.AvatarUrl);
            Assert.AreEqual("GitHub", user.ProviderName);
            Assert.AreEqual("github.com", user.Host);
            Assert.AreEqual(1, handler.RequestCount);
            Assert.AreEqual("https://api.github.com/user", handler.LastRequest?.RequestUri?.AbsoluteUri);
        }

        [TestMethod]
        public async Task CredentialLookup_WhenCanceled_StopsBeforeLookupAsync()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                DevHubCredentialHelper.GetCredentialAsync("github.com", cancellation.Token));

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        }

        [TestMethod]
        public async Task Refresh_WhenCanceled_LeavesDashboardUnchangedAsync()
        {
            var service = new DevHubService();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.RefreshAsync(cancellation.Token));

            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsNull(service.CurrentDashboard);
            Assert.IsFalse(service.IsRefreshing);
        }

        private sealed class PendingHttpMessageHandler : HttpMessageHandler
        {
            private readonly Task<HttpResponseMessage> _response;

            public PendingHttpMessageHandler(Task<HttpResponseMessage> response)
            {
                _response = response;
            }

            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return _response;
            }
        }
    }
}