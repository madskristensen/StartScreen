using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.ToolWindows;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test
{
    [TestClass]
    public class WorkspaceTests
    {
        [TestMethod]
        public void RepositorySummaryGroups_DeferOffscreenItemsButKeepPinnedAndSelected()
        {
            var items = Enumerable.Range(0, 4).Select(index => new MruItem
            {
                Name = "repo" + index,
                Path = @"C:\repos\repo" + index + @"\App.sln",
                RemoteUrl = "https://github.com/owner/repo" + index,
            }).ToArray();
            using var model = new StartScreenViewModel(items);
            model.EnableMruViewportTracking();
            items[1].IsPinned = true;
            model.SelectedMruItem = items[2];
            model.SetMruItemRealized(items[0], true);
            CollectionAssert.AreEquivalent(items.Take(3).ToArray(),
                model.GetRepositorySummaryGroups(items).SelectMany(group => group).ToArray());
            model.SetMruItemRealized(items[0], false);
            model.SetMruItemRealized(items[3], true);
            CollectionAssert.AreEquivalent(items.Skip(1).ToArray(),
                model.GetRepositorySummaryGroups(items).SelectMany(group => group).ToArray());
        }

        [STATestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ActivityIssues_ReentryRetainsListCollectionAndGeneratedContainers(bool personal)
        {
            var activity = new DevHubActivityViewModel();
            var issues = Enumerable.Range(0, 3).Select(index => new DevHubIssue
            {
                Title = "Task " + index, WebUrl = "https://example.com/issues/" + index,
                IsAssignedToCurrentUser = personal,
            }).ToArray();
            activity.UpdateRepositoryActivity([], issues, [], [], DevHubSortOrder.MostRecent, false);
            var host = new StackPanel();
            var original = new ItemsControl
            {
                Focusable = false,
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) },
            };
            host.Children.Add(original);
            var presentation = new DevHubActivityList(original);
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            activity.SetIncomingFilter(DevHubWorkItemKind.Issue);
            var items = personal ? activity.MyWorkItems : activity.IncomingItems;
            var list = presentation.Show(DevHubWorkItemKind.Issue, false, items);
            LayoutActivity(host);
            var source = list.ItemsSource;
            var containers = Enumerable.Range(0, 3).Select(list.ItemContainerGenerator.ContainerFromIndex).ToArray();
            Assert.IsTrue(containers.All(container => container != null));
            var changes = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)source).CollectionChanged += (_, e) => changes.Add(e.Action);

            for (var visit = 0; visit < 3; visit++)
            {
                activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
                activity.SetIncomingFilter(DevHubWorkItemKind.PullRequest);
                var other = presentation.Show(DevHubWorkItemKind.PullRequest, false,
                    personal ? activity.MyWorkItems : activity.IncomingItems);
                LayoutActivity(host);
                Assert.AreEqual(Visibility.Collapsed, list.Visibility);
                activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
                activity.SetIncomingFilter(DevHubWorkItemKind.Issue);
                Assert.AreSame(list, presentation.Show(DevHubWorkItemKind.Issue, false, items));
                LayoutActivity(host);
                Assert.AreEqual(Visibility.Collapsed, other.Visibility);
                Assert.AreSame(source, list.ItemsSource);
                CollectionAssert.AreEqual(containers,
                    Enumerable.Range(0, 3).Select(list.ItemContainerGenerator.ContainerFromIndex).ToArray());
            }
            Assert.IsEmpty(changes);
            Assert.HasCount(2, host.Children);
        }

        [STATestMethod]
        public void ActivityIssues_HiddenListSynchronizesRealUpdatesBeforeReentry()
        {
            var activity = new DevHubActivityViewModel();
            var issue = new DevHubIssue { Title = "Old", WebUrl = "https://example.com/issues/1", IsAssignedToCurrentUser = true };
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            activity.UpdateRepositoryActivity([], [issue], [], [], DevHubSortOrder.MostRecent, false);
            var host = new StackPanel();
            var original = new ItemsControl();
            host.Children.Add(original);
            var presentation = new DevHubActivityList(original);
            var list = presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems);
            var source = list.ItemsSource;
            presentation.Show(DevHubWorkItemKind.PullRequest, false, []);
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            issue.Title = "Updated";
            activity.UpdateRepositoryActivity([], [issue], [], [], DevHubSortOrder.MostRecent, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);

            Assert.AreSame(list, presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems));
            Assert.AreSame(source, list.ItemsSource);
            Assert.AreEqual("Updated", list.Items.Cast<DevHubWorkItem>().Single().Title);

            presentation.Show(DevHubWorkItemKind.PullRequest, false, []);
            activity.UpdateRepositoryActivity([], [], [], [], DevHubSortOrder.MostRecent, false);
            presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems);
            Assert.IsEmpty(list.Items);
        }

        [STATestMethod]
        public void ActivityIssues_CachedListsHonorSortCompletedAttentionAndIdentityChanges()
        {
            var now = DateTime.UtcNow;
            var github = new DevHubIssue
            {
                Title = "GitHub", WebUrl = "https://github.com/o/r/issues/1", State = "open",
                RepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r.git"),
                IsAssignedToCurrentUser = true, UpdatedAt = now,
            };
            var ado = new DevHubIssue
            {
                Title = "ADO", WebUrl = "https://dev.azure.com/o/p/_workitems/edit/1", State = "active",
                RepoIdentifier = RemoteRepoIdentifier.TryParse("https://dev.azure.com/o/p/_git/r"),
                IsAssignedToCurrentUser = true, UpdatedAt = now.AddHours(-1),
            };
            var completed = new DevHubIssue
            {
                Title = "Completed", WebUrl = "https://github.com/o/r/issues/2", IsClosed = true,
                IsAssignedToCurrentUser = true, UpdatedAt = now.AddHours(-2),
            };
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            activity.UpdateRepositoryActivity([], [github, ado, completed], [], [], DevHubSortOrder.MostRecent, false);
            var host = new StackPanel();
            var original = new ItemsControl();
            host.Children.Add(original);
            var presentation = new DevHubActivityList(original);
            var list = presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems);
            var source = list.ItemsSource;
            var cards = list.Items.Cast<DevHubWorkItem>().ToArray();
            var changes = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)source).CollectionChanged += (_, e) => changes.Add(e.Action);
            presentation.Show(DevHubWorkItemKind.PullRequest, false, []);

            activity.Refresh([], DevHubSortOrder.AzureDevOpsFirst, true);
            presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems);
            CollectionAssert.AreEqual(new[] { "ADO", "GitHub", "Completed" }, list.Items.Cast<DevHubWorkItem>().Select(item => item.Title).ToArray());
            Assert.AreSame(cards[1], list.Items[0]);
            Assert.AreSame(cards[0], list.Items[1]);
            Assert.AreSame(source, list.ItemsSource);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);

            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            var attention = presentation.Show(DevHubWorkItemKind.All, true, activity.MyWorkItems);
            CollectionAssert.AreEqual(new[] { "GitHub", "ADO" }, attention.Items.Cast<DevHubWorkItem>().Select(item => item.Title).ToArray());
            github.IsAssignedToCurrentUser = false;
            ado.IsAssignedToCurrentUser = false;
            activity.Refresh([], DevHubSortOrder.AzureDevOpsFirst, false);
            presentation.Show(DevHubWorkItemKind.All, true, activity.MyWorkItems);
            Assert.IsEmpty(attention.Items);
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            Assert.AreSame(list, presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems));
            Assert.IsEmpty(list.Items);
            activity.SetIncomingFilter(DevHubWorkItemKind.Issue);
            CollectionAssert.AreEqual(new[] { "ADO", "GitHub" }, activity.IncomingItems.Select(item => item.Title).ToArray());
            Assert.AreEqual(DevHubWorkItemKind.Issue, activity.MyWorkKind);
            Assert.IsFalse(activity.MyWorkAttention);
        }

        [STATestMethod]
        public void ActivityContextChange_ReleasesInactiveDataButKeepsFilterListsReusable()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            activity.UpdateRepositoryActivity([], [new DevHubIssue
            {
                Title = "Previous scope", WebUrl = "https://example.com/issues/old", IsAssignedToCurrentUser = true,
            }], [], [], DevHubSortOrder.MostRecent, false);
            var host = new StackPanel();
            var original = new ItemsControl();
            host.Children.Add(original);
            var presentation = new DevHubActivityList(original);
            var issueList = presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems);
            var issueSource = issueList.ItemsSource;
            var active = presentation.Show(DevHubWorkItemKind.PullRequest, false, activity.MyWorkItems);
            var activeSource = active.ItemsSource;
            Assert.HasCount(1, issueList.Items);

            presentation.ClearInactiveItems();

            Assert.IsEmpty(issueList.Items, "Inactive lists must not retain cards from the previous context.");
            Assert.AreSame(issueSource, issueList.ItemsSource);
            Assert.AreSame(activeSource, active.ItemsSource);
            Assert.HasCount(1, active.Items, "The active list is synchronized separately without discarding its containers.");
            activity.UpdateRepositoryActivity([], [new DevHubIssue
            {
                Title = "New scope", WebUrl = "https://example.com/issues/new", IsAssignedToCurrentUser = true,
            }], [], [], DevHubSortOrder.MostRecent, false);
            Assert.AreSame(issueList, presentation.Show(DevHubWorkItemKind.Issue, false, activity.MyWorkItems));
            Assert.AreEqual("New scope", issueList.Items.Cast<DevHubWorkItem>().Single().Title);
            Assert.AreSame(issueSource, issueList.ItemsSource);
            Assert.HasCount(2, host.Children);
        }

        private static void LayoutActivity(FrameworkElement host)
        {
            host.Measure(new Size(800, 1000));
            host.Arrange(new Rect(0, 0, 800, 1000));
            host.UpdateLayout();
        }

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
            var review = new DevHubPullRequest { Title = "Review", Status = "open", IsReviewRequestedForCurrentUser = true, UpdatedAt = DateTime.UtcNow };
            var authored = new DevHubPullRequest { Title = "Fix checks", Status = "open", IsAuthoredByCurrentUser = true, CiStatus = "failure", UpdatedAt = DateTime.UtcNow };
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
        [DataRow(780d, true, 960d, false, 1, 1)]
        [DataRow(900d, true, 960d, false, 1, 1)]
        [DataRow(1399d, true, 960d, false, 1, 1)]
        [DataRow(1400d, true, 960d, false, 1, 1)]
        [DataRow(1401d, true, 961d, false, 1, 1)]
        [DataRow(1439d, true, 999d, false, 1, 1)]
        [DataRow(1440d, true, 1000d, false, 1, 1)]
        [DataRow(1441d, true, 1000d, false, 1, 1)]
        [DataRow(1600d, true, 1000d, false, 1, 2)]
        [DataRow(1663d, true, 1000d, false, 1, 2)]
        [DataRow(1664d, true, 1000d, false, 2, 2)]
        [DataRow(1995d, true, 1000d, false, 2, 3)]
        [DataRow(1996d, true, 1000d, false, 3, 3)]
        [DataRow(2200d, true, 1000d, false, 3, 4)]
        [DataRow(3200d, true, 1000d, false, 6, 7)]
        [DataRow(779d, true, 960d, false, 1, 1)]
        [DataRow(0d, true, 960d, false, 1, 1)]
        [DataRow(1000d, true, 960d, false, 1, 1)]
        [DataRow(1080d, true, 960d, false, 1, 1)]
        [DataRow(1081d, true, 960d, false, 1, 1)]
        [DataRow(1200d, false, 0d, false, 3, 4)]
        [DataRow(0d, false, 0d, false, 1, 1)]
        [DataRow(300d, false, 0d, false, 1, 1)]
        [DataRow(440d, false, 0d, false, 1, 1)]
        [DataRow(3200d, false, 0d, false, 9, 10)]
        public void ContentLayout_KeepsWorkBladesReadableAndNewsOnTheRight(double width, bool showWork,
            double expectedWorkWidth, bool expectedStacked, int expectedNewsColumns, int expectedVideoColumns)
        {
            var layout = StartScreenControl.CalculateContentLayout(width, showWork);

            Assert.AreEqual(expectedWorkWidth, layout.WorkWidth);
            Assert.AreEqual(expectedStacked, layout.Stacked);
            Assert.AreEqual(expectedNewsColumns, layout.NewsColumns);
            Assert.AreEqual(expectedVideoColumns, layout.VideoColumns);
            if (showWork)
            {
                Assert.IsLessThanOrEqualTo(1000d, layout.WorkWidth);
                Assert.IsGreaterThanOrEqualTo(960d, layout.WorkWidth);
                Assert.IsGreaterThanOrEqualTo(440d, Math.Max(1400, width) - layout.WorkWidth);
            }
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

            CollectionAssert.AreEqual(new[] { "Other update" }, incoming.Select(item => item.Title).ToArray());
            Assert.AreEqual("Repository activity", incoming.Single().Section);
            CollectionAssert.AreEquivalent(new[] { "Fix checks", "Review me" }, own.Select(item => item.Title).ToArray());
            Assert.AreEqual("Checks failing", own.Single(item => item.Title == "Fix checks").AttentionReason);
            Assert.AreEqual("Review requested", own.Single(item => item.Title == "Review me").AttentionReason);
            CollectionAssert.AreEquivalent(new[] { "Review me", "Fix checks" },
                DevHubWorkItem.Filter(own, DevHubWorkItemKind.All, true).Select(item => item.Title).ToArray());
            Assert.IsEmpty(DevHubWorkItem.Filter(incoming, DevHubWorkItemKind.All, true));
        }

        [TestMethod]
        public void MyWork_IncludesCreatedAndAssignedIssuesButDoesNotDuplicateThemInIncoming()
        {
            var issues = new[]
            {
                new DevHubIssue { Title = "Created", IsAuthoredByCurrentUser = true, State = "open", WebUrl = "https://example.com/1" },
                new DevHubIssue { Title = "Assigned", IsAssignedToCurrentUser = true, State = "active", WebUrl = "https://example.com/2", UpdatedAt = DateTime.UtcNow },
                new DevHubIssue { Title = "Both", IsAuthoredByCurrentUser = true, IsAssignedToCurrentUser = true, State = "open", WebUrl = "https://example.com/3", UpdatedAt = DateTime.UtcNow },
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
                new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, HasChangesRequested = true, WebUrl = "https://example.com/1", UpdatedAt = DateTime.UtcNow },
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
        public void Attention_ShowsEveryActionableTypeChronologicallyWithoutGroupsOrATopItemsLimit()
        {
            var now = DateTime.UtcNow;
            var prs = new[]
            {
                new DevHubPullRequest { Title = "My PR", Status = "open", IsAuthoredByCurrentUser = true, CiStatus = "failure", WebUrl = "https://example.com/pr/1", UpdatedAt = now.AddHours(-12.5) },
                new DevHubPullRequest { Title = "Waiting PR", Status = "open", IsAuthoredByCurrentUser = true, CiStatus = "pending", WebUrl = "https://example.com/pr/2", UpdatedAt = now },
                new DevHubPullRequest { Title = "Merged PR", Status = "closed", IsMerged = true, IsAuthoredByCurrentUser = true, HasChangesRequested = true, WebUrl = "https://example.com/pr/3", UpdatedAt = now },
            };
            var issues = Enumerable.Range(0, 12).Select(index => new DevHubIssue
            {
                Title = $"Task {index}", IsAssignedToCurrentUser = true, State = "open",
                WebUrl = $"https://example.com/issues/{index}", UpdatedAt = now.AddHours(-index - 1),
            }).Append(new DevHubIssue { Title = "Completed task", IsAssignedToCurrentUser = true, IsClosed = true, WebUrl = "https://example.com/issues/closed", UpdatedAt = now });
            var sessions = new[]
            {
                new CopilotActivitySession { Id = "waiting", Title = "Copilot", PendingQuestions = 1, LastActivity = now.AddHours(-3.5) },
                new CopilotActivitySession { Id = "idle", Title = "Idle", LastActivity = now },
            };
            var builds = new[]
            {
                new DevHubCiRun { Name = "Build", Status = "failure", IsTriggeredByCurrentUser = true, WebUrl = "https://example.com/build/1", Timestamp = now.AddHours(-7.5) },
                new DevHubCiRun { Name = "Successful build", Status = "success", IsTriggeredByCurrentUser = true, WebUrl = "https://example.com/build/2", Timestamp = now },
            };
            var inventory = DevHubWorkItem.GetMyWork(prs, issues, sessions, DevHubSortOrder.AzureDevOpsFirst,
                includeCompleted: true, builds: builds);
            var attention = DevHubWorkItem.Filter(inventory, DevHubWorkItemKind.All, true);
            var activity = new DevHubActivityViewModel();
            activity.UpdateRepositoryActivity(prs, issues.ToArray(), builds, sessions, DevHubSortOrder.AzureDevOpsFirst, true);
            Assert.IsTrue(activity.TrySelectInitialFilter());
            var view = CollectionViewSource.GetDefaultView(activity.MyWorkItems);

            Assert.AreEqual(2, inventory.Count(item => item.IsCompleted));
            Assert.IsFalse(attention.Any(item => item.IsBuild));
            CollectionAssert.AreEquivalent(new[] { "Build", "Successful build" },
                DevHubWorkItem.Filter(inventory, DevHubWorkItemKind.Build, false).Select(item => item.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "Task 0", "Task 1", "Task 2", "Copilot", "Task 3", "Task 4", "Task 5", "Task 6", "Task 7", "Task 8", "Task 9", "Task 10", "Task 11", "My PR" },
                view.Cast<DevHubWorkItem>().Select(item => item.Title).ToArray());
            Assert.IsNull(view.Groups);
        }

        [TestMethod]
        public void RequestedReviews_AppearInMyWorkAttentionAndKeepProviderOrderInPrInventory()
        {
            var now = DateTime.UtcNow;
            var prs = new[]
            {
                new DevHubPullRequest { Title = "GitHub review", Status = "open", IsReviewRequestedForCurrentUser = true, WebUrl = "https://github.com/o/r/pull/1", RepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r.git"), UpdatedAt = now.AddHours(-1) },
                new DevHubPullRequest { Title = "ADO review", Status = "open", IsReviewRequestedForCurrentUser = true, WebUrl = "https://dev.azure.com/o/p/_git/r/pullrequest/1", RepoIdentifier = RemoteRepoIdentifier.TryParse("https://dev.azure.com/o/p/_git/r"), UpdatedAt = now.AddHours(-2) },
                new DevHubPullRequest { Title = "Passive update", Status = "open", WebUrl = "https://github.com/o/r/pull/2", RepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r.git"), UpdatedAt = now },
            };
            var incoming = DevHubWorkItem.GetIncoming(prs, [], [], DevHubSortOrder.AzureDevOpsFirst);
            var own = DevHubWorkItem.GetMyWork(prs, [], [], DevHubSortOrder.AzureDevOpsFirst);

            CollectionAssert.AreEqual(new[] { "GitHub review", "ADO review" },
                DevHubWorkItem.Filter(own, DevHubWorkItemKind.All, true).Select(item => item.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "ADO review", "GitHub review" },
                DevHubWorkItem.Filter(own, DevHubWorkItemKind.PullRequest, false).Select(item => item.Title).ToArray());
            CollectionAssert.AreEqual(new[] { "Passive update" }, incoming.Select(item => item.Title).ToArray());
        }

        [STATestMethod]
        [DataRow(0, (int)DevHubWorkItemKind.Copilot)]
        [DataRow(15, (int)DevHubWorkItemKind.Copilot)]
        [DataRow(14, (int)DevHubWorkItemKind.Copilot)]
        [DataRow(12, (int)DevHubWorkItemKind.Copilot)]
        [DataRow(8, (int)DevHubWorkItemKind.Copilot)]
        [DataRow(5, (int)DevHubWorkItemKind.Copilot)]
        [DataRow(10, (int)DevHubWorkItemKind.Copilot)]
        public void InitialMyWorkTab_DefaultsToCopilotRegardlessOfOtherAvailableTabs(int available, int expected)
        {
            var inventory = DevHubWorkItem.GetMyWork(
                [new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, WebUrl = "https://example.com/pr/1" }],
                [new DevHubIssue { IsAssignedToCurrentUser = true, State = "open", WebUrl = "https://example.com/issues/1" }],
                [new CopilotActivitySession { Id = "session", IsResponding = true }],
                builds: [new DevHubCiRun { IsTriggeredByCurrentUser = true, WebUrl = "https://example.com/build/1" }]);
            var items = inventory.Where(item => (available & (item.Kind switch
            {
                DevHubWorkItemKind.Copilot => 1,
                DevHubWorkItemKind.PullRequest => 2,
                DevHubWorkItemKind.Issue => 4,
                DevHubWorkItemKind.Build => 8,
                _ => 0,
            })) != 0).ToArray();

            Assert.IsTrue(items.All(item => !item.NeedsAttention));
            Assert.AreEqual((DevHubWorkItemKind)expected, DevHubActivityViewModel.GetInitialMyWorkKind(items));
        }

        [STATestMethod]
        public void InitialMyWorkTab_KeepsAttentionWhenAnActionableItemExists()
        {
            var items = DevHubWorkItem.GetMyWork(
                [new DevHubPullRequest { Status = "open", IsReviewRequestedForCurrentUser = true,
                    WebUrl = "https://example.com/pr/1", UpdatedAt = DateTime.UtcNow }], [],
                [new CopilotActivitySession { Id = "session", IsResponding = true }]);

            Assert.AreEqual(DevHubWorkItemKind.All, DevHubActivityViewModel.GetInitialMyWorkKind(items));
        }

        [STATestMethod]
        public void WorkViews_DisplayCardsWithoutSectionHeadingsOrLosingItems()
        {
            var prs = new[]
            {
                new DevHubPullRequest { Status = "open", IsAuthoredByCurrentUser = true, WebUrl = "https://example.com/1" }
            };
            var issues = new[]
            {
                new DevHubIssue { IsAssignedToCurrentUser = true, State = "open", WebUrl = "https://example.com/2" }
            };
            var sessions = new[] { new CopilotActivitySession { Id = "session", PendingQuestions = 1 } };
            var own = DevHubWorkItem.GetMyWork(prs, issues, sessions);
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, false);
            activity.UpdateRepositoryActivity(prs, issues, [], sessions, DevHubSortOrder.MostRecent, false);
            var view = CollectionViewSource.GetDefaultView(activity.MyWorkItems);

            Assert.IsNull(view.Groups);
            CollectionAssert.AreEqual(activity.MyWorkItems.ToArray(), view.Cast<DevHubWorkItem>().ToArray());
            CollectionAssert.AreEqual(own.Select(item => item.Title).ToArray(), view.Cast<DevHubWorkItem>().Select(item => item.Title).ToArray());
            Assert.AreEqual(own.Count, view.Cast<DevHubWorkItem>().Count());
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