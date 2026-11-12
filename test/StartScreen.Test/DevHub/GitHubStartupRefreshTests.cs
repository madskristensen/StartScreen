using System.Diagnostics;
using System.Net;
using System.Net.Http;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.Services.DevHub.Providers;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class GitHubStartupRefreshTests
    {
        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public async Task RepositoryRefresh_IndependentCategoryLatencyAsync()
        {
            using var client = new HttpClient(new DelayedHandler());
            var elapsed = Stopwatch.StartNew();
            var detail = await GitHubDevHubProvider.GetRepoDetailAsync(client,
                new DevHubCredential("account", "test-token"), null!,
                new RemoteRepoIdentifier("github.com", "owner", "repo"),
                new DevHubActivityOptions(), CancellationToken.None);
            elapsed.Stop();
            TestContext.WriteLine($"Repository refresh with three 200ms responses: {elapsed.ElapsedMilliseconds}ms");
            Assert.IsFalse(detail.HasData);
            Assert.AreEqual(DevHubActivityStatus.Success, detail.Status);
        }

        private sealed class DelayedHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(200, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/actions/runs", StringComparison.Ordinal)
                        ? "{\"workflow_runs\":[]}" : "[]"),
                };
            }
        }
    }
}
