using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubPullRequestTests
    {
        [TestMethod]
        public void UpdatedAgoText_JustNow_ReturnsJustNow()
        {
            var pr = new DevHubPullRequest { UpdatedAt = DateTime.UtcNow.AddSeconds(-30) };

            Assert.AreEqual("just now", pr.UpdatedAgoText);
        }

        [TestMethod]
        public void UpdatedAgoText_MinutesAgo_ReturnsMinutes()
        {
            var pr = new DevHubPullRequest { UpdatedAt = DateTime.UtcNow.AddMinutes(-45) };

            Assert.AreEqual("45m ago", pr.UpdatedAgoText);
        }

        [TestMethod]
        public void UpdatedAgoText_HoursAgo_ReturnsHours()
        {
            var pr = new DevHubPullRequest { UpdatedAt = DateTime.UtcNow.AddHours(-3) };

            Assert.AreEqual("3h ago", pr.UpdatedAgoText);
        }

        [TestMethod]
        public void UpdatedAgoText_DaysAgo_ReturnsDays()
        {
            var pr = new DevHubPullRequest { UpdatedAt = DateTime.UtcNow.AddDays(-4) };

            Assert.AreEqual("4d ago", pr.UpdatedAgoText);
        }

        [TestMethod]
        public void UpdatedAgoText_WeeksAgo_ReturnsWeeks()
        {
            var pr = new DevHubPullRequest { UpdatedAt = DateTime.UtcNow.AddDays(-14) };

            Assert.AreEqual("2w ago", pr.UpdatedAgoText);
        }

        [TestMethod]
        public void UpdatedAgoText_MonthsAgo_ReturnsDate()
        {
            var pr = new DevHubPullRequest { UpdatedAt = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc) };

            Assert.AreEqual("Jan 15", pr.UpdatedAgoText);
        }

        [TestMethod]
        [DataRow(0, "today")]
        [DataRow(1, "yesterday")]
        [DataRow(4, "Nov 1")]
        [DataRow(365, "Nov 5, 2025")]
        [DataRow(-1, "Nov 6")]
        public void ActivityDate_UsesFriendlyLocalCalendarDates(int daysAgo, string expected)
        {
            var now = new DateTime(2026, 11, 5, 0, 15, 0, DateTimeKind.Local);
            var updated = now.Date.AddDays(-daysAgo).AddHours(23);

            Assert.AreEqual(expected, DevHubPullRequest.FormatActivityDate(updated, now));
            Assert.AreEqual(expected, DevHubPullRequest.FormatActivityDate(updated.ToUniversalTime(), now.ToUniversalTime()));
        }

        [TestMethod]
        public void ActivityDate_IsSharedByOwnAndIncomingPrsAndExposesExactTime()
        {
            var pr = new DevHubPullRequest
            {
                Status = "open",
                IsAuthoredByCurrentUser = true,
                UpdatedAt = new DateTime(2025, 11, 1, 18, 30, 0, DateTimeKind.Utc),
                WebUrl = "https://github.com/o/r/pull/1",
            };
            var item = DevHubWorkItem.GetOwnPullRequests([pr]).Single();

            Assert.AreEqual(pr.ActivityDateText, item.ActivityDateText);
            Assert.AreEqual(pr.UpdatedAt.ToLocalTime().ToString("f"), pr.ActivityDateToolTip);
            Assert.AreEqual(pr.ActivityDateToolTip, item.ActivityDateToolTip);
            Assert.AreEqual("today", new DevHubPullRequest { UpdatedAt = DateTime.UtcNow }.ActivityDateText);
        }

        [TestMethod]
        public void ActivityDate_UnknownTimestampHasNoMisleadingDate()
        {
            var pr = new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, WebUrl = "https://github.com/o/r/pull/1" };
            var item = DevHubWorkItem.GetOwnPullRequests([pr]).Single();

            Assert.AreEqual(string.Empty, pr.ActivityDateText);
            Assert.AreEqual(string.Empty, pr.ActivityDateToolTip);
            Assert.AreEqual(string.Empty, item.ActivityDateText);
            Assert.AreEqual(string.Empty, item.ActivityDateToolTip);
        }

        [TestMethod]
        public void CiStatusIcon_Success_ReturnsCheckmark()
        {
            var pr = new DevHubPullRequest { CiStatus = "success" };

            Assert.AreEqual("\u2713", pr.CiStatusIcon);
        }

        [TestMethod]
        public void CiStatusIcon_Failure_ReturnsCross()
        {
            var pr = new DevHubPullRequest { CiStatus = "failure" };

            Assert.AreEqual("\u2717", pr.CiStatusIcon);
        }

        [TestMethod]
        public void CiStatusIcon_Pending_ReturnsDot()
        {
            var pr = new DevHubPullRequest { CiStatus = "pending" };

            Assert.AreEqual("\u25CF", pr.CiStatusIcon);
        }

        [TestMethod]
        public void CiStatusIcon_Null_ReturnsEmpty()
        {
            var pr = new DevHubPullRequest { CiStatus = null };

            Assert.AreEqual(string.Empty, pr.CiStatusIcon);
        }

        [TestMethod]
        public void RepoFullName_WhenSet_ReturnsDisplayName()
        {
            var pr = new DevHubPullRequest { RepoDisplayName = "owner/repo" };

            Assert.AreEqual("owner/repo", pr.RepoFullName);
        }

        [TestMethod]
        public void RepoFullName_WhenNull_ReturnsEmpty()
        {
            var pr = new DevHubPullRequest { RepoDisplayName = null };

            Assert.AreEqual(string.Empty, pr.RepoFullName);
        }

        [TestMethod]
        public void IsDraft_WhenStatusIsDraft_ReturnsTrue()
        {
            var pr = new DevHubPullRequest { Status = "draft" };

            Assert.IsTrue(pr.IsDraft);
        }

        [TestMethod]
        public void IsDraft_WhenStatusIsOpen_ReturnsFalse()
        {
            var pr = new DevHubPullRequest { Status = "open" };

            Assert.IsFalse(pr.IsDraft);
        }

        [TestMethod]
        [DataRow("open", null, 0, false, "Waiting for review")]
        [DataRow("open", null, 1, false, "1 approval")]
        [DataRow("open", "success", 2, false, "2 approvals")]
        [DataRow("open", "pending", 1, false, "Checks pending")]
        [DataRow("open", "failure", 1, false, "Checks failing")]
        [DataRow("conflicts", "success", 1, false, "Conflicts")]
        [DataRow("draft", null, 0, false, "Draft")]
        [DataRow("draft", null, 0, true, "Draft")]
        [DataRow("open", null, 0, true, "Review requested")]
        public void StatusText_IsConsistentForOwnAndIncomingPullRequests(string status, string? ciStatus,
            int approvals, bool reviewRequested, string expected)
        {
            var pr = new DevHubPullRequest
            {
                Status = status,
                CiStatus = ciStatus,
                ApprovalCount = approvals,
                IsReviewRequestedForCurrentUser = reviewRequested,
            };

            Assert.AreEqual(expected, pr.StatusText);
            pr.IsAuthoredByCurrentUser = true;
            Assert.AreEqual(expected, pr.StatusText);
        }

        [TestMethod]
        public void BranchDisplayText_WithBothBranches_ReturnsArrowFormat()
        {
            var pr = new DevHubPullRequest { SourceBranch = "feature/test", TargetBranch = "main" };

            Assert.AreEqual("feature/test \u2192 main", pr.BranchDisplayText);
        }

        [TestMethod]
        public void BranchDisplayText_WithOnlySource_ReturnsSourceOnly()
        {
            var pr = new DevHubPullRequest { SourceBranch = "feature/test", TargetBranch = null };

            Assert.AreEqual("feature/test", pr.BranchDisplayText);
        }

        [TestMethod]
        public void BranchDisplayText_WithNoSource_ReturnsNull()
        {
            var pr = new DevHubPullRequest { SourceBranch = null, TargetBranch = "main" };

            Assert.IsNull(pr.BranchDisplayText);
        }
    }
}
