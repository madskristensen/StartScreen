using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using System;
using System.Collections.Generic;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubIssueTests
    {
        [TestMethod]
        public void CreatedAgoText_JustNow_ReturnsJustNow()
        {
            var issue = new DevHubIssue { CreatedAt = DateTime.UtcNow.AddSeconds(-10) };

            Assert.AreEqual("just now", issue.CreatedAgoText);
        }

        [TestMethod]
        public void CreatedAgoText_HoursAgo_ReturnsHours()
        {
            var issue = new DevHubIssue { CreatedAt = DateTime.UtcNow.AddHours(-5) };

            Assert.AreEqual("5h ago", issue.CreatedAgoText);
        }

        [TestMethod]
        public void CreatedAgoText_DaysAgo_ReturnsDays()
        {
            var issue = new DevHubIssue { CreatedAt = DateTime.UtcNow.AddDays(-2) };

            Assert.AreEqual("2d ago", issue.CreatedAgoText);
        }

        [TestMethod]
        public void LabelText_NoLabels_ReturnsEmpty()
        {
            var issue = new DevHubIssue();

            Assert.AreEqual(string.Empty, issue.LabelText);
        }

        [TestMethod]
        public void LabelText_SingleLabel_ReturnsLabel()
        {
            var issue = new DevHubIssue
            {
                Labels = new List<DevHubLabel>
                {
                    new DevHubLabel { Name = "bug", Color = "d73a4a" }
                }
            };

            Assert.AreEqual("bug", issue.LabelText);
        }

        [TestMethod]
        public void LabelText_MultipleLabels_ReturnsCommaSeparated()
        {
            var issue = new DevHubIssue
            {
                Labels = new List<DevHubLabel>
                {
                    new DevHubLabel { Name = "bug", Color = "d73a4a" },
                    new DevHubLabel { Name = "P1", Color = "e4e669" },
                    new DevHubLabel { Name = "area-ui", Color = "0075ca" }
                }
            };

            Assert.AreEqual("bug, P1, area-ui", issue.LabelText);
        }

        [TestMethod]
        public void RepoFullName_WhenSet_ReturnsDisplayName()
        {
            var issue = new DevHubIssue { RepoDisplayName = "owner/repo" };

            Assert.AreEqual("owner/repo", issue.RepoFullName);
        }

        [TestMethod]
        public void RepoFullName_WhenNull_ReturnsEmpty()
        {
            var issue = new DevHubIssue { RepoDisplayName = null };

            Assert.AreEqual(string.Empty, issue.RepoFullName);
        }
    }
}
