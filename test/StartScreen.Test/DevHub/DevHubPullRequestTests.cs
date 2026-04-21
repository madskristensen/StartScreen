using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using System;

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
    }
}
