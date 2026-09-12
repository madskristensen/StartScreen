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
        private static readonly RemoteRepoIdentifier _scopedRepo =
            new RemoteRepoIdentifier("github.com", "madskristensen", "StartScreen");

        private static readonly RemoteRepoIdentifier _otherRepo =
            new RemoteRepoIdentifier("github.com", "madskristensen", "FileIcons");

        [TestMethod]
        public void MergeRepoDetail_ReplacesItemsForScopedRepo()
        {
            var dashboard = new DevHubDashboard
            {
                PullRequests = new List<DevHubPullRequest>
                {
                    new DevHubPullRequest { Number = "#1", RepoIdentifier = _scopedRepo },
                    new DevHubPullRequest { Number = "#2", RepoIdentifier = _otherRepo },
                },
            };

            var detail = new DevHubRepoDetail
            {
                RepoIdentifier = _scopedRepo,
                PullRequests = new List<DevHubPullRequest>
                {
                    new DevHubPullRequest { Number = "#10", RepoIdentifier = _scopedRepo },
                    new DevHubPullRequest { Number = "#11", RepoIdentifier = _scopedRepo },
                    new DevHubPullRequest { Number = "#12", RepoIdentifier = _scopedRepo },
                },
            };

            DevHubService.MergeRepoDetail(dashboard, _scopedRepo, detail);

            var scoped = dashboard.PullRequests.Where(p => _scopedRepo.Equals(p.RepoIdentifier)).ToList();
            Assert.HasCount(3, scoped, "Scoped repo PRs should be replaced with the fresh fuller set.");
            CollectionAssert.AreEquivalent(new[] { "#10", "#11", "#12" }, scoped.Select(p => p.Number).ToList());
        }

        [TestMethod]
        public void MergeRepoDetail_PreservesOtherRepoItems()
        {
            var dashboard = new DevHubDashboard
            {
                Issues = new List<DevHubIssue>
                {
                    new DevHubIssue { Number = "#1", RepoIdentifier = _scopedRepo },
                    new DevHubIssue { Number = "#2", RepoIdentifier = _otherRepo },
                },
            };

            var detail = new DevHubRepoDetail
            {
                RepoIdentifier = _scopedRepo,
                Issues = new List<DevHubIssue>
                {
                    new DevHubIssue { Number = "#10", RepoIdentifier = _scopedRepo },
                },
            };

            DevHubService.MergeRepoDetail(dashboard, _scopedRepo, detail);

            Assert.IsTrue(dashboard.Issues.Any(i => i.Number == "#2" && _otherRepo.Equals(i.RepoIdentifier)),
                "Items belonging to other repos must be preserved.");
        }

        [TestMethod]
        public void MergeRepoDetail_AddsRepoNotPreviouslyPresent()
        {
            var dashboard = new DevHubDashboard
            {
                CiRuns = new List<DevHubCiRun>
                {
                    new DevHubCiRun { Name = "build-other", RepoIdentifier = _otherRepo },
                },
            };

            var detail = new DevHubRepoDetail
            {
                RepoIdentifier = _scopedRepo,
                CiRuns = new List<DevHubCiRun>
                {
                    new DevHubCiRun { Name = "build-scoped", RepoIdentifier = _scopedRepo },
                },
            };

            DevHubService.MergeRepoDetail(dashboard, _scopedRepo, detail);

            Assert.HasCount(2, dashboard.CiRuns);
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
                    new DevHubPullRequest { Number = "#1", RepoIdentifier = _scopedRepo },
                    new DevHubPullRequest { Number = "#2", RepoIdentifier = _otherRepo },
                },
            };

            var detail = new DevHubRepoDetail { RepoIdentifier = _scopedRepo };

            DevHubService.MergeRepoDetail(dashboard, _scopedRepo, detail);

            Assert.IsFalse(dashboard.PullRequests.Any(p => _scopedRepo.Equals(p.RepoIdentifier)));
            Assert.IsTrue(dashboard.PullRequests.Any(p => _otherRepo.Equals(p.RepoIdentifier)));
        }

    }
}
