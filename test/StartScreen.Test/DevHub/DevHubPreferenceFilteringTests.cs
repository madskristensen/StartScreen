using System.Collections.Specialized;
using StartScreen.Models;
using StartScreen.Models.DevHub;
using StartScreen.ToolWindows.Controls;

namespace StartScreen.Test.DevHub
{
    [TestClass]
    public class DevHubPreferenceFilteringTests
    {
        [TestMethod]
        public void PinnedItems_DoNotBypassKindOwnershipOrCompletionFilters()
        {
            var now = DateTime.UtcNow;
            var own = new DevHubPullRequest
            {
                Title = "Own", WebUrl = "https://github.com/o/r/pull/1", Status = "open", IsAuthoredByCurrentUser = true,
            };
            var team = new DevHubIssue { Title = "Team", WebUrl = "https://github.com/o/r/issues/2" };
            var completed = new DevHubIssue
            {
                Title = "Completed", WebUrl = "https://github.com/o/r/issues/3", State = "closed", IsClosed = true,
                IsAssignedToCurrentUser = true, UpdatedAt = now,
            };
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(DevHubWorkItem.GetMyWork([own], [], []).Single(), true, now);
            preferences.SetPinned(DevHubWorkItem.GetIncoming([], [team], []).Single(), true, now);
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            activity.SetIncomingFilter(DevHubWorkItemKind.Issue);
            activity.UpdateRepositoryActivity([own], [team, completed], [], [], DevHubSortOrder.MostRecent, false);

            Assert.IsEmpty(activity.MyWorkItems);
            Assert.AreEqual("Team", activity.IncomingItems.Single().Title);
            activity.SetMyWorkFilter(DevHubWorkItemKind.PullRequest, false);
            Assert.AreEqual("Own", activity.MyWorkItems.Single().Title);
            Assert.IsTrue(activity.MyWorkItems.Single().IsPinned);
            activity.UpdateRepositoryActivity([], [], [], [], DevHubSortOrder.MostRecent, false);
            Assert.IsEmpty(activity.MyWorkItems);
            Assert.IsEmpty(activity.IncomingItems);
        }

        [TestMethod]
        public void StablePreferenceRefresh_PreservesCardsAndCollections()
        {
            var now = DateTime.UtcNow;
            var issue = new DevHubIssue { Title = "Task", WebUrl = "https://github.com/o/r/issues/1", IsAssignedToCurrentUser = true };
            var preferences = new DevHubItemPreferences();
            preferences.SetPinned(DevHubWorkItem.GetMyWork([], [issue], []).Single(), true, now);
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(preferences, false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.Issue, false);
            activity.UpdateRepositoryActivity([], [issue], [], [], DevHubSortOrder.MostRecent, false);
            var item = activity.MyWorkItems.Single();
            var changes = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)activity.MyWorkItems).CollectionChanged += (_, e) => changes.Add(e.Action);

            activity.SetPreferences(DevHubItemPreferences.FromJson(preferences.ToJson(now)), false);
            activity.Refresh([], DevHubSortOrder.MostRecent, false);

            Assert.AreSame(item, activity.MyWorkItems.Single());
            Assert.IsEmpty(changes);
        }

        [TestMethod]
        public void Snooze_DoesNotHideOrPinCopilotPermissionRequests()
        {
            var now = DateTime.UtcNow;
            var session = new CopilotActivitySession { Id = "session", PendingPermissions = 1, LastActivity = now };
            var activity = new DevHubActivityViewModel(() => now);
            activity.SetPreferences(new DevHubItemPreferences(), false);
            activity.SetMyWorkFilter(DevHubWorkItemKind.All, true);
            activity.UpdateRepositoryActivity([], [], [], [session], DevHubSortOrder.MostRecent, false);

            Assert.AreEqual("session", activity.MyWorkItems.Single().CopilotSessionId);
            Assert.IsFalse(activity.MyWorkItems.Single().IsPinned);
            Assert.IsFalse(activity.MyWorkItems.Single().IsSnoozed);
            Assert.AreEqual(1, activity.AttentionCount);
        }
    }
}
