using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.ToolWindows;

namespace StartScreen.Test
{
    [TestClass]
    public class WorkspaceTests
    {
        [TestMethod]
        [DataRow(DevHubDefaultTab.Issues, DevHubDefaultTab.Incoming)]
        [DataRow(DevHubDefaultTab.PullRequests, DevHubDefaultTab.Incoming)]
        [DataRow(DevHubDefaultTab.Builds, DevHubDefaultTab.Incoming)]
        [DataRow(DevHubDefaultTab.Overview, DevHubDefaultTab.MyWork)]
        [DataRow(DevHubDefaultTab.NeedsYou, DevHubDefaultTab.MyWork)]
        [DataRow((DevHubDefaultTab)999, DevHubDefaultTab.MyWork)]
        public void WorkViewsDefault_MigratesLegacyPreferencesOnce(DevHubDefaultTab previous, DevHubDefaultTab expected)
        {
            var options = new Options { DevHubDefaultTab = previous };

            Assert.IsTrue(options.ApplyWorkViewsDefault());
            Assert.AreEqual(expected, options.DevHubDefaultTab);
            Assert.IsFalse(options.ApplyWorkViewsDefault());
        }

        [TestMethod]
        [DataRow(DevHubDefaultTab.MyWork)]
        [DataRow(DevHubDefaultTab.Incoming)]
        public void WorkViewsDefault_PreservesExplicitPreference(DevHubDefaultTab selected)
        {
            var options = new Options();
            options.DevHubDefaultTab = selected;

            Assert.IsFalse(options.ApplyWorkViewsDefault());
            Assert.AreEqual(selected, options.DevHubDefaultTab);
        }

        [TestMethod]
        public void CopilotWorkspaceRoots_UseAllKnownCheckoutsNotOnlyVisibleMruItems()
        {
            var first = new MruItem { Name = "First", Path = @"C:\First", Type = MruItemType.Folder, RemoteUrl = "https://github.com/owner/repo.git" };
            var second = new MruItem { Name = "Second", Path = @"C:\Second\App.sln", Type = MruItemType.Solution, RemoteUrl = "https://github.com/owner/repo.git" };
            var other = new MruItem { Name = "Other", Path = @"C:\Other", Type = MruItemType.Folder, RemoteUrl = "https://github.com/owner/other.git" };
            var model = new StartScreenViewModel([first, second, other]);
            model.FilterMru("Other");
            model.SetRepositoryScope(RemoteRepoIdentifier.TryParse(first.RemoteUrl));

            CollectionAssert.AreEquivalent(new[] { @"C:\First", @"C:\Second" }, model.GetCopilotWorkspaceRoots().ToArray());

            model.SelectedMruItem = second;
            CollectionAssert.AreEqual(new[] { @"C:\Second" }, model.GetCopilotWorkspaceRoots().ToArray());
        }

        [TestMethod]
        public void SolutionsOnly_PreservesPinnedFoldersButSearchStillApplies()
        {
            var pinned = new MruItem { Name = "Tools", Path = @"C:\Tools", Type = MruItemType.Folder, IsPinned = true };
            var folder = new MruItem { Name = "Code", Path = @"C:\Code", Type = MruItemType.Folder };
            var solution = new MruItem { Name = "App", Path = @"C:\App.sln", Type = MruItemType.Solution };

            Assert.IsTrue(StartScreenViewModel.MatchesMruFilter(pinned, "tools", true));
            Assert.IsFalse(StartScreenViewModel.MatchesMruFilter(pinned, "app", true));
            Assert.IsFalse(StartScreenViewModel.MatchesMruFilter(folder, null, true));
            Assert.IsTrue(StartScreenViewModel.MatchesMruFilter(solution, null, true));
            Assert.IsTrue(StartScreenViewModel.MatchesMruFilter(folder, null, false));
        }

        [TestMethod]
        public void SolutionsOnly_AppliesToGroupedAndSearchResults()
        {
            var pinned = new MruItem { Name = "Pinned", Path = @"C:\Pinned", Type = MruItemType.Folder, IsPinned = true };
            var folder = new MruItem { Name = "Folder", Path = @"C:\Folder", Type = MruItemType.Folder };
            var solution = new MruItem { Name = "Solution", Path = @"C:\Solution.sln", Type = MruItemType.Solution };
            var model = new StartScreenViewModel([pinned, folder, solution]) { ShowSolutionsOnly = true };

            CollectionAssert.AreEquivalent(new[] { pinned, solution }, model.VisibleMruItems.ToArray());
            model.FilterMru("folder");
            Assert.IsEmpty(model.VisibleMruItems);
            model.FilterMru("pinned");
            CollectionAssert.AreEqual(new[] { pinned }, model.VisibleMruItems.ToArray());
        }

        [TestMethod]
        public void Reload_RebindsSelectionByPathAndClearsRemovedWorkspace()
        {
            var old = new MruItem { Name = "Old", Path = @"C:\App.sln" };
            var model = new StartScreenViewModel([old]) { SelectedMruItem = old };
            var refreshed = new MruItem { Name = "App", Path = @"c:\app.sln" };

            model.SetMruItems([refreshed]);

            Assert.AreSame(refreshed, model.SelectedMruItem);
            Assert.IsTrue(refreshed.IsSelected);
            Assert.IsFalse(old.IsSelected);
            Assert.AreEqual("App", model.WorkSectionTitle);
            model.SetMruItems([]);
            Assert.IsNull(model.SelectedMruItem);
            Assert.AreEqual("Your work", model.WorkSectionTitle);
        }

        [TestMethod]
        public void RemoteMetadata_UpdatesSelectedRepositoryWithoutGuessingLocalActivity()
        {
            var item = new MruItem { Name = "App" };
            var model = new StartScreenViewModel([item]) { SelectedMruItem = item };
            var changed = new List<string?>();
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Assert.IsNull(model.SelectedRepository);
            item.RemoteUrl = "https://github.com/owner/repo.git";

            Assert.AreEqual("repo", model.SelectedRepository.Repo);
            CollectionAssert.Contains(changed, nameof(StartScreenViewModel.SelectedRepository));
            item.ApplyRepositorySummary("12 PRs / 1 review", "Repository-wide counts");
            Assert.AreEqual("12 PRs / 1 review", item.RepositorySummaryText);
            item.RemoteUrl = "https://github.com/owner/other.git";
            Assert.AreEqual("View repository activity", item.RepositorySummaryText);
            StringAssert.Contains(item.RepositorySummaryTooltip, "not been loaded yet");
        }

        [TestMethod]
        public void RepositorySummaryGroups_IncludeEveryVisibleRepositoryBeyondTwenty()
        {
            var items = Enumerable.Range(0, 30).Select(index => new MruItem
            {
                Name = "Repo" + index,
                Path = $@"C:\Checkouts\Repo{index}\App.sln",
                RemoteUrl = $"https://github.com/owner/repo{index}.git",
            }).ToArray();
            var model = new StartScreenViewModel(items);

            var groups = model.GetRepositorySummaryGroups(items);

            Assert.HasCount(30, groups);
            CollectionAssert.AreEquivalent(items, groups.SelectMany(group => group).ToArray());
            Assert.AreEqual("repo29", groups.Last().Key.Repo);
        }

        [TestMethod]
        public void RepositorySummaryGroups_PrioritizeSelectionAndPinsAndKeepAllCheckouts()
        {
            var first = new MruItem { Name = "First", Path = @"C:\First\App.sln", RemoteUrl = "https://github.com/owner/repo.git" };
            var pinned = new MruItem { Name = "Pinned", Path = @"D:\Pinned\App.sln", IsPinned = true, RemoteUrl = "https://github.com/owner/pinned.git" };
            var selected = new MruItem { Name = "Selected", Path = @"D:\Selected\App.sln", RemoteUrl = "git@github.com:OWNER/REPO.git" };
            var local = new MruItem { Name = "Local", Path = @"D:\Local\App.sln" };
            var model = new StartScreenViewModel([first, pinned, selected, local]) { SelectedMruItem = selected };

            var groups = model.GetRepositorySummaryGroups([first, pinned, selected, local]);

            Assert.HasCount(2, groups);
            Assert.AreEqual("REPO", groups[0].Key.Repo);
            CollectionAssert.AreEquivalent(new[] { first, selected }, groups[0].ToArray());
            Assert.AreSame(pinned, groups[1].Single());

            model.FilterMru("Pinned");
            Assert.AreSame(pinned, model.GetRepositorySummaryGroups([first, pinned, selected, local]).Single().Single());
        }

        [TestMethod]
        public void RepositorySummary_OffersActivityLinkUntilLoadedAndPreservesFailures()
        {
            var item = new MruItem();
            Assert.AreEqual(string.Empty, item.RepositorySummaryText);
            item.GitBranch = "main";
            Assert.AreEqual("Local repository", item.RepositorySummaryText);

            item.RemoteUrl = "https://github.com/owner/repo.git";
            Assert.AreEqual("View repository activity", item.RepositorySummaryText);
            Assert.IsFalse(item.HasRepositoryCounts);
            StringAssert.Contains(item.RepositorySummaryTooltip, "Select to view");

            item.ApplyRepositorySummary("Repository activity unavailable", "Refresh to try again.");
            Assert.AreEqual("Repository activity unavailable", item.RepositorySummaryText);
            Assert.AreEqual("Refresh to try again.", item.RepositorySummaryTooltip);

            item.ApplyRepositorySummary("Sign in to see repository activity", null);
            Assert.AreEqual(item.RepositorySummaryText, item.RepositorySummaryTooltip);
        }

        [TestMethod]
        public void NeedsYou_RequiresActualReviewRequestOrFailingAuthoredPr()
        {
            var unrelated = new DevHubPullRequest { Title = "Other", CiStatus = "failure" };
            var review = new DevHubPullRequest { Title = "Review", Status = "open", IsReviewRequestedForCurrentUser = true };
            var authored = new DevHubPullRequest { Title = "Fix checks", Status = "open", IsAuthoredByCurrentUser = true, CiStatus = "failure" };
            var draft = new DevHubPullRequest { Title = "Draft", Status = "draft", IsReviewRequestedForCurrentUser = true };

            var items = DevHubWorkItem.GetNeedsYou([unrelated, authored, review, draft]);

            CollectionAssert.AreEqual(new[] { "Review", "Fix checks" }, items.Select(item => item.Title).ToArray());
            Assert.IsTrue(items.All(item => item.IsPullRequest));
        }

        [TestMethod]
        public void Incoming_PreservesOlderActivityAndUsesProviderTimestampsNewestFirst()
        {
            var seen = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var old = new DevHubPullRequest { Title = "Old", Status = "open", UpdatedAt = seen, WebUrl = "https://github.com/o/r/pull/1" };
            var recent = new DevHubIssue { Title = "Issue", UpdatedAt = seen.AddMinutes(1), WebUrl = "https://github.com/o/r/issues/2" };
            var latest = new DevHubCiRun { Name = "CI", Timestamp = seen.AddMinutes(2), WebUrl = "https://github.com/o/r/actions/runs/3" };

            var items = DevHubWorkItem.GetIncoming([old], [recent], [latest]);

            CollectionAssert.AreEqual(new[] { "CI", "Issue", "Old" }, items.Select(item => item.Title).ToArray());
            Assert.IsFalse(items[0].IsPullRequest);
        }

        [TestMethod]
        public void OwnPullRequests_PersistAfterActivityIsSeenAndAreNotLimitedToRecentChanges()
        {
            var prs = Enumerable.Range(1, 12).Select(id => new DevHubPullRequest
            {
                Title = $"Your PR {id}",
                WebUrl = $"https://github.com/o/r/pull/{id}",
                IsAuthoredByCurrentUser = true,
                Status = id == 1 ? "draft" : "open",
                UpdatedAt = new DateTime(2026, 1, id, 0, 0, 0, DateTimeKind.Utc),
            }).ToArray();

            var own = DevHubWorkItem.GetOwnPullRequests(prs);

            Assert.HasCount(12, own);
            CollectionAssert.AreEqual(Enumerable.Reverse(prs).Select(pr => pr.Title).ToArray(), own.Select(item => item.Title).ToArray());
            Assert.IsTrue(own.All(item => item.IsPullRequest));
            Assert.IsEmpty(DevHubWorkItem.GetIncoming(prs, [], []));
            Assert.HasCount(12, DevHubWorkItem.GetMyWork(prs, [], []));
        }

        [TestMethod]
        public void OwnPullRequests_ExcludeOtherAuthorsClosedPrsAndMissingLinks()
        {
            var prs = new[]
            {
                new DevHubPullRequest { Status = "open", WebUrl = "https://github.com/o/r/pull/1" },
                new DevHubPullRequest { IsAuthoredByCurrentUser = true, Status = "completed", WebUrl = "https://example.com/2" },
                new DevHubPullRequest { IsAuthoredByCurrentUser = true, Status = "abandoned", WebUrl = "https://example.com/3" },
                new DevHubPullRequest { IsAuthoredByCurrentUser = true, Status = "open" },
            };

            Assert.IsEmpty(DevHubWorkItem.GetOwnPullRequests(prs));
            Assert.IsEmpty(DevHubWorkItem.GetOwnPullRequests(null!));
        }

        [TestMethod]
        [DataRow("open", null, 0, "Waiting for review")]
        [DataRow("open", "success", 1, "1 approval")]
        [DataRow("open", "success", 2, "2 approvals")]
        [DataRow("open", "pending", 1, "Checks pending")]
        [DataRow("open", "failure", 1, "Checks failing")]
        [DataRow("conflicts", "success", 1, "Conflicts")]
        [DataRow("draft", null, 0, "Draft")]
        public void OwnPullRequests_ShowAvailableStatus(string status, string? ciStatus, int approvals, string expected)
        {
            var pr = new DevHubPullRequest
            {
                IsAuthoredByCurrentUser = true,
                Status = status,
                CiStatus = ciStatus,
                ApprovalCount = approvals,
                RepoDisplayName = "o/r",
                Number = "#1",
                WebUrl = "https://github.com/o/r/pull/1",
            };

            var item = DevHubWorkItem.GetOwnPullRequests([pr]).Single();

            Assert.AreEqual(expected, item.StatusText);
            Assert.AreEqual("o/r / #1", item.Detail);
            Assert.AreEqual(pr.WebUrl, item.WebUrl);
        }

        [TestMethod]
        public void SummaryCounts_HaveSeparateDestinationsAndClearOnRemoteChange()
        {
            var item = new MruItem { RemoteUrl = "https://github.com/o/r.git" };
            var changes = new List<string?>();
            item.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

            item.ApplyRepositorySummary("12 PRs / 1 review", "Complete counts", 12, 1);

            Assert.IsTrue(item.HasRepositoryCounts);
            Assert.IsTrue(item.HasOpenPullRequests);
            Assert.AreEqual("12", item.RepositoryPullRequestCountText);
            Assert.IsTrue(item.HasRequestedReviews);
            Assert.AreEqual("12 open PRs", item.RepositoryPullRequestsText);
            Assert.AreEqual("1 review", item.RepositoryReviewsText);
            CollectionAssert.Contains(changes, nameof(MruItem.RepositoryPullRequestsText));
            CollectionAssert.Contains(changes, nameof(MruItem.RepositoryReviewsText));
            CollectionAssert.Contains(changes, nameof(MruItem.HasOpenPullRequests));
            CollectionAssert.Contains(changes, nameof(MruItem.RepositoryPullRequestCountText));

            item.RemoteUrl = "https://github.com/o/other.git";
            Assert.IsFalse(item.HasRepositoryCounts);
            Assert.IsFalse(item.HasOpenPullRequests);
            Assert.AreEqual(string.Empty, item.RepositoryPullRequestCountText);
            Assert.IsFalse(item.HasRequestedReviews);
        }

        [TestMethod]
        public void SummaryCounts_DistinguishZeroFromUnavailableAndKeepReviewsIndependent()
        {
            var item = new MruItem();
            item.ApplyRepositorySummary("No open PRs", "Loaded", 0, 0);
            Assert.IsTrue(item.HasRepositoryCounts);
            Assert.IsFalse(item.HasOpenPullRequests);
            Assert.IsFalse(item.HasRequestedReviews);

            item.ApplyRepositorySummary("Reviews requested", "Loaded", 0, 2);
            Assert.IsFalse(item.HasOpenPullRequests);
            Assert.IsTrue(item.HasRequestedReviews);
            Assert.AreEqual("2 reviews", item.RepositoryReviewsText);

            item.ApplyRepositorySummary("Repository activity unavailable", "Refresh to try again");
            Assert.IsFalse(item.HasRepositoryCounts);
            Assert.IsFalse(item.HasOpenPullRequests);
            Assert.AreEqual(string.Empty, item.RepositoryPullRequestCountText);
            Assert.AreEqual("Repository activity unavailable", item.RepositorySummaryText);
        }

        [TestMethod]
        [DataRow(780d, true, 460d, false, 1, 1)]
        [DataRow(900d, true, 560d, false, 1, 1)]
        [DataRow(1600d, true, 560d, false, 3, 3)]
        [DataRow(2200d, true, 560d, false, 4, 5)]
        [DataRow(779d, true, 560d, true, 2, 2)]
        [DataRow(1200d, false, 0d, false, 3, 4)]
        public void ContentLayout_CapsWorkAndGivesExtraWidthToNews(double width, bool showWork,
            double expectedWorkWidth, bool expectedStacked, int expectedNewsColumns, int expectedVideoColumns)
        {
            var layout = StartScreenControl.CalculateContentLayout(width, showWork);

            Assert.AreEqual(expectedWorkWidth, layout.WorkWidth);
            Assert.AreEqual(expectedStacked, layout.Stacked);
            Assert.AreEqual(expectedNewsColumns, layout.NewsColumns);
            Assert.AreEqual(expectedVideoColumns, layout.VideoColumns);
            Assert.IsLessThanOrEqualTo(560d, layout.WorkWidth);
        }

        [TestMethod]
        public void WorkViews_KeepActionableItemsInTheirOwnershipView()
        {
            var requested = new DevHubPullRequest
            {
                Title = "Review me",
                Status = "open",
                IsReviewRequestedForCurrentUser = true,
                WebUrl = "https://github.com/o/r/pull/1",
                UpdatedAt = DateTime.UtcNow,
            };
            var failing = new DevHubPullRequest
            {
                Title = "Fix checks",
                Status = "open",
                IsAuthoredByCurrentUser = true,
                CiStatus = "failure",
                WebUrl = "https://github.com/o/r/pull/2",
                UpdatedAt = DateTime.UtcNow,
            };
            var other = new DevHubPullRequest
            {
                Title = "Other update",
                Status = "open",
                WebUrl = "https://github.com/o/r/pull/3",
                UpdatedAt = DateTime.UtcNow,
            };

            var incoming = DevHubWorkItem.GetIncoming([requested, failing, other], [], []);
            var own = DevHubWorkItem.GetMyWork([requested, failing, other], [], []);

            CollectionAssert.AreEqual(new[] { "Review me", "Other update" }, incoming.Select(item => item.Title).ToArray());
            Assert.AreEqual("Requests for you", incoming[0].Section);
            Assert.AreEqual("Repository activity", incoming[1].Section);
            CollectionAssert.AreEqual(new[] { "Fix checks" }, own.Select(item => item.Title).ToArray());
            Assert.AreEqual("Checks failing", own.Single().AttentionReason);
            CollectionAssert.AreEqual(new[] { "Review me" },
                DevHubWorkItem.Filter(incoming, DevHubWorkItemKind.All, true).Select(item => item.Title).ToArray());
            Assert.HasCount(2, incoming);
        }

        [TestMethod]
        public void MyWork_IncludesCreatedAndAssignedIssuesButDoesNotDuplicateThemInIncoming()
        {
            var issues = new[]
            {
                new DevHubIssue { Title = "Created", IsAuthoredByCurrentUser = true, State = "open", WebUrl = "https://example.com/1" },
                new DevHubIssue { Title = "Assigned", IsAssignedToCurrentUser = true, State = "active", WebUrl = "https://example.com/2" },
                new DevHubIssue { Title = "Both", IsAuthoredByCurrentUser = true, IsAssignedToCurrentUser = true, State = "open", WebUrl = "https://example.com/3" },
                new DevHubIssue { Title = "Other", State = "open", WebUrl = "https://example.com/4" },
                new DevHubIssue { Title = "Closed", IsAuthoredByCurrentUser = true, IsClosed = true, WebUrl = "https://example.com/5" },
                new DevHubIssue { Title = "Unlinked", IsAuthoredByCurrentUser = true },
            };

            var own = DevHubWorkItem.GetMyWork([], issues, []);
            var incoming = DevHubWorkItem.GetIncoming([], issues, []);

            CollectionAssert.AreEquivalent(new[] { "Created", "Assigned", "Both" }, own.Select(item => item.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "Other" }, incoming.Select(item => item.Title).ToArray());
            CollectionAssert.AreEquivalent(new[] { "Assigned", "Both" },
                DevHubWorkItem.Filter(own, DevHubWorkItemKind.Issue, true).Select(item => item.Title).ToArray());
        }

        [TestMethod]
        public void AttentionFilter_ComposesWithKindAndDoesNotTreatIdleOrCachedSessionsAsWaiting()
        {
            var sessions = new[]
            {
                new CopilotActivitySession { Id = "waiting", PendingQuestions = 1 },
                new CopilotActivitySession { Id = "idle", IsResponding = false },
                new CopilotActivitySession { Id = "cached", IsCached = true, PendingPermissions = 1 },
                new CopilotActivitySession { Id = "saved", IsTranscript = true, PendingQuestions = 1 },
            };
            var prs = new[]
            {
                new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, HasChangesRequested = true, WebUrl = "https://example.com/1" },
                new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, CiStatus = "pending", WebUrl = "https://example.com/2" },
            };
            var own = DevHubWorkItem.GetMyWork(prs, [], sessions);

            Assert.HasCount(6, own);
            Assert.HasCount(2, DevHubWorkItem.Filter(own, DevHubWorkItemKind.All, true));
            Assert.AreEqual("waiting", DevHubWorkItem.Filter(own, DevHubWorkItemKind.Copilot, true).Single().CopilotSessionId);
            Assert.AreEqual("Changes requested", DevHubWorkItem.Filter(own, DevHubWorkItemKind.PullRequest, true).Single().AttentionReason);
            Assert.HasCount(4, DevHubWorkItem.Filter(own, DevHubWorkItemKind.Copilot, false));
            Assert.IsEmpty(DevHubWorkItem.Filter(own, DevHubWorkItemKind.Build, true));
        }

        [STATestMethod]
        public void WorkViews_GroupCardsWithoutLosingActionableItems()
        {
            var own = DevHubWorkItem.GetMyWork(
                [new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, WebUrl = "https://example.com/1" }],
                [new DevHubIssue { IsAssignedToCurrentUser = true, State = "open", WebUrl = "https://example.com/2" }],
                [new CopilotActivitySession { Id = "session", PendingQuestions = 1 }]);

            var view = ToolWindows.Controls.DevHubPanel.CreateWorkItemView(own);

            Assert.HasCount(3, view.Groups);
            CollectionAssert.AreEqual(new[] { "Pull requests", "Issues and tasks", "Copilot sessions" },
                view.Groups.Cast<System.Windows.Data.CollectionViewGroup>().Select(group => group.Name.ToString()).ToArray());
            Assert.AreEqual(own.Count, view.Count);
        }

        [TestMethod]
        public void WorkViews_PreserveProviderSortWithinSections()
        {
            var prs = new[]
            {
                new DevHubPullRequest { Title = "GitHub", IsAuthoredByCurrentUser = true, Status = "open", WebUrl = "https://github.com/o/r/pull/1", RepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r.git"), UpdatedAt = DateTime.UtcNow },
                new DevHubPullRequest { Title = "ADO", IsAuthoredByCurrentUser = true, Status = "open", WebUrl = "https://dev.azure.com/o/p/_git/r/pullrequest/1", RepoIdentifier = RemoteRepoIdentifier.TryParse("https://dev.azure.com/o/p/_git/r"), UpdatedAt = DateTime.UtcNow.AddDays(-1) },
            };

            CollectionAssert.AreEqual(new[] { "ADO", "GitHub" }, DevHubWorkItem.GetMyWork(prs, [], [], DevHubSortOrder.AzureDevOpsFirst)
                .Select(item => item.Title).ToArray());
        }
    }
}