using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubAttentionPolicyTests
    {
        [TestMethod]
        [DataRow(-1L, false)]
        [DataRow(0L, true)]
        [DataRow(1L, true)]
        public void RecentActivity_IncludesTheExactThirtyDayBoundary(long offsetTicks, bool expected)
        {
            var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(expected, DevHubAttentionPolicy.HasRecentActivity(
                now.AddDays(-30).AddTicks(offsetTicks), now.AddMonths(-5), now));
        }

        [TestMethod]
        public void RecentActivity_UsesCreationOnlyWhenUpdateIsUnknown()
        {
            var now = DateTime.UtcNow;
            Assert.IsTrue(DevHubAttentionPolicy.HasRecentActivity(default, now.AddDays(-1), now));
            Assert.IsFalse(DevHubAttentionPolicy.HasRecentActivity(default, now.AddMonths(-5), now));
            Assert.IsFalse(DevHubAttentionPolicy.HasRecentActivity(default, default, now));
            Assert.IsFalse(DevHubAttentionPolicy.HasRecentActivity(now.AddMonths(-5), now, now));
        }

        [TestMethod]
        [DataRow(1, false)]
        [DataRow(1, true)]
        [DataRow(150, false)]
        [DataRow(150, true)]
        public void AssignedPullRequests_StayInPrInventoryButRequireRecentActionForAttention(int daysAgo, bool failing)
        {
            var pr = new DevHubPullRequest
            {
                Status = "open", IsAssignedToCurrentUser = true, CiStatus = failing ? "failure" : "pending",
                CreatedAt = DateTime.UtcNow.AddMonths(-6), UpdatedAt = DateTime.UtcNow.AddDays(-daysAgo),
                WebUrl = "https://github.com/o/r/pull/1",
            };
            var mine = DevHubWorkItem.GetMyWork([pr], [], []);
            var card = DevHubWorkItem.Filter(mine, DevHubWorkItemKind.PullRequest, false).Single();
            Assert.IsTrue(card.IsAssignedToCurrentUser);
            Assert.IsTrue(card.IsPullRequest);
            Assert.AreEqual(failing && daysAgo < 30, card.NeedsAttention);
            Assert.IsEmpty(DevHubWorkItem.GetIncoming([pr], [], []));
            Assert.HasCount(card.NeedsAttention ? 1 : 0,
                DevHubWorkItem.Filter(mine, DevHubWorkItemKind.All, true));
        }

        [TestMethod]
        [DataRow(1, true)]
        [DataRow(150, false)]
        public void RequestedReviews_StayInPrInventoryWhenTheyAgeOutOfAttention(int daysAgo, bool expected)
        {
            var pr = new DevHubPullRequest
            {
                Status = "open", IsReviewRequestedForCurrentUser = true,
                UpdatedAt = DateTime.UtcNow.AddDays(-daysAgo), WebUrl = "https://github.com/o/r/pull/1",
            };
            var mine = DevHubWorkItem.GetMyWork([pr], [], []);
            Assert.HasCount(1, DevHubWorkItem.Filter(mine, DevHubWorkItemKind.PullRequest, false));
            Assert.AreEqual(expected, mine.Single().NeedsAttention);
            Assert.IsEmpty(DevHubWorkItem.GetIncoming([pr], [], []));
        }

        [TestMethod]
        public void AssignedReviewRequest_KeepsItsExplicitReviewAction()
        {
            var pr = new DevHubPullRequest
            {
                Status = "open", IsAssignedToCurrentUser = true, IsReviewRequestedForCurrentUser = true,
                UpdatedAt = DateTime.UtcNow,
            };
            Assert.AreEqual("Review requested", pr.AttentionReason);
            Assert.AreEqual("Review this PR", pr.NextActionText);
            pr.IsReviewRequestedForCurrentUser = false;
            Assert.IsFalse(pr.NeedsAttention);
            pr.CiStatus = "failure";
            Assert.AreEqual("Checks failing", pr.AttentionReason);
            Assert.AreEqual("Inspect failing checks", pr.NextActionText);
            pr.IsAssignedToCurrentUser = false;
            Assert.IsFalse(pr.NeedsAttention);
        }

        [TestMethod]
        [DataRow(1, false, true)]
        [DataRow(1, true, true)]
        [DataRow(150, false, false)]
        [DataRow(150, true, false)]
        public void AssignedIssues_RequireRecentActivityEvenWhenBlocked(int daysAgo, bool blocked, bool expected)
        {
            var issue = new DevHubIssue
            {
                State = "open", IsAssignedToCurrentUser = true, IsBlocked = blocked,
                UpdatedAt = DateTime.UtcNow.AddDays(-daysAgo), WebUrl = "https://github.com/o/r/issues/5",
            };
            var mine = DevHubWorkItem.GetMyWork([], [issue], []);
            Assert.HasCount(1, DevHubWorkItem.Filter(mine, DevHubWorkItemKind.Issue, false));
            Assert.AreEqual(expected, mine.Single().NeedsAttention);
            issue.IsClosed = true;
            Assert.IsFalse(issue.NeedsAttention);
        }

        [TestMethod]
        public void MissingDates_DoNotInventAttentionForPersonalItems()
        {
            Assert.IsFalse(new DevHubPullRequest
            {
                Status = "open", IsAuthoredByCurrentUser = true, HasChangesRequested = true,
            }.NeedsAttention);
            Assert.IsFalse(new DevHubIssue
            {
                State = "open", IsAssignedToCurrentUser = true,
            }.NeedsAttention);
        }

        [TestMethod]
        public void RepositoryRefresh_PreservesAndMergesAssignedPullRequests()
        {
            var repo = new RemoteRepoIdentifier("github.com", "o", "r");
            var assigned = new DevHubPullRequest
            {
                RepoIdentifier = repo, Status = "open", IsAssignedToCurrentUser = true,
                WebUrl = "https://github.com/o/r/pull/1",
            };
            var dashboard = new DevHubDashboard { PullRequests = [assigned] };
            DevHubService.MergeRepoDetail(dashboard, repo, new DevHubRepoDetail());
            Assert.AreSame(assigned, dashboard.PullRequests.Single());

            var repositoryVersion = new DevHubPullRequest { WebUrl = assigned.WebUrl };
            Assert.AreSame(repositoryVersion, DevHubService.MergePullRequests([assigned], [repositoryVersion]).Single());
            Assert.IsTrue(repositoryVersion.IsAssignedToCurrentUser);
        }
    }
}