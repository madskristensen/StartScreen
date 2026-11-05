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

        [TestMethod]
        public void MergeIssues_PrefersRepositoryMetadataWithoutCollapsingUnknownUrls()
        {
            var aggregate = new DevHubIssue { WebUrl = "https://dev.azure.com/org/project/_workitems/edit/1" };
            var linked = new DevHubIssue { WebUrl = aggregate.WebUrl.ToUpperInvariant(), RepoIdentifier = _scopedRepo };
            var firstUnknown = new DevHubIssue { Title = "First unknown" };
            var secondUnknown = new DevHubIssue { Title = "Second unknown" };

            var merged = DevHubService.MergeIssues([aggregate, firstUnknown, secondUnknown], [linked]).ToList();

            Assert.HasCount(3, merged);
            Assert.AreSame(linked, merged[0]);
            CollectionAssert.Contains(merged, firstUnknown);
            CollectionAssert.Contains(merged, secondUnknown);
        }

        [TestMethod]
        public void MergeRepoDetail_WarningsRemainScopedAndClearAfterRecovery()
        {
            var dashboard = new DevHubDashboard();
            var detail = new DevHubRepoDetail { RepoIdentifier = _scopedRepo, ActivityWarning = "Issue access unavailable." };
            DevHubService.MergeRepoDetail(dashboard, _scopedRepo, detail);

            Assert.AreEqual(detail.ActivityWarning, dashboard.FilterByRepo(_scopedRepo).ActivityWarning);
            Assert.IsNull(dashboard.FilterByRepo(_otherRepo).ActivityWarning);
            var equivalentRepo = new RemoteRepoIdentifier("GITHUB.COM", "MADSKRISTENSEN", "STARTSCREEN");
            Assert.AreEqual(detail.ActivityWarning, dashboard.FilterByRepo(equivalentRepo).ActivityWarning);

            DevHubService.MergeRepoDetail(dashboard, equivalentRepo, new DevHubRepoDetail { RepoIdentifier = equivalentRepo });

            Assert.IsEmpty(dashboard.ActivityWarnings);
            Assert.IsNull(dashboard.FilterByRepo(_scopedRepo).ActivityWarning);
        }
    }
}
