using StartScreen.Models.DevHub;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class CopilotWorkItemSessionsTests
    {
        [TestMethod]
        public async Task Associations_PersistReplaceAndRemainSpecificToAnIssueOrRunAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("work-items.json");
            var first = Guid.NewGuid().ToString("D");
            var replacement = Guid.NewGuid().ToString("D");
            var issue = Issue("https://github.com/o/r/issues/1");
            Assert.IsNull(await CopilotWorkItemSessions.FindAsync(path, issue, CancellationToken.None));
            await CopilotWorkItemSessions.SaveAsync(path, issue, first, CancellationToken.None);
            Assert.AreEqual(first, await CopilotWorkItemSessions.FindAsync(path, Issue(issue.WebUrl + "#comment"), CancellationToken.None));
            Assert.IsNull(await CopilotWorkItemSessions.FindAsync(path, Issue("https://github.com/o/r/issues/2"), CancellationToken.None));
            await CopilotWorkItemSessions.SaveAsync(path, issue, replacement, CancellationToken.None);
            Assert.AreEqual(replacement, await CopilotWorkItemSessions.FindAsync(path, issue, CancellationToken.None));
            var run = Build("https://dev.azure.com/o/p/_build/results?buildId=1");
            await CopilotWorkItemSessions.SaveAsync(path, run, first, CancellationToken.None);
            Assert.IsNull(await CopilotWorkItemSessions.FindAsync(path, Build("https://dev.azure.com/o/p/_build/results?buildId=2"), CancellationToken.None));
            Assert.AreEqual(replacement, await CopilotWorkItemSessions.FindAsync(path, issue, CancellationToken.None));
        }

        [TestMethod]
        public async Task ConcurrentSaves_PreserveDifferentItemsAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("work-items.json");
            var id = Guid.NewGuid().ToString("D");
            var items = Enumerable.Range(1, 10).Select(index => Issue("https://github.com/o/r/issues/" + index)).ToArray();
            await Task.WhenAll(items.Select(item => CopilotWorkItemSessions.SaveAsync(path, item, id, CancellationToken.None)));
            foreach (var item in items)
                Assert.AreEqual(id, await CopilotWorkItemSessions.FindAsync(path, item, CancellationToken.None));
        }

        [TestMethod]
        public async Task InvalidAndCancelledWrites_DoNotReplaceExistingAssociationsAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("work-items.json");
            var item = Issue("https://github.com/o/r/issues/1");
            var id = Guid.NewGuid().ToString("D");
            await CopilotWorkItemSessions.SaveAsync(path, item, id, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => CopilotWorkItemSessions.SaveAsync(path, item, "bad", CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => CopilotWorkItemSessions.SaveAsync(path, item, Guid.NewGuid().ToString("D"), cancellation.Token));
            Assert.AreEqual(id, await CopilotWorkItemSessions.FindAsync(path, item, CancellationToken.None));
        }

        private static DevHubWorkItem Issue(string url) => DevHubWorkItem.GetMyWork([], [new DevHubIssue
        {
            Title = "Issue", WebUrl = url, IsAssignedToCurrentUser = true,
        }], []).Single();

        private static DevHubWorkItem Build(string url) => DevHubWorkItem.GetIncoming([], [], [new DevHubCiRun
        {
            Name = "CI", Status = "failure", WebUrl = url,
        }]).Single();
    }
}
