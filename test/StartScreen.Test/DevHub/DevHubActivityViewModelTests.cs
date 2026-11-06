using System.Collections.Specialized;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubActivityViewModelTests
    {
        [TestMethod]
        public void Inventories_KeepOwnershipAttentionAndCountsSeparateFromFilters()
        {
            var activity = new DevHubActivityViewModel();
            var own = PullRequest("own", true);
            own.CiStatus = "failure";
            var review = PullRequest("review", false);
            review.IsReviewRequestedForCurrentUser = true;
            var team = PullRequest("team", false);
            var issue = new DevHubIssue { Title = "Task", WebUrl = "https://example.com/issues/1", IsAssignedToCurrentUser = true };
            var build = new DevHubCiRun { Name = "Build", Status = "failure", WebUrl = "https://example.com/build/1", IsTriggeredByCurrentUser = true };
            var waiting = new CopilotActivitySession { Id = "waiting", PendingQuestions = 1, LastActivity = DateTimeOffset.UtcNow };
            activity.UpdateRepositoryActivity([own, review, team], [issue], [build], [waiting], DevHubSortOrder.MostRecent, false);

            Assert.AreEqual(5, activity.MyWorkCount);
            Assert.AreEqual(1, activity.IncomingCount);
            Assert.AreEqual(3, activity.AttentionCount);
            Assert.AreEqual("Attention (3)", activity.AttentionFilterText);
            Assert.AreEqual(" 5", activity.MyWorkCountText);
            Assert.AreEqual(" 1", activity.IncomingCountText);
            Assert.AreEqual(5, activity.LoadedActivityCount);
            Assert.IsTrue(activity.MyWorkItems.All(item => item.NeedsAttention));
            CollectionAssert.AreEquivalent(new[] { "own", "review", "waiting" },
                activity.MyWorkItems.Select(item => item.CopilotSessionId ?? item.Title).ToArray());
            Assert.AreEqual("team", activity.IncomingItems.Single().Title);

            activity.SetMyWorkFilter(DevHubWorkItemKind.Build, false);
            Assert.AreEqual("Build", activity.MyWorkItems.Single().Title);
            Assert.IsFalse(activity.MyWorkItems.Single().NeedsAttention);
            Assert.AreEqual(5, activity.MyWorkCount);
            Assert.AreEqual(3, activity.AttentionCount);
        }

        [TestMethod]
        public void EmptyPresentation_TracksFilteredResultsAndRaisesOnlyChangedValues()
        {
            var activity = new DevHubActivityViewModel();
            var notifications = new List<string?>();
            activity.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            Assert.IsTrue(activity.IsMyWorkEmpty);
            Assert.IsTrue(activity.IsIncomingEmpty);
            Assert.AreEqual(string.Empty, activity.MyWorkCountText);
            Assert.AreEqual(string.Empty, activity.IncomingCountText);
            Assert.AreEqual("No items need your attention in this view.", activity.MyWorkEmptyText);
            Assert.AreEqual("No matching team activity.", activity.IncomingEmptyText);

            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            activity.UpdateRepositoryActivity([PullRequest("own", true)], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsTrue(activity.IsMyWorkEmpty);
            Assert.AreEqual(" 1", activity.MyWorkCountText);
            Assert.AreEqual("No matching personal work in the loaded activity.", activity.MyWorkEmptyText);
            CollectionAssert.Contains(notifications, nameof(DevHubActivityViewModel.MyWorkCountText));

            notifications.Clear();
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            Assert.IsFalse(activity.IsMyWorkEmpty);
            CollectionAssert.Contains(notifications, nameof(DevHubActivityViewModel.IsMyWorkEmpty));
            notifications.Clear();
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            Assert.IsEmpty(notifications);
        }

        [TestMethod]
        public void EquivalentRefresh_PreservesCollectionsCardsAndEmitsNoCollectionChanges()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            activity.UpdateRepositoryActivity([PullRequest("own", true), PullRequest("team", false)], [], [], [], DevHubSortOrder.MostRecent, false);
            var myWork = activity.MyWorkItems;
            var incoming = activity.IncomingItems;
            var own = myWork.Single();
            var team = incoming.Single();
            var changes = Observe(activity);

            activity.UpdateRepositoryActivity([PullRequest("own", true), PullRequest("team", false)], [], [], [], DevHubSortOrder.MostRecent, false);

            Assert.AreSame(myWork, activity.MyWorkItems);
            Assert.AreSame(incoming, activity.IncomingItems);
            Assert.AreSame(own, activity.MyWorkItems.Single());
            Assert.AreSame(team, activity.IncomingItems.Single());
            Assert.IsEmpty(changes);
        }

        [TestMethod]
        public void RepeatedIdentities_PreserveMultiplicityWithoutInvalidMovesOrResets()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            var pr = PullRequest("duplicate", true);
            activity.UpdateRepositoryActivity([pr, pr], [], [], [], DevHubSortOrder.MostRecent, false);
            var changes = Observe(activity);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.HasCount(2, activity.MyWorkItems);
            Assert.IsTrue(activity.MyWorkItems.All(item => item.Title == "duplicate"));
            changes.Clear();
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.IsEmpty(changes);

            activity.UpdateRepositoryActivity([pr], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.HasCount(1, activity.MyWorkItems);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        }

        [TestMethod]
        public void ChangedCardAndLoadMore_PreserveOtherCardsWithoutResettingEitherCollection()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            var own = PullRequest("own", true);
            var unchanged = PullRequest("unchanged", true);
            activity.UpdateRepositoryActivity([own, unchanged, PullRequest("team", false)], [], [], [], DevHubSortOrder.MostRecent, false);
            var original = activity.MyWorkItems[0];
            var retained = activity.MyWorkItems[1];
            var team = activity.IncomingItems.Single();
            var changes = Observe(activity);
            own.Title = "Updated title";
            own.CiStatus = "failure";
            activity.UpdateRepositoryActivity([own, unchanged, PullRequest("team", false), PullRequest("loaded", true)], [], [], [], DevHubSortOrder.MostRecent, false);

            Assert.AreNotSame(original, activity.MyWorkItems[0]);
            Assert.AreEqual("Updated title", activity.MyWorkItems[0].Title);
            Assert.AreEqual("Checks failing", activity.MyWorkItems[0].AttentionReason);
            Assert.AreSame(retained, activity.MyWorkItems[1]);
            Assert.AreSame(team, activity.IncomingItems.Single());
            Assert.AreEqual("loaded", activity.MyWorkItems[2].Title);
            Assert.AreEqual(4, activity.LoadedActivityCount);
            Assert.IsNotEmpty(changes);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        }

        [TestMethod]
        public void Filters_AreIndependentAndReuseHiddenCardsWhenRestored()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            activity.UpdateRepositoryActivity([PullRequest("own", true), PullRequest("team", false)],
                [new DevHubIssue { Title = "Team task", WebUrl = "https://example.com/issues/1" }], [], [], DevHubSortOrder.MostRecent, false);
            var own = activity.MyWorkItems.Single();
            var team = activity.IncomingItems.Single();
            var myWorkChanges = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)activity.MyWorkItems).CollectionChanged += (_, e) => myWorkChanges.Add(e.Action);

            activity.SetIncomingFilter(DevHubWorkItemKind.Issue);
            Assert.AreEqual("Team task", activity.IncomingItems.Single().Title);
            Assert.AreSame(own, activity.MyWorkItems.Single());
            Assert.IsEmpty(myWorkChanges);
            activity.SetMyWorkFilter(DevHubWorkItemKind.Copilot, false);
            Assert.IsTrue(activity.IsMyWorkEmpty);
            Assert.AreEqual(1, activity.MyWorkCount);
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            activity.SetIncomingFilter(DevHubWorkItemKind.PullRequest);
            Assert.AreSame(own, activity.MyWorkItems.Single());
            Assert.AreSame(team, activity.IncomingItems.Single());
        }

        [TestMethod]
        public void ProviderSortChange_MovesExistingCardsWithoutReset()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            var github = PullRequest("github", true);
            github.RepoIdentifier = RemoteRepoIdentifier.TryParse("https://github.com/o/r.git");
            github.UpdatedAt = DateTime.UtcNow;
            var ado = PullRequest("ado", true);
            ado.RepoIdentifier = RemoteRepoIdentifier.TryParse("https://dev.azure.com/org/project/_git/repo");
            ado.UpdatedAt = github.UpdatedAt.AddMinutes(-1);
            activity.UpdateRepositoryActivity([github, ado], [], [], [], DevHubSortOrder.MostRecent, false);
            var first = activity.MyWorkItems[0];
            var second = activity.MyWorkItems[1];
            var changes = Observe(activity);

            activity.Refresh([], DevHubSortOrder.AzureDevOpsFirst, false);

            Assert.AreSame(second, activity.MyWorkItems[0]);
            Assert.AreSame(first, activity.MyWorkItems[1]);
            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Move }, changes);
        }

        [TestMethod]
        public void WorkspaceReplacement_AndCopilotRefreshNeverReintroduceOldRepositoryItems()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, false);
            var session = new CopilotActivitySession { Id = "local", Title = "Local session", LastActivity = DateTimeOffset.UtcNow };
            activity.UpdateRepositoryActivity([PullRequest("old", true)], [], [], [session], DevHubSortOrder.MostRecent, false);
            var copilot = activity.MyWorkItems.Single(item => item.IsCopilot);
            activity.UpdateRepositoryActivity([PullRequest("new", true)], [], [], [session], DevHubSortOrder.MostRecent, false);
            Assert.AreSame(copilot, activity.MyWorkItems.Single(item => item.IsCopilot));
            Assert.IsFalse(activity.MyWorkItems.Any(item => item.Title == "old"));

            session.PendingPermissions = 1;
            activity.Refresh([session], DevHubSortOrder.MostRecent, false);
            Assert.AreEqual("Waiting", activity.MyWorkItems.Single(item => item.IsCopilot).CopilotStatus);
            Assert.AreEqual(1, activity.AttentionCount);
            Assert.IsFalse(activity.MyWorkItems.Any(item => item.Title == "old"));
            activity.UpdateRepositoryActivity([], [], [], [session], DevHubSortOrder.MostRecent, false);
            Assert.AreEqual("local", activity.MyWorkItems.Single().CopilotSessionId);
            Assert.AreEqual(0, activity.LoadedActivityCount);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.IsTrue(activity.IsMyWorkEmpty);
        }

        [TestMethod]
        public void IncludeCompleted_RefreshesPersonalProjectionWithoutChangingTeamOrActiveCards()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, false);
            var completed = PullRequest("completed", true);
            completed.Status = "closed";
            completed.IsMerged = true;
            completed.UpdatedAt = DateTime.UtcNow.AddDays(-1);
            activity.UpdateRepositoryActivity([PullRequest("active", true), PullRequest("team", false), completed], [], [], [], DevHubSortOrder.MostRecent, false);
            var active = activity.MyWorkItems.Single();
            var team = activity.IncomingItems.Single();
            activity.Refresh([], DevHubSortOrder.MostRecent, true);

            Assert.AreSame(active, activity.MyWorkItems[0]);
            Assert.AreSame(team, activity.IncomingItems.Single());
            Assert.AreEqual("Recently completed", activity.MyWorkItems[1].Section);
            Assert.IsTrue(activity.MyWorkItems[1].IsCompleted);
            Assert.IsFalse(activity.MyWorkItems[1].NeedsAttention);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.AreSame(active, activity.MyWorkItems.Single());
        }

        [TestMethod]
        public void MutableLabels_UpdateCardEvenWhenProviderReusesLabelInstances()
        {
            var activity = new DevHubActivityViewModel();
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            var issue = new DevHubIssue
            {
                Title = "Task", WebUrl = "https://example.com/issues/1", IsAssignedToCurrentUser = true,
                Labels = [new DevHubLabel { Name = "bug", Color = "000000" }],
            };
            activity.UpdateRepositoryActivity([], [issue], [], [], DevHubSortOrder.MostRecent, false);
            var original = activity.MyWorkItems.Single();
            issue.Labels[0].Color = "ffffff";
            activity.Refresh([], DevHubSortOrder.MostRecent, false);

            Assert.AreNotSame(original, activity.MyWorkItems.Single());
            Assert.AreEqual("ffffff", activity.MyWorkItems.Single().Labels.Single().Color);
        }

        [TestMethod]
        public void InitialFilter_WaitsForLoadingAndHonorsExplicitChoiceAcrossRefreshes()
        {
            var activity = new DevHubActivityViewModel();
            Assert.IsFalse(activity.TrySelectInitialFilter(false));
            activity.UpdateRepositoryActivity([PullRequest("own", true)], [], [],
                [new CopilotActivitySession { Id = "local", IsResponding = true }], DevHubSortOrder.MostRecent, false);
            Assert.IsFalse(activity.TrySelectInitialFilter(true));
            Assert.IsTrue(activity.TrySelectInitialFilter(false, true));
            Assert.AreEqual(DevHubWorkItemKind.Copilot, activity.MyWorkKind);
            Assert.IsFalse(activity.MyWorkAttention);
            Assert.IsFalse(activity.TrySelectInitialFilter(false));

            activity.BeginVisit();
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            Assert.IsFalse(activity.TrySelectInitialFilter(false, true));
            Assert.AreEqual(DevHubWorkItemKind.Issue, activity.MyWorkKind);
            activity.BeginVisit();
            activity.CancelInitialFilterSelection();
            Assert.IsFalse(activity.TrySelectInitialFilter(false, true));
        }

        [TestMethod]
        public void InitialFilter_KeepsAttentionForKnownActionsAndCompletesEmptyVisitsOnce()
        {
            var activity = new DevHubActivityViewModel();
            var pr = PullRequest("review", false);
            pr.IsReviewRequestedForCurrentUser = true;
            activity.UpdateRepositoryActivity([pr], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsTrue(activity.TrySelectInitialFilter(false));
            Assert.IsTrue(activity.MyWorkAttention);
            Assert.AreEqual(DevHubWorkItemKind.All, activity.MyWorkKind);

            activity = new DevHubActivityViewModel();
            Assert.IsTrue(activity.TrySelectInitialFilter(false, true));
            Assert.IsFalse(activity.TrySelectInitialFilter(false, true));
            Assert.IsTrue(activity.MyWorkAttention);
        }

        private static DevHubPullRequest PullRequest(string title, bool own)
        {
            return new DevHubPullRequest
            {
                Title = title, WebUrl = "https://example.com/pr/" + title, Status = "open",
                IsAuthoredByCurrentUser = own, UpdatedAt = DateTime.UtcNow.Date,
            };
        }

        private static List<NotifyCollectionChangedAction> Observe(DevHubActivityViewModel activity)
        {
            var changes = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)activity.MyWorkItems).CollectionChanged += (_, e) => changes.Add(e.Action);
            ((INotifyCollectionChanged)activity.IncomingItems).CollectionChanged += (_, e) => changes.Add(e.Action);
            return changes;
        }
    }
}