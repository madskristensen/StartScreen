using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubScopeMergeTests
    {
        private static readonly RemoteRepoIdentifier ScopedRepo =
            new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

        private static readonly RemoteRepoIdentifier OtherRepo =
            new RemoteRepoIdentifier("github.com", "madskristensen", "FileIcons");

        [TestMethod]
        public void MergeRepoDetail_ReplacesItemsForScopedRepo()
        {
            var dashboard = new DevHubDashboard
            {
                PullRequests = new List<DevHubPullRequest>
                {
                    new DevHubPullRequest { Number = "#1", RepoIdentifier = ScopedRepo },
                    new DevHubPullRequest { Number = "#2", RepoIdentifier = OtherRepo },
                },
            };

            var detail = new DevHubRepoDetail
            {
                RepoIdentifier = ScopedRepo,
                PullRequests = new List<DevHubPullRequest>
                {
                    new DevHubPullRequest { Number = "#10", RepoIdentifier = ScopedRepo },
                    new DevHubPullRequest { Number = "#11", RepoIdentifier = ScopedRepo },
                    new DevHubPullRequest { Number = "#12", RepoIdentifier = ScopedRepo },
                },
            };

            DevHubService.MergeRepoDetail(dashboard, ScopedRepo, detail);

            var scoped = dashboard.PullRequests.Where(p => ScopedRepo.Equals(p.RepoIdentifier)).ToList();
            Assert.AreEqual(3, scoped.Count, "Scoped repo PRs should be replaced with the fresh fuller set.");
            CollectionAssert.AreEquivalent(new[] { "#10", "#11", "#12" }, scoped.Select(p => p.Number).ToList());
        }

        [TestMethod]
        public void MergeRepoDetail_PreservesOtherRepoItems()
        {
            var dashboard = new DevHubDashboard
            {
                Issues = new List<DevHubIssue>
                {
                    new DevHubIssue { Number = "#1", RepoIdentifier = ScopedRepo },
                    new DevHubIssue { Number = "#2", RepoIdentifier = OtherRepo },
                },
            };

            var detail = new DevHubRepoDetail
            {
                RepoIdentifier = ScopedRepo,
                Issues = new List<DevHubIssue>
                {
                    new DevHubIssue { Number = "#10", RepoIdentifier = ScopedRepo },
                },
            };

            DevHubService.MergeRepoDetail(dashboard, ScopedRepo, detail);

            Assert.IsTrue(dashboard.Issues.Any(i => i.Number == "#2" && OtherRepo.Equals(i.RepoIdentifier)),
                "Items belonging to other repos must be preserved.");
        }

        [TestMethod]
        public void MergeRepoDetail_AddsRepoNotPreviouslyPresent()
        {
            var dashboard = new DevHubDashboard
            {
                CiRuns = new List<DevHubCiRun>
                {
                    new DevHubCiRun { Name = "build-other", RepoIdentifier = OtherRepo },
                },
            };

            var detail = new DevHubRepoDetail
            {
                RepoIdentifier = ScopedRepo,
                CiRuns = new List<DevHubCiRun>
                {
                    new DevHubCiRun { Name = "build-scoped", RepoIdentifier = ScopedRepo },
                },
            };

            DevHubService.MergeRepoDetail(dashboard, ScopedRepo, detail);

            Assert.AreEqual(2, dashboard.CiRuns.Count);
            Assert.IsTrue(dashboard.CiRuns.Any(c => c.Name == "build-scoped"));
            Assert.IsTrue(dashboard.CiRuns.Any(c => c.Name == "build-other"));
        }

        [TestMethod]
        public void MergeRepoDetail_EmptyDetailClearsScopedRepoItems()
        {
            var dashboard = new DevHubDashboard
            {
                PullRequests = new List<DevHubPullRequest>
                {
                    new DevHubPullRequest { Number = "#1", RepoIdentifier = ScopedRepo },
                    new DevHubPullRequest { Number = "#2", RepoIdentifier = OtherRepo },
                },
            };

            var detail = new DevHubRepoDetail { RepoIdentifier = ScopedRepo };

            DevHubService.MergeRepoDetail(dashboard, ScopedRepo, detail);

            Assert.IsFalse(dashboard.PullRequests.Any(p => ScopedRepo.Equals(p.RepoIdentifier)));
            Assert.IsTrue(dashboard.PullRequests.Any(p => OtherRepo.Equals(p.RepoIdentifier)));
        }

        [TestMethod]
        public void ScopedRepoItemCount_IsLargerThanDefaultDashboardFetch()
        {
            // Sanity check that scoping fetches a fuller set than a normal refresh.
            Assert.IsTrue(DevHubService.ScopedRepoItemCount >= 20);
        }
    }
}
