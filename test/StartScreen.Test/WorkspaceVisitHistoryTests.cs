using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.Services;

namespace StartScreen.Test
{
    [TestClass]
    public class WorkspaceVisitHistoryTests
    {
        [TestMethod]
        public void FirstVisitAndUnchangedSnapshots_DoNotClaimThatNothingChanged()
        {
            var current = Capture(Pr());
            Assert.IsNull(WorkspaceVisitHistory.Compare(null, current, false));
            Assert.IsNull(WorkspaceVisitHistory.Compare(current, Capture(Pr()), false));
        }

        [TestMethod]
        public void KnownChanges_CompareWorkflowChecksDiscussionsAndCommits()
        {
            var before = Pr();
            before.CiStatus = "success";
            before.UnresolvedThreadCount = 0;
            before.HeadCommitId = "old";
            var previous = Capture(before);
            var after = Pr();
            after.CiStatus = "failure";
            after.UnresolvedThreadCount = 2;
            after.HeadCommitId = "new";
            after.Status = "conflicts";
            var text = WorkspaceVisitHistory.Compare(previous, Capture(after), false);
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "Since opened ");
            StringAssert.Contains(text, "open -> conflicts");
            StringAssert.Contains(text, "success -> failure");
            StringAssert.Contains(text, "0 -> 2");
            StringAssert.Contains(text, "different head commit");
        }

        [TestMethod]
        public void UnknownMetadataAndMissingItems_AreNotInterpretedAsChangesOrCompletions()
        {
            var previous = Capture(Pr());
            var after = Pr();
            after.CiStatus = "failure";
            after.HasChangesRequested = true;
            after.UnresolvedThreadCount = 3;
            after.HeadCommitId = "now-known";
            Assert.IsNull(WorkspaceVisitHistory.Compare(previous, Capture(after), false));
            var missing = WorkspaceVisitHistory.Capture(Item(), [], [], "account|query", DateTime.UtcNow);
            Assert.IsNull(WorkspaceVisitHistory.Compare(previous, missing, false));
        }

        [TestMethod]
        public void NewlyDiscoveredItems_AreNotClaimedToBeNewlyCreated()
        {
            var previous = WorkspaceVisitHistory.Capture(Item(), [], [], "account|query", DateTime.UtcNow.AddDays(-1));
            var text = WorkspaceVisitHistory.Compare(previous, Capture(Pr()), true);
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "newly present in loaded activity");
            StringAssert.Contains(text, "Partial comparison");
        }

        [TestMethod]
        [DataRow("path")]
        [DataRow("repository")]
        [DataRow("branch")]
        [DataRow("account-or-query")]
        public void DifferentWorkspaceOrIdentityCoverage_NeverReusesTheCheckpoint(string difference)
        {
            var previous = Capture(Pr());
            var current = Capture(Pr());
            switch (difference)
            {
                case "path": current.Path += "-clone"; break;
                case "repository": current.Repository += "-fork"; break;
                case "branch": current.Branch = "Fix"; break;
                default: current.Context += "-other"; break;
            }
            current.PullRequests[0].Checks = "failure";
            previous.PullRequests[0].Checks = "success";
            Assert.IsNull(WorkspaceVisitHistory.Compare(previous, current, false));
        }

        [TestMethod]
        public void ExplicitlyLinkedIssues_CompareOnlyLoadedKnownWorkflowStates()
        {
            var pr = Pr();
            pr.LinkedWorkItems["https://github.com/o/r/issues/42"] = "Task";
            var issue = new DevHubIssue { WebUrl = "https://github.com/o/r/issues/42", State = "open" };
            var unrelated = new DevHubIssue { WebUrl = "https://github.com/o/r/issues/43", State = "open" };
            var previous = WorkspaceVisitHistory.Capture(Item(), [pr], [issue, unrelated], "account|query", DateTime.UtcNow.AddDays(-1));
            Assert.HasCount(1, previous.Issues);
            issue.State = "closed";
            unrelated.State = "closed";
            var current = WorkspaceVisitHistory.Capture(Item(), [pr], [issue, unrelated], "account|query", DateTime.UtcNow);
            var text = WorkspaceVisitHistory.Compare(previous, current, false);
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "Linked work item: open -> closed");
            Assert.DoesNotContain("issues/43", text);
        }

        [TestMethod]
        public void ClosedPrs_AreComparedOnlyWhenExplicitlyPresentInCurrentData()
        {
            var previous = Capture(Pr());
            var merged = Pr();
            merged.Status = "merged";
            merged.IsMerged = true;
            var text = WorkspaceVisitHistory.Compare(previous, Capture(merged), false);
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "open -> Merged");
        }

        [TestMethod]
        public void Comparison_ContainsAtMostFourChangesPlusAnOverflowCount()
        {
            var old = Enumerable.Range(1, 10).Select(index =>
            {
                var pr = Pr();
                pr.WebUrl += index;
                pr.Number = "#" + index;
                return pr;
            }).ToArray();
            var previous = WorkspaceVisitHistory.Capture(Item(), old, [], "account|query", DateTime.UtcNow.AddDays(-1));
            foreach (var pr in old)
                pr.Status = "conflicts";
            var current = WorkspaceVisitHistory.Capture(Item(), old, [], "account|query", DateTime.UtcNow);
            var text = WorkspaceVisitHistory.Compare(previous, current, false);
            Assert.IsNotNull(text);
            StringAssert.Contains(text, "And 6 more changes");
            Assert.HasCount(6, text.Split([Environment.NewLine], StringSplitOptions.None));
        }

        [TestMethod]
        public async Task History_PersistsIndependentPathsAndIgnoresOutOfOrderWritesAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("visits.json");
            var current = Capture(Pr());
            var other = Capture(Pr());
            other.Path += "-worktree";
            await Task.WhenAll(WorkspaceVisitHistory.SaveAsync(path, current, CancellationToken.None),
                WorkspaceVisitHistory.SaveAsync(path, other, CancellationToken.None));
            var old = Capture(Pr());
            old.VisitedAtUtc = current.VisitedAtUtc.AddDays(-1);
            await WorkspaceVisitHistory.SaveAsync(path, old, CancellationToken.None);
            var entries = await WorkspaceVisitHistory.ReadAsync(path, CancellationToken.None);
            Assert.HasCount(2, entries);
            Assert.AreEqual(current.VisitedAtUtc, entries.Single(entry => entry.Path == current.Path).VisitedAtUtc);
        }

        [TestMethod]
        public async Task History_BoundsRetentionToFiveHundredWorkspacePathsAsync()
        {
            using var directory = new TemporaryDirectory();
            var path = directory.GetPath("visits.json");
            var entries = Enumerable.Range(0, 500).Select(index => new WorkspaceVisitHistory.Snapshot
            {
                Path = @"C:\Code\" + index, Context = "account|query", VisitedAtUtc = DateTime.UtcNow.AddDays(-1).AddMinutes(-index),
            }).ToArray();
            await AtomicJsonCache.WriteAsync(path, entries);
            var newest = Capture(Pr());
            await WorkspaceVisitHistory.SaveAsync(path, newest, CancellationToken.None);
            var retained = await WorkspaceVisitHistory.ReadAsync(path, CancellationToken.None);
            Assert.HasCount(500, retained);
            Assert.AreEqual(newest.Path, retained[0].Path);
            Assert.IsFalse(retained.Any(entry => entry.Path == @"C:\Code\499"));
        }

        private static WorkspaceVisitHistory.Snapshot Capture(DevHubPullRequest pr) =>
            WorkspaceVisitHistory.Capture(Item(), [pr], [], "account|query", DateTime.UtcNow);

        private static MruItem Item() => new()
        {
            Path = @"C:\Code\repo.sln", GitBranch = "fix", RemoteUrl = "https://github.com/o/r",
        };

        private static DevHubPullRequest Pr() => new()
        {
            SourceBranch = "fix", SourceRepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r"),
            Status = "open", Number = "#1", WebUrl = "https://github.com/o/r/pull/1",
        };
    }
}
