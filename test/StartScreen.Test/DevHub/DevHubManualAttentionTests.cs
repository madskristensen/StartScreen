using StartScreen.Models.DevHub;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubManualAttentionTests
    {
        [TestMethod]
        public void PinningTeamWork_AddsToAttentionAndUnpinningRemovesItWithoutChangingOwnership()
        {
            var now = DateTime.UtcNow;
            var pr = new DevHubPullRequest
            {
                Title = "Review later", WebUrl = "https://github.com/o/r/pull/42", Status = "open", UpdatedAt = now,
            };
            var preferences = new DevHubItemPreferences();
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            activity.UpdateRepositoryActivity([pr], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsEmpty(activity.MyWorkItems);
            var teamCard = activity.IncomingItems.Single();
            preferences.SetPinned(teamCard, true, now);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);

            Assert.AreEqual(pr.WebUrl, activity.MyWorkItems.Single().WebUrl);
            Assert.AreSame(activity.MyWorkItems.Single(), activity.IncomingItems.Single());
            Assert.AreEqual(1, activity.AttentionCount);
            Assert.AreEqual(0, activity.MyWorkCount);
            Assert.AreEqual(1, activity.IncomingCount);
            Assert.IsFalse(activity.MyWorkItems.Single().NeedsAttention);
            Assert.IsFalse(pr.IsAuthoredByCurrentUser);
            Assert.IsFalse(pr.IsAssignedToCurrentUser);
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            Assert.IsEmpty(activity.MyWorkItems);

            preferences.SetPinned(teamCard, false, now);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            Assert.IsEmpty(activity.MyWorkItems);
            Assert.AreEqual(0, activity.AttentionCount);
            Assert.AreEqual(pr.WebUrl, activity.IncomingItems.Single().WebUrl);
        }

        [TestMethod]
        public void UnpinningAnAutomaticallyActionableItem_DoesNotRemoveItsAction()
        {
            var now = DateTime.UtcNow;
            var pr = new DevHubPullRequest
            {
                Title = "Failing checks", WebUrl = "https://github.com/o/r/pull/1", Status = "open",
                IsAuthoredByCurrentUser = true, CiStatus = "failure", UpdatedAt = now,
            };
            var preferences = new DevHubItemPreferences();
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            activity.UpdateRepositoryActivity([pr], [], [], [], DevHubSortOrder.MostRecent, false);
            preferences.SetPinned(activity.MyWorkItems.Single(), true, now);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.AreEqual(1, activity.AttentionCount);
            Assert.IsTrue(activity.MyWorkItems.Single().IsPinned);

            preferences.SetPinned(activity.MyWorkItems.Single(), false, now);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);

            Assert.AreEqual(1, activity.AttentionCount);
            Assert.IsTrue(activity.MyWorkItems.Single().NeedsAttention);
            Assert.IsFalse(activity.MyWorkItems.Single().IsPinned);
            Assert.AreEqual("Checks failing", activity.MyWorkItems.Single().AttentionReason);
        }

        [TestMethod]
        public void SnoozeAndScope_ApplyToManuallyPinnedAttentionItems()
        {
            var now = DateTime.UtcNow;
            var issue = new DevHubIssue { Title = "Team task", WebUrl = "https://github.com/o/r/issues/3", State = "open" };
            var item = DevHubWorkItem.GetIncoming([], [issue], []).Single();
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(item, true, now);
            preferences.Snooze(item, now.AddHours(1), now);
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            activity.UpdateRepositoryActivity([], [issue], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsEmpty(activity.MyWorkItems);
            Assert.AreEqual(0, activity.AttentionCount);

            now = now.AddHours(1);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);
            Assert.AreEqual(issue.WebUrl, activity.MyWorkItems.Single().WebUrl);
            Assert.AreEqual(1, activity.AttentionCount);
            activity.UpdateRepositoryActivity([], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsEmpty(activity.MyWorkItems);
            Assert.AreEqual(0, activity.AttentionCount);
        }

        [TestMethod]
        public void CompletedPinnedItems_AreExcludedFromAttention()
        {
            var now = DateTime.UtcNow;
            var issue = new DevHubIssue
            {
                Title = "Completed", WebUrl = "https://github.com/o/r/issues/3", State = "closed", IsClosed = true,
                IsAssignedToCurrentUser = true, UpdatedAt = now,
            };
            var item = DevHubWorkItem.GetMyWork([], [issue], [], includeCompleted: true).Single();
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(item, true, now);
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            activity.UpdateRepositoryActivity([], [issue], [], [], DevHubSortOrder.MostRecent, true);

            Assert.IsEmpty(activity.MyWorkItems);
            Assert.AreEqual(0, activity.AttentionCount);
            Assert.AreEqual(1, activity.MyWorkCount);
            Assert.AreEqual(DevHubWorkItemKind.Copilot, DevHubActivityViewModel.GetInitialMyWorkKind([item]));
        }

        [TestMethod]
        public void ManualPins_CanChooseTheInitialAttentionView()
        {
            var now = DateTime.UtcNow;
            var build = new DevHubCiRun { Name = "Build", Status = "success", WebUrl = "https://example.com/build/1" };
            var item = DevHubWorkItem.GetIncoming([], [], [build]).Single();
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(item, true, now);
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.UpdateRepositoryActivity([], [], [build], [], DevHubSortOrder.MostRecent, false);

            Assert.IsTrue(activity.TrySelectInitialFilter());
            Assert.IsTrue(activity.MyWorkAttention);
            Assert.AreEqual(build.WebUrl, activity.MyWorkItems.Single().WebUrl);
            Assert.AreEqual(1, activity.AttentionCount);
            Assert.IsFalse(activity.TrySelectInitialFilter());
        }
    }
}
