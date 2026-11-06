using System.Net;
using System.Net.Http;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubRequestWarningTests
    {
        [TestMethod]
        public async Task OptionalMetadataWarningDoesNotInvalidateSuccessfulInventoryAsync()
        {
            var result = await DevHubActivityRequest.RunAsync(() =>
            {
                DevHubActivityRequest.Warning("Some review details are unavailable.");
                return Task.FromResult<IReadOnlyList<string>>(["current pull request"]);
            }, items => items.Count > 0, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Success, result.Status);
            Assert.AreEqual("current pull request", result.Value.Single());
            Assert.AreEqual("Some review details are unavailable.", result.Message);
        }

        [TestMethod]
        public async Task RepositoryCategoryPreservesOptionalMetadataWarningWithoutStaleInventoryAsync()
        {
            var detail = new DevHubRepoDetail();
            detail.PullRequests = await DevHubActivityRequest.ReadCategoryAsync(detail, DevHubActivityKind.PullRequests, () =>
            {
                DevHubActivityRequest.Warning("Some review details are unavailable.");
                return Task.FromResult(new List<DevHubPullRequest> { new DevHubPullRequest { Title = "current" } });
            }, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Success, detail.PullRequestsStatus);
            Assert.AreEqual("current", detail.PullRequests.Single().Title);
            Assert.AreEqual("Some review details are unavailable.", detail.ActivityWarning);
        }

        [TestMethod]
        public async Task HttpFailureRemainsExplicitWithSafeDetailsAsync()
        {
            var result = await DevHubActivityRequest.RunAsync(() =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/search/issues?q=private-query");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-token");
                using var response = DevHubActivityRequest.Observe(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });
                return Task.FromResult<IReadOnlyList<string>>([]);
            }, items => items.Count > 0, CancellationToken.None);

            Assert.AreEqual(DevHubActivityStatus.Unavailable, result.Status);
            Assert.AreEqual("api.github.com returned HTTP 403. Refresh to retry.", result.Message);
            Assert.IsEmpty(result.Value);
        }

        [TestMethod]
        public async Task ConcurrentOperationsDoNotShareWarningsAsync()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var warned = DevHubActivityRequest.RunAsync(async () =>
            {
                DevHubActivityRequest.Warning("Only this source has unavailable details.");
                started.SetResult(true);
                await release.Task;
                return new[] { "item" };
            }, items => items.Length > 0, CancellationToken.None);
            DevHubActivityResult<string[]> clean;
            try
            {
                Assert.AreSame(started.Task, await Task.WhenAny(started.Task, warned, Task.Delay(TimeSpan.FromSeconds(10))),
                    "The warning producer must signal readiness, not fail early or hang.");
                clean = await DevHubActivityRequest.RunAsync(() => Task.FromResult(Array.Empty<string>()),
                    items => items.Length > 0, CancellationToken.None);
            }
            finally
            {
                release.SetResult(true);
            }

            Assert.IsNull(clean.Message);
            Assert.AreEqual(DevHubActivityStatus.Success, clean.Status);
            Assert.AreEqual("Only this source has unavailable details.", (await warned).Message);
        }
    }
}