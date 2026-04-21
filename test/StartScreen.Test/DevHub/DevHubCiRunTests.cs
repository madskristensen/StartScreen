using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using System;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubCiRunTests
    {
        [TestMethod]
        public void StatusIcon_Success_ReturnsCheckmark()
        {
            var run = new DevHubCiRun { Status = "success" };

            Assert.AreEqual("\u2713", run.StatusIcon);
        }

        [TestMethod]
        public void StatusIcon_Failure_ReturnsCross()
        {
            var run = new DevHubCiRun { Status = "failure" };

            Assert.AreEqual("\u2717", run.StatusIcon);
        }

        [TestMethod]
        public void StatusIcon_Pending_ReturnsDot()
        {
            var run = new DevHubCiRun { Status = "pending" };

            Assert.AreEqual("\u25CF", run.StatusIcon);
        }

        [TestMethod]
        public void StatusIcon_Cancelled_ReturnsCircle()
        {
            var run = new DevHubCiRun { Status = "cancelled" };

            Assert.AreEqual("\u25CB", run.StatusIcon);
        }

        [TestMethod]
        public void StatusIcon_Skipped_ReturnsArrow()
        {
            var run = new DevHubCiRun { Status = "skipped" };

            Assert.AreEqual("\u2192", run.StatusIcon);
        }

        [TestMethod]
        public void StatusIcon_Unknown_ReturnsDot()
        {
            var run = new DevHubCiRun { Status = "something_else" };

            Assert.AreEqual("\u25CF", run.StatusIcon);
        }

        [TestMethod]
        public void TimestampAgoText_JustNow_ReturnsJustNow()
        {
            var run = new DevHubCiRun { Timestamp = DateTime.UtcNow.AddSeconds(-20) };

            Assert.AreEqual("just now", run.TimestampAgoText);
        }

        [TestMethod]
        public void TimestampAgoText_MinutesAgo_ReturnsMinutes()
        {
            var run = new DevHubCiRun { Timestamp = DateTime.UtcNow.AddMinutes(-45) };

            Assert.AreEqual("45m ago", run.TimestampAgoText);
        }

        [TestMethod]
        public void TimestampAgoText_HoursAgo_ReturnsHours()
        {
            var run = new DevHubCiRun { Timestamp = DateTime.UtcNow.AddHours(-2) };

            Assert.AreEqual("2h ago", run.TimestampAgoText);
        }

        [TestMethod]
        public void RepoFullName_WhenSet_ReturnsDisplayName()
        {
            var run = new DevHubCiRun { RepoDisplayName = "owner/repo" };

            Assert.AreEqual("owner/repo", run.RepoFullName);
        }

        [TestMethod]
        public void RepoFullName_WhenNull_ReturnsEmpty()
        {
            var run = new DevHubCiRun { RepoDisplayName = null };

            Assert.AreEqual(string.Empty, run.RepoFullName);
        }
    }
}
