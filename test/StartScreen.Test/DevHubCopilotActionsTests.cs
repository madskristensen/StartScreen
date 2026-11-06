using System.ComponentModel.Design;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.Shell;
using StartScreen.Commands;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;
using StartScreen.Services.DevHub;

namespace StartScreen.Test
{
    [TestClass]
    [DoNotParallelize]
    public class DevHubCopilotActionsTests
    {
        [TestCleanup]
        public void Cleanup() => DevHubWorkItemMenu.Current = null;

        [TestMethod]
        [DataRow("DevHubFixWithCopilot")]
        [DataRow("DevHubRequestCopilotReview")]
        public void CopilotMenuCommands_UseTheGitHubCopilotMoniker(string commandId)
        {
            using var stream = typeof(DevHubCopilotActionsTests).Assembly.GetManifestResourceStream("VSCommandTable.vsct");
            Assert.IsNotNull(stream);
            var document = XDocument.Load(stream);
            XNamespace vsct = "http://schemas.microsoft.com/VisualStudio/2005-10-18/CommandTable";
            var button = document.Descendants(vsct + "Button").Single(element => element.Attribute("id")?.Value == commandId);

            Assert.AreEqual("ImageCatalogGuid", button.Element(vsct + "Icon")?.Attribute("guid")?.Value);
            Assert.AreEqual("GitHubCopilot", button.Element(vsct + "Icon")?.Attribute("id")?.Value);
            Assert.IsTrue(button.Elements(vsct + "CommandFlag").Any(element => element.Value == "IconIsMoniker"));
        }

        [TestMethod]
        [DataRow("https://github.com/owner/repo/issues/42")]
        [DataRow("https://dev.azure.com/org/project/_workitems/edit/42")]
        public void IssuePrompt_StartsWithFixAndTheProviderLink(string url)
        {
            var item = Issue(url);
            var prompt = CopilotWorkItemPrompt.Create(item);

            Assert.StartsWith("Fix " + url + Environment.NewLine, prompt);
            StringAssert.Contains(prompt, "Confirm the current workspace matches the repository");
            StringAssert.Contains(prompt, "run the relevant tests");
            Assert.IsTrue(CopilotWorkItemPrompt.CanStartChat(item));
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void FailedBuilds_IncludeRunBranchAndFailureLinksInBothBlades(bool personal)
        {
            var build = new DevHubCiRun
            {
                Name = "CI", Status = "failure", Branch = "feature/fix", RepoDisplayName = "owner/repo",
                IsTriggeredByCurrentUser = personal, WebUrl = "https://github.com/owner/repo/actions/runs/123",
                FailureSummary = "Unit tests failed", FailureUrl = "https://github.com/owner/repo/actions/runs/123/job/456",
            };
            var item = (personal ? DevHubWorkItem.GetMyWork([], [], [], builds: [build])
                : DevHubWorkItem.GetIncoming([], [], [build])).Single();
            var prompt = CopilotWorkItemPrompt.Create(item);

            Assert.IsTrue(item.IsFailedBuild);
            StringAssert.Contains(prompt, build.WebUrl);
            StringAssert.Contains(prompt, build.FailureUrl);
            StringAssert.Contains(prompt, "Branch: feature/fix");
            StringAssert.Contains(prompt, "Unit tests failed");
            StringAssert.Contains(prompt, "Repository: owner/repo");
        }

        [TestMethod]
        [DataRow("success")]
        [DataRow("pending")]
        [DataRow("cancelled")]
        [DataRow("skipped")]
        public void NonFailedBuilds_DoNotOfferInvestigation(string status)
        {
            var item = Build(status);
            Assert.IsFalse(item.IsFailedBuild);
            Assert.IsFalse(CopilotWorkItemPrompt.CanStartChat(item));
            Assert.ThrowsExactly<InvalidOperationException>(() => CopilotWorkItemPrompt.Create(item));
            SetMenu(item);
            Assert.IsFalse(Command(PackageIds.DevHubFixWithCopilot).Visible);
        }

        [TestMethod]
        public void FailedBuildWithoutFailureMetadata_StillUsesTheRunLink()
        {
            var prompt = CopilotWorkItemPrompt.Create(Build("failure"));
            StringAssert.Contains(prompt, "https://example.com/build/1");
            Assert.DoesNotContain("Failure details:", prompt);
        }

        [TestMethod]
        [DataRow("javascript:alert(1)")]
        [DataRow("https://example.com/issue\r\nInjected")]
        public void InvalidIssueLinks_AreRejected(string url)
        {
            Assert.ThrowsExactly<ArgumentException>(() => CopilotWorkItemPrompt.Create(Issue(url)));
        }

        [TestMethod]
        public void MenuStatus_TracksEachCardRatherThanReusingThePreviousCardState()
        {
            SetMenu(Issue("https://github.com/owner/repo/issues/42"));
            var fix = Command(PackageIds.DevHubFixWithCopilot);
            Assert.IsTrue(fix.Visible);
            Assert.IsTrue(fix.Enabled);
            Assert.AreEqual("Fix with Copilot", fix.Text);
            Assert.AreEqual("Open in Browser", Command(PackageIds.DevHubOpenInBrowser).Text);
            Assert.IsFalse(Command(PackageIds.DevHubRequestCopilotReview).Visible);

            SetMenu(Build("failure"));
            DevHubWorkItemMenu.Update(fix, PackageIds.DevHubFixWithCopilot);
            Assert.AreEqual("Investigate with Copilot", fix.Text);
            Assert.IsTrue(fix.Enabled);

            SetMenu(PullRequest("https://github.com/owner/repo/pull/42"));
            DevHubWorkItemMenu.Update(fix, PackageIds.DevHubFixWithCopilot);
            Assert.IsFalse(fix.Visible);
            Assert.IsFalse(fix.Enabled);
            Assert.IsTrue(Command(PackageIds.DevHubRequestCopilotReview).Visible);
            Assert.IsTrue(Command(PackageIds.DevHubReviewInVisualStudio).Visible);
            Assert.AreEqual("Review on the web", Command(PackageIds.DevHubOpenInBrowser).Text);

            DevHubWorkItemMenu.Current = null;
            DevHubWorkItemMenu.Update(fix, PackageIds.DevHubFixWithCopilot);
            Assert.IsFalse(fix.Visible);
            Assert.IsFalse(fix.Enabled);
        }

        [TestMethod]
        [DataRow("https://dev.azure.com/org/project/_git/repo/pullrequest/42", true)]
        [DataRow("https://bitbucket.org/owner/repo/pull-requests/42", false)]
        public void OtherProviderPrMenus_DoNotAdvertiseGitHubReviewRequests(string url, bool native)
        {
            SetMenu(PullRequest(url));
            Assert.IsFalse(Command(PackageIds.DevHubRequestCopilotReview).Visible);
            Assert.AreEqual(native, Command(PackageIds.DevHubReviewInVisualStudio).Visible);
            Assert.IsTrue(Command(PackageIds.DevHubOpenInBrowser).Enabled);
        }

        [TestMethod]
        public void CompletedPrs_DoNotOfferNewCopilotReviews()
        {
            SetMenu(PullRequest("https://github.com/owner/repo/pull/42", completed: true));
            Assert.IsFalse(Command(PackageIds.DevHubRequestCopilotReview).Visible);
            Assert.IsTrue(Command(PackageIds.DevHubReviewInVisualStudio).Visible);
        }

        [TestMethod]
        public void MenuScopeActions_PreserveRepositoryScopeAndShowAllState()
        {
            var item = Issue("https://github.com/owner/repo/issues/42");
            SetMenu(item);
            Assert.IsTrue(Command(PackageIds.DevHubScopeToRepo).Enabled);
            Assert.AreEqual("Scope to repo", Command(PackageIds.DevHubScopeToRepo).Text);
            Assert.IsFalse(Command(PackageIds.DevHubShowAllRepos).Visible);
            DevHubWorkItemMenu.Current.IsScoped = true;
            Assert.IsFalse(Command(PackageIds.DevHubScopeToRepo).Visible);
            Assert.IsTrue(Command(PackageIds.DevHubShowAllRepos).Enabled);
        }

        [TestMethod]
        public async Task MenuExecution_CapturesTheRequestedActionBeforeTheMenuClosesAsync()
        {
            var completion = new TaskCompletionSource<bool>();
            int? executed = null;
            SetMenu(Issue("https://github.com/owner/repo/issues/42"));
            DevHubWorkItemMenu.Current.ExecuteActionAsync = id =>
            {
                executed = id;
                return completion.Task;
            };
            var task = DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubFixWithCopilot);
            DevHubWorkItemMenu.Current = null;
            completion.SetResult(true);
            await task;
            Assert.AreEqual(PackageIds.DevHubFixWithCopilot, executed);
            Assert.ThrowsExactly<InvalidOperationException>(() => DevHubWorkItemMenu.ExecuteAsync(PackageIds.DevHubCopyUrl));
        }

        [TestMethod]
        public async Task ReviewRequest_PostsOnlyCopilotUsingTheProvidedAccountAsync()
        {
            int requests = 0;
            using var client = new HttpClient(new Handler(async request =>
            {
                requests++;
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("https://api.github.com/repos/owner/repo/pulls/42/requested_reviewers", request.RequestUri!.AbsoluteUri);
                Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
                Assert.AreEqual("test-token", request.Headers.Authorization.Parameter);
                Assert.AreEqual("2026-03-10", request.Headers.GetValues("X-GitHub-Api-Version").Single());
                Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
                CollectionAssert.AreEqual(new[] { "copilot-pull-request-reviewer[bot]" },
                    body.RootElement.GetProperty("reviewers").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.AreEqual(1, body.RootElement.EnumerateObject().Count());
                return new HttpResponseMessage(HttpStatusCode.Created);
            }));

            await CopilotReviewService.RequestAsync(client, new DevHubCredential("me", "test-token"),
                "https://github.com/owner/repo/pull/42?tab=files#discussion", CancellationToken.None);
            Assert.AreEqual(1, requests);
        }

        [TestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [DataRow(422)]
        [DataRow(429)]
        [DataRow(503)]
        public async Task ReviewRequest_FailuresAreExplicitAndNotRetriedAsync(int status)
        {
            int requests = 0;
            using var client = new HttpClient(new Handler(_ =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
            }));
            var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => CopilotReviewService.RequestAsync(client,
                new DevHubCredential("me", "test-token"), "https://github.com/owner/repo/pull/42", CancellationToken.None));
            StringAssert.Contains(error.Message, "HTTP " + status);
            Assert.AreEqual(1, requests);
        }

        [TestMethod]
        [DataRow("https://github.com.evil.example/o/r/pull/42")]
        [DataRow("https://dev.azure.com/o/p/_git/r/pullrequest/42")]
        [DataRow("https://github.com/o/r/issues/42")]
        [DataRow("https://github.com/o/r/pull/0")]
        [DataRow("http://github.com/o/r/pull/42")]
        public async Task ReviewRequest_RejectsUnsupportedUrlsBeforeSendingCredentialsAsync(string url)
        {
            Assert.IsFalse(CopilotReviewService.Supports(url));
            using var client = new HttpClient(new Handler(_ => throw new AssertFailedException("Unexpected request")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => CopilotReviewService.RequestAsync(client,
                new DevHubCredential("me", "test-token"), url, CancellationToken.None));
        }

        [TestMethod]
        public async Task ReviewRequest_MissingCredentialsNeverSendsARequestAsync()
        {
            using var client = new HttpClient(new Handler(_ => throw new AssertFailedException("Unexpected request")));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CopilotReviewService.RequestAsync(client,
                null!, "https://github.com/owner/repo/pull/42", CancellationToken.None));
        }

        private static DevHubWorkItem Issue(string url) => DevHubWorkItem.GetIncoming([], [new DevHubIssue
        {
            WebUrl = url, Title = "Issue",
            RepoIdentifier = new RemoteRepoIdentifier("github.com", "owner", "repo"),
        }], []).Single();

        private static DevHubWorkItem Build(string status) => DevHubWorkItem.GetIncoming([], [], [new DevHubCiRun
        {
            WebUrl = "https://example.com/build/1", Name = "Build", Status = status,
        }]).Single();

        private static DevHubWorkItem PullRequest(string url, bool completed = false) => DevHubWorkItem.GetMyWork([new DevHubPullRequest
        {
            WebUrl = url, Title = "PR", IsAuthoredByCurrentUser = true, Status = completed ? "closed" : "open",
            IsMerged = completed, UpdatedAt = DateTime.UtcNow,
        }], [], [], includeCompleted: completed).Single();

        private static void SetMenu(DevHubWorkItem item) => DevHubWorkItemMenu.Current = new DevHubWorkItemMenu { Item = item };

        private static OleMenuCommand Command(int id)
        {
            var command = new OleMenuCommand(null, new CommandID(PackageGuids.StartScreen, id));
            DevHubWorkItemMenu.Update(command, id);
            return command;
        }

        private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
        }
    }
}