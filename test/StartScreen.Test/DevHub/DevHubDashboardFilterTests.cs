using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using System;
using System.Collections.Generic;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubDashboardFilterTests
    {
        private static DevHubDashboard CreateTestDashboard()
        {
            var githubRepo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");
            var adoRepo = new RemoteRepoIdentifier("dev.azure.com", "myorg", "WebApp", "MyProject");
            var otherRepo = new RemoteRepoIdentifier("github.com", "madskristensen", "FileIcons");

            return new DevHubDashboard
            {
                FetchedAt = DateTime.UtcNow,
                Users = new List<DevHubUser>
                {
                    new DevHubUser { Username = "madskristensen", ProviderName = "GitHub" }
                },
                PullRequests = new List<DevHubPullRequest>
                {
                    new DevHubPullRequest
                    {
                        Title = "Fix null ref",
                        Number = "#142",
                        RepoIdentifier = githubRepo,
                        RepoDisplayName = "madskristensen/StartScreen",
                        UpdatedAt = DateTime.UtcNow.AddHours(-1)
                    },
                    new DevHubPullRequest
                    {
                        Title = "Add dark theme",
                        Number = "#89",
                        RepoIdentifier = otherRepo,
                        RepoDisplayName = "madskristensen/FileIcons",
                        UpdatedAt = DateTime.UtcNow.AddHours(-2)
                    },
                    new DevHubPullRequest
                    {
                        Title = "Sprint 4 changes",
                        Number = "!731",
                        RepoIdentifier = adoRepo,
                        RepoDisplayName = "myorg/MyProject/WebApp",
                        UpdatedAt = DateTime.UtcNow.AddDays(-1)
                    }
                },
                Issues = new List<DevHubIssue>
                {
                    new DevHubIssue
                    {
                        Title = "Crash on startup",
                        Number = "#201",
                        RepoIdentifier = githubRepo,
                        RepoDisplayName = "madskristensen/StartScreen",
                        CreatedAt = DateTime.UtcNow.AddDays(-3),
                        UpdatedAt = DateTime.UtcNow.AddDays(-2)
                    },
                    new DevHubIssue
                    {
                        Title = "Perf regression",
                        Number = "#44",
                        RepoIdentifier = adoRepo,
                        RepoDisplayName = "myorg/MyProject/WebApp",
                        CreatedAt = DateTime.UtcNow.AddHours(-12),
                        UpdatedAt = DateTime.UtcNow.AddDays(-1)
                    }
                },
                CiRuns = new List<DevHubCiRun>
                {
                    new DevHubCiRun
                    {
                        Name = "build.yaml",
                        Status = "failure",
                        RepoIdentifier = githubRepo,
                        RepoDisplayName = "madskristensen/StartScreen",
                        Timestamp = DateTime.UtcNow.AddMinutes(-45)
                    },
                    new DevHubCiRun
                    {
                        Name = "release.yaml",
                        Status = "success",
                        RepoIdentifier = githubRepo,
                        RepoDisplayName = "madskristensen/StartScreen",
                        Timestamp = DateTime.UtcNow.AddDays(-3)
                    }
                }
            };
        }

        [TestMethod]
        public void FilterByRepo_ReturnsOnlyMatchingPRs()
        {
            var dashboard = CreateTestDashboard();
            var repo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            var detail = dashboard.FilterByRepo(repo);

            Assert.AreEqual(1, detail.PullRequests.Count);
            Assert.AreEqual("#142", detail.PullRequests[0].Number);
        }

        [TestMethod]
        public void FilterByRepo_ReturnsOnlyMatchingIssues()
        {
            var dashboard = CreateTestDashboard();
            var repo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            var detail = dashboard.FilterByRepo(repo);

            Assert.AreEqual(1, detail.Issues.Count);
            Assert.AreEqual("#201", detail.Issues[0].Number);
        }

        [TestMethod]
        public void FilterByRepo_ReturnsOnlyMatchingCiRuns()
        {
            var dashboard = CreateTestDashboard();
            var repo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            var detail = dashboard.FilterByRepo(repo);

            Assert.AreEqual(2, detail.CiRuns.Count);
        }

        [TestMethod]
        public void FilterByRepo_AdoRepo_ReturnsCorrectData()
        {
            var dashboard = CreateTestDashboard();
            var repo = new RemoteRepoIdentifier("dev.azure.com", "myorg", "WebApp", "MyProject");

            var detail = dashboard.FilterByRepo(repo);

            Assert.AreEqual(1, detail.PullRequests.Count);
            Assert.AreEqual("!731", detail.PullRequests[0].Number);
            Assert.AreEqual(1, detail.Issues.Count);
            Assert.AreEqual("#44", detail.Issues[0].Number);
            Assert.AreEqual(0, detail.CiRuns.Count);
        }

        [TestMethod]
        public void FilterByRepo_NoMatchingData_ReturnsEmptyDetail()
        {
            var dashboard = CreateTestDashboard();
            var unknownRepo = new RemoteRepoIdentifier("github.com", "someone", "UnknownRepo");

            var detail = dashboard.FilterByRepo(unknownRepo);

            Assert.IsNotNull(detail);
            Assert.AreEqual(0, detail.PullRequests.Count);
            Assert.AreEqual(0, detail.Issues.Count);
            Assert.AreEqual(0, detail.CiRuns.Count);
            Assert.IsFalse(detail.HasData);
        }

        [TestMethod]
        public void FilterByRepo_Null_ReturnsNull()
        {
            var dashboard = CreateTestDashboard();

            Assert.IsNull(dashboard.FilterByRepo(null));
        }

        [TestMethod]
        public void FilterByRepo_OrdersByTimestampDescending()
        {
            var dashboard = CreateTestDashboard();
            var repo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            var detail = dashboard.FilterByRepo(repo);

            // CI runs should be ordered newest first
            Assert.IsTrue(detail.CiRuns[0].Timestamp > detail.CiRuns[1].Timestamp);
        }

        [TestMethod]
        public void FilterByRepo_IssuesOrderedByCreatedAtDescending()
        {
            var githubRepo = new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

            var dashboard = new DevHubDashboard
            {
                FetchedAt = DateTime.UtcNow,
                Users = new List<DevHubUser>(),
                PullRequests = new List<DevHubPullRequest>(),
                Issues = new List<DevHubIssue>
                {
                    new DevHubIssue
                    {
                        Title = "Older issue",
                        Number = "#1",
                        RepoIdentifier = githubRepo,
                        RepoDisplayName = "madskristensen/StartScreen",
                        CreatedAt = DateTime.UtcNow.AddDays(-5),
                        UpdatedAt = DateTime.UtcNow
                    },
                    new DevHubIssue
                    {
                        Title = "Newer issue",
                        Number = "#2",
                        RepoIdentifier = githubRepo,
                        RepoDisplayName = "madskristensen/StartScreen",
                        CreatedAt = DateTime.UtcNow.AddDays(-1),
                        UpdatedAt = DateTime.UtcNow.AddDays(-3)
                    }
                },
                CiRuns = new List<DevHubCiRun>()
            };

            var detail = dashboard.FilterByRepo(githubRepo);

            Assert.AreEqual("Newer issue", detail.Issues[0].Title);
            Assert.AreEqual("Older issue", detail.Issues[1].Title);
        }

        [TestMethod]
        public void HasAuthentication_WithUsers_ReturnsTrue()
        {
            var dashboard = CreateTestDashboard();

            Assert.IsTrue(dashboard.HasAuthentication);
        }

        [TestMethod]
        public void HasAuthentication_Empty_ReturnsFalse()
        {
            var dashboard = new DevHubDashboard();

            Assert.IsFalse(dashboard.HasAuthentication);
        }

        [TestMethod]
        public void HasData_WithPRs_ReturnsTrue()
        {
            var dashboard = CreateTestDashboard();

            Assert.IsTrue(dashboard.HasData);
        }

        [TestMethod]
        public void HasData_Empty_ReturnsFalse()
        {
            var dashboard = new DevHubDashboard();

            Assert.IsFalse(dashboard.HasData);
        }

        [TestMethod]
        public void TotalPullRequests_ReturnsCount()
        {
            var dashboard = CreateTestDashboard();

            Assert.AreEqual(3, dashboard.TotalPullRequests);
        }

        [TestMethod]
        public void TotalIssues_ReturnsCount()
        {
            var dashboard = CreateTestDashboard();

            Assert.AreEqual(2, dashboard.TotalIssues);
        }

        [TestMethod]
        public void FailedCiRuns_ReturnsOnlyFailures()
        {
            var dashboard = CreateTestDashboard();

            Assert.AreEqual(1, dashboard.FailedCiRuns);
        }
    }
}
