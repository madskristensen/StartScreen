using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StartScreen.Models.DevHub;
using StartScreen.Services.DevHub;
using StartScreen.ToolWindows.Controls;

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
        public void MergeActivity_CloudUrlAliasesProduceOneAttentionCardPerItem()
        {
            var legacyRepo = RemoteRepoIdentifier.TryParse("https://devdiv.visualstudio.com/DevDiv/_git/VSEditor");
            var modernRepo = RemoteRepoIdentifier.TryParse("https://dev.azure.com/devdiv/DevDiv/_git/VSEditor");
            var personalPr = new DevHubPullRequest
            {
                RepoIdentifier = modernRepo, NumericId = 791225, IsAuthoredByCurrentUser = true,
                WebUrl = "https://dev.azure.com/devdiv/DevDiv/_git/VSEditor/pullrequest/791225",
            };
            var repositoryPr = new DevHubPullRequest
            {
                RepoIdentifier = legacyRepo, NumericId = 791225, Status = "open", CiStatus = "failure", UpdatedAt = DateTime.UtcNow,
                WebUrl = "https://devdiv.visualstudio.com/DevDiv/_git/VSEditor/pullrequest/791225",
            };
            var personalIssue = new DevHubIssue
            {
                RepoIdentifier = modernRepo, NumericId = 3078702, IsAssignedToCurrentUser = true,
                WebUrl = "https://dev.azure.com/devdiv/DevDiv/_workitems/edit/3078702",
            };
            var repositoryIssue = new DevHubIssue
            {
                RepoIdentifier = legacyRepo, NumericId = 3078702, State = "Resolved", UpdatedAt = DateTime.UtcNow,
                WebUrl = "https://devdiv.visualstudio.com/DevDiv/_workitems/edit/3078702",
            };

            var prs = DevHubService.MergePullRequests([personalPr], [repositoryPr]).ToList();
            var issues = DevHubService.MergeIssues([personalIssue], [repositoryIssue]).ToList();
            var attention = DevHubWorkItem.Filter(DevHubWorkItem.GetMyWork(prs, issues, []), DevHubWorkItemKind.All, true);

            Assert.AreSame(repositoryPr, prs.Single());
            Assert.IsTrue(prs.Single().IsAuthoredByCurrentUser);
            Assert.AreSame(repositoryIssue, issues.Single());
            Assert.IsTrue(issues.Single().IsAssignedToCurrentUser);
            Assert.HasCount(2, attention);
        }

        [TestMethod]
        [DataRow("https://dev.azure.com/devdiv/DevDiv", "https://dev.azure.com/devdiv/DevDiv/_git/WebTools")]
        [DataRow("https://devdiv.visualstudio.com/DevDiv", "https://dev.azure.com/devdiv/DevDiv/_git/WebTools")]
        [DataRow("https://devdiv.visualstudio.com/DefaultCollection/DevDiv", "https://dev.azure.com/devdiv/OtherProject/_git/WebTools")]
        [DataRow("https://dev.azure.com/devdiv/DevDiv", "https://devdiv.visualstudio.com/DevDiv/_git/WebTools")]
        [DataRow("https://tfs.contoso.com:8080/tfs/Collection/DevDiv", "https://tfs.contoso.com:8080/tfs/Collection/DevDiv/_git/WebTools")]
        [DataRow("http://tfs.contoso.com:8080/Collection/DevDiv", "http://tfs.contoso.com:8080/Collection/OtherProject/_git/WebTools")]
        public void MergeIssues_ProjectWideAndRepositoryActivityProduceOneAttentionCardPerWorkItem(string projectUrl, string remoteUrl)
        {
            var repo = RemoteRepoIdentifier.TryParse(remoteUrl);
            var personal = new List<DevHubIssue>();
            var linked = new List<DevHubIssue>();
            foreach (var id in new[] { 3106859, 3106860, 3105159 })
            {
                personal.Add(new DevHubIssue
                {
                    NumericId = id, Number = $"#{id}", Title = $"Task {id}", State = "Active",
                    IsAssignedToCurrentUser = true, IsAuthoredByCurrentUser = true, IsMentioned = true,
                    UpdatedAt = DateTime.UtcNow, RepoDisplayName = "devdiv/DevDiv",
                    WebUrl = $"{projectUrl}/_workitems/edit/{id}",
                });
                linked.Add(new DevHubIssue
                {
                    NumericId = id, Number = $"#{id}", Title = $"Task {id}", State = "Active",
                    RepoIdentifier = repo, RepoDisplayName = repo.DisplayName, UpdatedAt = DateTime.UtcNow,
                    WebUrl = $"{repo.BaseUrl}/{repo.Project}/_workitems/edit/{id}",
                });
            }

            var issues = DevHubService.MergeIssues(personal, linked).ToList();
            CollectionAssert.AreEqual(linked, issues);
            Assert.IsTrue(issues.All(issue => issue.IsAssignedToCurrentUser && issue.IsAuthoredByCurrentUser && issue.IsMentioned));
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            activity.UpdateRepositoryActivity([], issues, [], [], DevHubSortOrder.MostRecent, false);

            Assert.AreEqual(3, activity.AttentionCount);
            Assert.AreEqual("Attention (3)", activity.AttentionFilterText);
            CollectionAssert.AreEquivalent(linked.Select(issue => issue.WebUrl).ToArray(), activity.MyWorkItems.Select(item => item.WebUrl).ToArray());
            Assert.IsTrue(activity.MyWorkItems.All(item => item.RepoIdentifier.Equals(repo)));
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.HasCount(3, activity.MyWorkItems);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void MergeIssues_WorkItemIdentityIsCollectionWideButNotCrossOrganization(bool projectWide)
        {
            var first = new DevHubIssue
            {
                RepoIdentifier = projectWide ? null : new RemoteRepoIdentifier("dev.azure.com", "org", "one", "project"), NumericId = 1,
                WebUrl = "https://dev.azure.com/org/project/_workitems/edit/1",
            };
            var linked = new DevHubIssue
            {
                RepoIdentifier = new RemoteRepoIdentifier("dev.azure.com", "org", "two", "other-project"), NumericId = 1,
                WebUrl = "https://org.visualstudio.com/other-project/_workitems/edit/1",
            };
            var other = new DevHubIssue
            {
                RepoIdentifier = new RemoteRepoIdentifier("dev.azure.com", "other-org", "one", "project"), NumericId = 1,
                WebUrl = "https://dev.azure.com/other-org/project/_workitems/edit/1",
            };

            var merged = DevHubService.MergeIssues([first, other], [linked]).ToList();

            CollectionAssert.AreEqual(new[] { linked, other }, merged);
        }

        [TestMethod]
        [DataRow("https://tfs.contoso.com/tfs/First", "https://tfs.contoso.com/tfs/Second", false)]
        [DataRow("https://tfs.contoso.com:8080/tfs/First", "https://tfs.contoso.com:8081/tfs/First", false)]
        [DataRow("https://tfs.contoso.com/tfs/First", "https://tfs.contoso.com/tfs/Second", true)]
        [DataRow("https://tfs.contoso.com:8080/tfs/First", "https://tfs.contoso.com:8081/tfs/First", true)]
        public void MergeIssues_ServerCollectionsRemainDistinct(string firstUrl, string secondUrl, bool projectWide)
        {
            var first = new DevHubIssue
            {
                RepoIdentifier = projectWide ? null : new RemoteRepoIdentifier("tfs.contoso.com", "First", "repo", "project", firstUrl, true),
                NumericId = 1, WebUrl = firstUrl + "/project/_workitems/edit/1",
            };
            var second = new DevHubIssue
            {
                RepoIdentifier = new RemoteRepoIdentifier("tfs.contoso.com", "First", "repo", "project", secondUrl, true),
                NumericId = 1, WebUrl = secondUrl + "/project/_workitems/edit/1",
            };

            Assert.HasCount(2, DevHubService.MergeIssues([first], [second]).ToList());
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
